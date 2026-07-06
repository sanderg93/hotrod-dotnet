using System.Collections.Generic;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// A client-side transaction over a single transactional cache. Reads and writes are buffered locally
/// so the transaction sees its own uncommitted changes and a consistent snapshot of the keys it has
/// read; nothing is applied to the cache until <see cref="CommitAsync"/>. On commit the buffered write
/// set is staged on the server under this transaction's <see cref="Xid"/> with a PREPARE_TX request and,
/// if the server validates it, completed with COMMIT_TX — so the writes take effect atomically. A read
/// records the entry's version, which the server rechecks at prepare time, so a concurrent change to a
/// key this transaction read makes the commit fail rather than overwrite it.
/// <para>
/// Obtain one from <see cref="RemoteCache.BeginTransaction"/> or
/// <see cref="TransactionManager.Begin"/>. A transaction is not safe for concurrent use; drive it from a
/// single asynchronous flow. Dispose it to release it: an uncommitted transaction is rolled back
/// (its buffered writes are discarded and never reach the server).
/// </para>
/// </summary>
public sealed class HotRodTransaction : IAsyncDisposable
{
    private enum State { Active, Committed, RolledBack }

    private readonly HotRodClient _client;
    private readonly string _cacheName;
    private readonly CacheMarshaller _marshaller;
    private readonly DataFormat _dataFormat;
    private readonly TransactionXid _xid;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<byte[], Entry> _entries = new(ByteArrayComparer.Instance);

    private State _state = State.Active;

    internal HotRodTransaction(HotRodClient client, string cacheName, CacheMarshaller marshaller, TransactionXid xid, TimeSpan timeout)
    {
        _client = client;
        _cacheName = cacheName;
        _marshaller = marshaller;
        _dataFormat = marshaller.DataFormat;
        _xid = xid;
        _timeout = timeout;
    }

    /// <summary>The XID identifying this transaction on the server.</summary>
    public TransactionXid Xid => _xid;

    // -- Buffered operations (byte-array overloads) -------------------------

    /// <summary>
    /// Returns the value this transaction sees for <paramref name="key"/>, or null if absent. A value
    /// written earlier in the transaction is returned without a server round trip; otherwise the entry is
    /// read from the server once and its version tracked for commit-time validation.
    /// </summary>
    public async ValueTask<byte[]?> GetAsync(byte[] key, CancellationToken ct = default)
    {
        EnsureActive();
        Entry entry = await ResolveAsync(key, ct);
        return entry.Value;
    }

    /// <summary>Buffers a write of <paramref name="value"/> to <paramref name="key"/>; applied on commit.</summary>
    public ValueTask PutAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureActive();
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            _entries[key] = new Entry { ReadControl = TransactionCodec.ControlNotRead, Value = value, Expiration = expiration, Modified = true };
            return ValueTask.CompletedTask;
        }
        entry.Value = value;
        entry.Expiration = expiration;
        entry.Modified = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Buffers a removal of <paramref name="key"/>; applied on commit. Returns true if the transaction saw a value there.</summary>
    public ValueTask<bool> RemoveAsync(byte[] key, CancellationToken ct = default)
    {
        EnsureActive();
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            _entries[key] = new Entry { ReadControl = TransactionCodec.ControlNotRead, Value = null, Modified = true };
            return ValueTask.FromResult(false); // the key was not read, so prior existence is unknown to the transaction
        }
        bool existed = entry.Value is not null;
        entry.Value = null;
        entry.Modified = true;
        return ValueTask.FromResult(existed);
    }

    /// <summary>Returns true if this transaction sees a value for <paramref name="key"/>.</summary>
    public async ValueTask<bool> ContainsKeyAsync(byte[] key, CancellationToken ct = default)
    {
        EnsureActive();
        Entry entry = await ResolveAsync(key, ct);
        return entry.Value is not null;
    }

    // -- String convenience overloads (via the cache's encoding) ------------

    /// <summary>String-keyed overload of <see cref="PutAsync(byte[], byte[], Expiration, CancellationToken)"/>.</summary>
    public ValueTask PutAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        PutAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct);

    /// <summary>String-keyed overload of <see cref="GetAsync(byte[], CancellationToken)"/>.</summary>
    public async ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
    {
        byte[]? value = await GetAsync(_marshaller.Marshal(key), ct);
        return value is null ? null : _marshaller.Unmarshal(value);
    }

    /// <summary>String-keyed overload of <see cref="RemoveAsync(byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) =>
        RemoveAsync(_marshaller.Marshal(key), ct);

    /// <summary>String-keyed overload of <see cref="ContainsKeyAsync(byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> ContainsKeyAsync(string key, CancellationToken ct = default) =>
        ContainsKeyAsync(_marshaller.Marshal(key), ct);

    // -- Completion ---------------------------------------------------------

    /// <summary>
    /// Commits the transaction: stages the buffered write set on the server under <see cref="Xid"/> with
    /// PREPARE_TX, then completes it with COMMIT_TX so every write applies atomically. A transaction that
    /// wrote nothing commits as read-only without contacting the server. If the server rejects the
    /// prepare — because a key this transaction read or wrote changed underneath it — this throws a
    /// <see cref="HotRodTransactionConflictException"/> and nothing is applied.
    /// </summary>
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        EnsureActive();
        List<TransactionModification> modifications = BuildModifications();
        if (modifications.Count == 0)
        {
            _state = State.Committed; // read-only: nothing to stage or commit
            return;
        }

        bool prepared = await PrepareAsync(modifications, ct);
        if (!prepared)
        {
            _state = State.RolledBack; // a failed prepare stages nothing on the server
            throw new HotRodTransactionConflictException();
        }

        try
        {
            await CompleteAsync(Constants.CommitTransactionRequest, ct);
        }
        catch
        {
            await TryRollbackServerAsync(); // abort the prepared transaction so it does not linger
            _state = State.RolledBack;
            throw;
        }

        await TryForgetServerAsync();
        _state = State.Committed;
    }

    /// <summary>
    /// Rolls the transaction back, discarding its buffered writes. Nothing is staged on the server before
    /// commit, so this makes no server request; it simply ends the transaction.
    /// </summary>
    public ValueTask RollbackAsync(CancellationToken ct = default)
    {
        EnsureActive();
        _entries.Clear();
        _state = State.RolledBack;
        return ValueTask.CompletedTask;
    }

    /// <summary>Rolls back an uncommitted transaction; a committed or already-rolled-back one is left as is.</summary>
    public ValueTask DisposeAsync()
    {
        if (_state == State.Active)
        {
            _entries.Clear();
            _state = State.RolledBack;
        }
        return ValueTask.CompletedTask;
    }

    // -- Internals ----------------------------------------------------------

    /// <summary>Returns the transaction's entry for a key, reading it from the server once on first touch.</summary>
    private async ValueTask<Entry> ResolveAsync(byte[] key, CancellationToken ct)
    {
        if (_entries.TryGetValue(key, out Entry? existing))
            return existing;

        Entry entry = await ReadFromServerAsync(key, ct);
        _entries[key] = entry;
        return entry;
    }

    /// <summary>Reads a key with its metadata and turns it into a read entry (present with a version, or non-existing).</summary>
    private ValueTask<Entry> ReadFromServerAsync(byte[] key, CancellationToken ct) =>
        _client.ExecuteAsync(_cacheName, Constants.GetWithMetadataRequest, flags: 0, key, _dataFormat,
            w => HotRodCodec.WriteArray(w, key),
            async (status, reader, c) =>
            {
                if (ResponseStatus.KeyDoesNotExist(status))
                    return new Entry { ReadControl = TransactionCodec.ControlNonExisting, Value = null };
                EntryMetadata meta = await HotRodCodec.ReadMetadataAsync(reader, c);
                byte[] value = await HotRodCodec.ReadArrayAsync(reader, c);
                return new Entry { ReadControl = 0, Version = meta.Version, Value = value };
            }, ct);

    /// <summary>Turns the modified entries into the modification set streamed in a PREPARE_TX body.</summary>
    internal List<TransactionModification> BuildModifications()
    {
        var modifications = new List<TransactionModification>(_entries.Count);
        foreach (KeyValuePair<byte[], Entry> pair in _entries)
        {
            Entry entry = pair.Value;
            if (!entry.Modified)
                continue;

            byte control = entry.ReadControl;
            if (entry.Value is null)
                control |= TransactionCodec.ControlRemoveOp;

            modifications.Add(new TransactionModification
            {
                Key = pair.Key,
                Control = control,
                VersionRead = entry.Version,
                Value = entry.Value,
                Expiration = entry.Expiration,
            });
        }
        return modifications;
    }

    /// <summary>
    /// Sends PREPARE_TX and reports whether the server accepted it. A success status carries the XA
    /// return code (consumed to keep the stream in sync); a not-executed status signals a validation
    /// conflict and carries no body.
    /// </summary>
    private ValueTask<bool> PrepareAsync(IReadOnlyList<TransactionModification> modifications, CancellationToken ct) =>
        _client.ExecuteAsync(_cacheName, Constants.PrepareTransactionRequest, flags: 0, routingKey: null, _dataFormat,
            w => TransactionCodec.WritePrepareBody(w, _xid, onePhaseCommit: false, recoverable: false,
                (long)_timeout.TotalMilliseconds, modifications),
            async (status, reader, c) =>
            {
                if (status != Constants.StatusSuccess)
                    return false; // not-executed: a read/write conflict was detected during validation
                await TransactionCodec.ReadXaReturnCodeAsync(reader, c);
                return true;
            }, ct);

    /// <summary>Sends a COMMIT_TX or ROLLBACK_TX request (global to the XID; no cache name) and consumes the return code.</summary>
    private async ValueTask CompleteAsync(byte opcode, CancellationToken ct) =>
        await _client.ExecuteAsync(string.Empty, opcode, flags: 0, routingKey: null, DataFormat.None,
            w => TransactionCodec.WriteXid(w, _xid),
            async (status, reader, c) =>
            {
                if (status == Constants.StatusSuccess)
                    await TransactionCodec.ReadXaReturnCodeAsync(reader, c);
                return true;
            }, ct);

    /// <summary>Best-effort ROLLBACK_TX used to abort a prepared transaction after a commit failure; errors are swallowed.</summary>
    private async ValueTask TryRollbackServerAsync()
    {
        try { await CompleteAsync(Constants.RollbackTransactionRequest, CancellationToken.None); }
        catch { /* the reaper discards a prepared-but-uncompleted transaction after its timeout */ }
    }

    /// <summary>Best-effort FORGET_TX that discards the server-side record of a completed transaction; errors are swallowed.</summary>
    private async ValueTask TryForgetServerAsync()
    {
        try
        {
            await _client.ExecuteAsync(string.Empty, Constants.ForgetTransactionRequest, flags: 0, routingKey: null, DataFormat.None,
                w => TransactionCodec.WriteXid(w, _xid),
                (_, _, _) => ValueTask.FromResult(true), CancellationToken.None);
        }
        catch { /* the server reaper cleans up completed transactions on its own after a while */ }
    }

    private void EnsureActive()
    {
        if (_state != State.Active)
            throw new InvalidOperationException($"The transaction is already {_state.ToString().ToLowerInvariant()}.");
    }

    /// <summary>One key's buffered state: its read version and control, its current value (null = removed/absent), and whether it was written.</summary>
    private sealed class Entry
    {
        public long Version;
        public byte ReadControl;
        public byte[]? Value;
        public Expiration Expiration;
        public bool Modified;
    }

    /// <summary>Compares keys by content so equal byte arrays map to the same entry.</summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            x is null || y is null ? ReferenceEquals(x, y) : x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}

/// <summary>
/// Raised when a transaction's commit is rejected by the server because a key it read or wrote was
/// changed by another transaction (the server's prepare-time validation failed). No part of the
/// transaction is applied; the transaction is left rolled back.
/// </summary>
public sealed class HotRodTransactionConflictException : HotRodException
{
    /// <summary>Creates the exception with a fixed message describing the conflict.</summary>
    public HotRodTransactionConflictException()
        : base("The transaction could not be committed because a key it accessed was concurrently modified.",
            Protocol.Constants.StatusNotExecuted)
    {
    }
}
