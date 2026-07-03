using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// A handle to a single remote cache. The byte-array overloads exchange raw bytes; the string
/// overloads go through the cache's <see cref="CacheEncoding"/> (UTF-8 for Raw, a ProtoStream
/// <c>WrappedMessage</c> for ProtoStream) and declare the matching MediaType so the server stores
/// them in an interoperable form. All operations are asynchronous.
/// <para>
/// When a near cache is enabled (see
/// <see cref="HotRodClient.GetCacheAsync(string?, CacheEncoding?, NearCacheOptions?, CancellationToken)"/>)
/// reads populate a local copy and change events invalidate it; such a cache owns a client listener and
/// must be disposed with <see cref="DisposeAsync"/> to unsubscribe it.
/// </para>
/// </summary>
public sealed class RemoteCache : IAsyncDisposable
{
    private readonly HotRodClient _client;
    private readonly string _name;
    private readonly CacheMarshaller _marshaller;

    // Non-null once a near cache is enabled; reads then consult it and mutations invalidate it.
    private NearCache? _nearCache;
    private ClientListener? _nearCacheListener;

    internal RemoteCache(HotRodClient client, string name, CacheEncoding encoding)
    {
        _client = client;
        _name = name;
        _marshaller = CacheMarshaller.For(encoding);
    }

    /// <summary>
    /// The near cache's hit/miss/size counters, or null when this cache has no near cache. Reflects the
    /// same store reads and events act on, so it is the way to observe local hits versus server fall-through.
    /// </summary>
    public NearCacheStatistics? NearCacheStats => _nearCache?.Snapshot();

    // -- Basic operations ---------------------------------------------------

    /// <summary>Stores a key/value pair, optionally overriding the cache's default expiration.</summary>
    public async ValueTask PutAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        await Execute(Constants.PutRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
            },
            (_, _, _) => ValueTask.FromResult(true), ct);
        _nearCache?.Remove(key);
    }

    /// <summary>Returns the value for <paramref name="key"/>, or null if absent.</summary>
    public ValueTask<byte[]?> GetAsync(byte[] key, CancellationToken ct = default) =>
        _nearCache is null
            ? Execute(Constants.GetRequest, key,
                w => HotRodCodec.WriteArray(w, key),
                async (status, reader, c) =>
                    ResponseStatus.KeyDoesNotExist(status) ? null : await HotRodCodec.ReadArrayAsync(reader, c), ct)
            : NearGetValueAsync(key, ct);

    /// <summary>Removes <paramref name="key"/>; returns true if a value was removed.</summary>
    public async ValueTask<bool> RemoveAsync(byte[] key, CancellationToken ct = default)
    {
        bool removed = await Execute(Constants.RemoveRequest, key,
            w => HotRodCodec.WriteArray(w, key),
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);
        _nearCache?.Remove(key);
        return removed;
    }

    /// <summary>Returns true if <paramref name="key"/> exists in the cache.</summary>
    public ValueTask<bool> ContainsKeyAsync(byte[] key, CancellationToken ct = default) =>
        Execute(Constants.ContainsKeyRequest, key,
            w => HotRodCodec.WriteArray(w, key),
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);

    /// <summary>Removes all entries from the cache.</summary>
    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        await Execute(Constants.ClearRequest, routingKey: null,
            _ => { }, (_, _, _) => ValueTask.FromResult(true), ct);
        _nearCache?.Clear();
    }

    /// <summary>Returns the number of entries in the cache.</summary>
    public ValueTask<int> SizeAsync(CancellationToken ct = default) =>
        Execute(Constants.SizeRequest, routingKey: null,
            _ => { }, async (_, reader, c) => await HotRodCodec.ReadVIntAsync(reader, c), ct);

    /// <summary>Returns the server's statistics for this cache as a name→value map.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> StatsAsync(CancellationToken ct = default) =>
        Execute(Constants.StatsRequest, routingKey: null,
            _ => { }, (_, reader, c) => HotRodCodec.ReadStringMapAsync(reader, c), ct);

    // -- Conditional operations ---------------------------------------------

    /// <summary>Stores the pair only if the key is absent; returns true if it was stored.</summary>
    public async ValueTask<bool> PutIfAbsentAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        bool stored = await Execute(Constants.PutIfAbsentRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
            },
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);
        _nearCache?.Remove(key);
        return stored;
    }

    /// <summary>Replaces the value only if the key is present; returns true if it was replaced.</summary>
    public async ValueTask<bool> ReplaceAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        bool replaced = await Execute(Constants.ReplaceRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
            },
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);
        _nearCache?.Remove(key);
        return replaced;
    }

    // -- Versioned (optimistic concurrency) ---------------------------------

    /// <summary>Returns the value and its version, or null if absent.</summary>
    public ValueTask<Versioned<byte[]>?> GetWithVersionAsync(byte[] key, CancellationToken ct = default) =>
        _nearCache is null
            ? Execute(Constants.GetWithVersionRequest, key,
                w => HotRodCodec.WriteArray(w, key),
                async (status, reader, c) =>
                {
                    if (ResponseStatus.KeyDoesNotExist(status))
                        return null;
                    long version = await HotRodCodec.ReadLongAsync(reader, c);
                    byte[] value = await HotRodCodec.ReadArrayAsync(reader, c);
                    return new Versioned<byte[]>(value, version);
                }, ct)
            : NearGetVersionedAsync(key, ct);

    /// <summary>Replaces the value only if its version still matches; returns true if replaced.</summary>
    public async ValueTask<bool> ReplaceWithVersionAsync(byte[] key, byte[] value, long version, Expiration expiration = default, CancellationToken ct = default)
    {
        bool replaced = await Execute(Constants.ReplaceIfUnmodifiedRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteLong(w, version);
                HotRodCodec.WriteArray(w, value);
            },
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);
        _nearCache?.Remove(key);
        return replaced;
    }

    /// <summary>Removes the entry only if its version still matches; returns true if removed.</summary>
    public async ValueTask<bool> RemoveWithVersionAsync(byte[] key, long version, CancellationToken ct = default)
    {
        bool removed = await Execute(Constants.RemoveIfUnmodifiedRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                HotRodCodec.WriteLong(w, version);
            },
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);
        _nearCache?.Remove(key);
        return removed;
    }

    // -- Metadata -----------------------------------------------------------

    /// <summary>Returns the value with its server metadata (version, timestamps, expiration), or null if absent.</summary>
    public ValueTask<MetadataValue<byte[]>?> GetWithMetadataAsync(byte[] key, CancellationToken ct = default) =>
        _nearCache is null ? FetchMetadataAsync(key, ct) : NearGetAsync(key, ct);

    /// <summary>Fetches the value and its metadata straight from the server, bypassing any near cache.</summary>
    private ValueTask<MetadataValue<byte[]>?> FetchMetadataAsync(byte[] key, CancellationToken ct) =>
        Execute(Constants.GetWithMetadataRequest, key,
            w => HotRodCodec.WriteArray(w, key),
            async (status, reader, c) =>
            {
                if (ResponseStatus.KeyDoesNotExist(status))
                    return null;
                EntryMetadata meta = await HotRodCodec.ReadMetadataAsync(reader, c);
                byte[] value = await HotRodCodec.ReadArrayAsync(reader, c);
                return MetadataValues.From(meta, value);
            }, ct);

    // -- Previous-value variants (FORCE_RETURN_VALUE) -----------------------

    /// <summary>Stores the pair and returns the value it replaced, or null if the key was absent.</summary>
    public async ValueTask<byte[]?> PutAndReturnPreviousAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        byte[]? previous = await Execute(Constants.PutRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
            },
            HotRodCodec.ReadPreviousValueAsync, ct, CacheFlag.ForceReturnValue);
        _nearCache?.Remove(key);
        return previous;
    }

    /// <summary>Removes the key and returns the value it held, or null if the key was absent.</summary>
    public async ValueTask<byte[]?> RemoveAndReturnPreviousAsync(byte[] key, CancellationToken ct = default)
    {
        byte[]? previous = await Execute(Constants.RemoveRequest, key,
            w => HotRodCodec.WriteArray(w, key),
            HotRodCodec.ReadPreviousValueAsync, ct, CacheFlag.ForceReturnValue);
        _nearCache?.Remove(key);
        return previous;
    }

    /// <summary>Replaces the value when the key is present and returns the value it replaced, else null.</summary>
    public async ValueTask<byte[]?> ReplaceAndReturnPreviousAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        byte[]? previous = await Execute(Constants.ReplaceRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
            },
            HotRodCodec.ReadPreviousValueAsync, ct, CacheFlag.ForceReturnValue);
        _nearCache?.Remove(key);
        return previous;
    }

    /// <summary>Stores the pair only if absent; returns the existing value that blocked it, or null if stored.</summary>
    public async ValueTask<byte[]?> PutIfAbsentAndReturnPreviousAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default)
    {
        byte[]? previous = await Execute(Constants.PutIfAbsentRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
            },
            HotRodCodec.ReadPreviousValueAsync, ct, CacheFlag.ForceReturnValue);
        _nearCache?.Remove(key);
        return previous;
    }

    // -- Bulk operations (round-robin; the server spans owners) --------------

    /// <summary>Stores several entries in one request.</summary>
    public async ValueTask PutAllAsync(IReadOnlyCollection<KeyValuePair<byte[], byte[]>> entries, Expiration expiration = default, CancellationToken ct = default)
    {
        await Execute(Constants.PutAllRequest, routingKey: null,
            w =>
            {
                expiration.WriteTo(w);
                HotRodCodec.WriteVInt(w, entries.Count);
                foreach (KeyValuePair<byte[], byte[]> entry in entries)
                {
                    HotRodCodec.WriteArray(w, entry.Key);
                    HotRodCodec.WriteArray(w, entry.Value);
                }
            },
            (_, _, _) => ValueTask.FromResult(true), ct);
        if (_nearCache is { } near)
            foreach (KeyValuePair<byte[], byte[]> entry in entries)
                near.Remove(entry.Key);
    }

    /// <summary>Fetches several keys in one request; absent keys are omitted from the result.</summary>
    public ValueTask<IReadOnlyList<KeyValuePair<byte[], byte[]>>> GetAllAsync(IReadOnlyCollection<byte[]> keys, CancellationToken ct = default) =>
        Execute(Constants.GetAllRequest, routingKey: null,
            w =>
            {
                HotRodCodec.WriteVInt(w, keys.Count);
                foreach (byte[] key in keys)
                    HotRodCodec.WriteArray(w, key);
            },
            async (_, reader, c) =>
            {
                int count = await HotRodCodec.ReadVIntAsync(reader, c);
                var result = new List<KeyValuePair<byte[], byte[]>>(count);
                for (int i = 0; i < count; i++)
                {
                    byte[] key = await HotRodCodec.ReadArrayAsync(reader, c);
                    byte[] value = await HotRodCodec.ReadArrayAsync(reader, c);
                    result.Add(new KeyValuePair<byte[], byte[]>(key, value));
                }
                return (IReadOnlyList<KeyValuePair<byte[], byte[]>>)result;
            }, ct);

    /// <summary>
    /// Fetches every key in the cache (scope 0 = all keys); the server spans every owner. Keys come back
    /// in the cache's storage format. The body is a run of [1-byte more-flag, key array] pairs terminated
    /// by a zero more-flag.
    /// </summary>
    public ValueTask<IReadOnlyList<byte[]>> GetKeysAsync(CancellationToken ct = default) =>
        Execute(Constants.BulkGetKeysRequest, routingKey: null,
            w => HotRodCodec.WriteVInt(w, 0), // scope 0 = all keys
            (_, reader, c) => HotRodCodec.ReadBulkKeysAsync(reader, c), ct);

    // -- Iteration (a server-side cursor over every entry) ------------------

    /// <summary>
    /// Streams every entry in the cache in batches, without loading the whole cache into memory. The
    /// server-side cursor lives on one connection, so the iteration holds a single leased connection
    /// from start to finish; disposing the enumerator (or letting it finish) ends the cursor.
    /// </summary>
    public async IAsyncEnumerable<KeyValuePair<byte[], byte[]>> IterateAsync(
        int batchSize = 100, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using Cluster.ConnectionLease lease = await _client.LeaseAsync(ct);
        HotRodConnection connection = lease.Connection;

        byte[] iterationId = await connection.ExecuteAsync(_name, Constants.IterationStartRequest, flags: 0, _marshaller.DataFormat,
            w =>
            {
                HotRodCodec.WriteSignedVInt(w, -1); // segments: null = all
                HotRodCodec.WriteSignedVInt(w, -1); // filter/converter factory: none
                HotRodCodec.WriteVInt(w, batchSize);
                HotRodCodec.WriteByte(w, 0);        // per-entry metadata: no
            },
            (_, reader, c) => HotRodCodec.ReadArrayAsync(reader, c), ct);

        try
        {
            while (true)
            {
                IterationBatch batch = await connection.ExecuteAsync(_name, Constants.IterationNextRequest, flags: 0, _marshaller.DataFormat,
                    w => HotRodCodec.WriteArray(w, iterationId),
                    (_, reader, c) => HotRodCodec.ReadIterationBatchAsync(reader, c), ct);

                if (batch.Entries.Count == 0)
                    yield break; // the server returns an empty batch once every segment is exhausted

                foreach (KeyValuePair<byte[], byte[]> entry in batch.Entries)
                    yield return entry;
            }
        }
        finally
        {
            await connection.ExecuteAsync(_name, Constants.IterationEndRequest, flags: 0, _marshaller.DataFormat,
                w => HotRodCodec.WriteArray(w, iterationId),
                (_, _, _) => ValueTask.FromResult(true), ct);
        }
    }

    // -- Client listeners (server-pushed entry events) ----------------------

    /// <summary>
    /// Registers a listener that is invoked for every entry change the server pushes. The listener
    /// holds one dedicated connection for its lifetime; dispose the returned handle to unsubscribe.
    /// <paramref name="onEvent"/> is awaited before the next event is read, so a slow handler applies
    /// back-pressure rather than racing; an exception it throws is swallowed and the stream continues.
    /// </summary>
    public async Task<ClientListener> AddListenerAsync(
        Func<ClientCacheEntryEvent, ValueTask> onEvent,
        ClientListenerOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        options ??= new ClientListenerOptions();

        byte[] listenerId = ClientListener.NewId();
        Cluster.ConnectionLease lease = await _client.LeaseAsync(ct);
        var listenerCts = new CancellationTokenSource();
        var ackReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task loop = lease.Connection.ListenAsync(
            _name, _marshaller.DataFormat,
            w => WriteAddListenerBody(w, listenerId, options),
            ev => onEvent(Map(ev)),
            ackReady, listenerCts.Token);

        try
        {
            await ackReady.Task.WaitAsync(ct); // the server's add ack (faulted on a server-side error)
        }
        catch
        {
            await listenerCts.CancelAsync();
            try { await loop; } catch { /* observe */ }
            await lease.DisposeAsync();
            listenerCts.Dispose();
            throw;
        }

        return new ClientListener(this, listenerId, lease, loop, listenerCts);
    }

    /// <summary>
    /// Decodes an event key back to a string through the cache's encoding. A
    /// <see cref="ClientCacheEntryEvent.Key"/> is the raw key in storage format (a ProtoStream
    /// <c>WrappedMessage</c> under the default encoding), so this is how a string-keyed listener reads it.
    /// </summary>
    public string DecodeKey(byte[] key) => _marshaller.Unmarshal(key);

    /// <summary>Removes the registration for <paramref name="listenerId"/> across the cluster.</summary>
    internal ValueTask<bool> RemoveListenerAsync(byte[] listenerId, CancellationToken ct) =>
        Execute(Constants.RemoveClientListenerRequest, routingKey: null,
            w => HotRodCodec.WriteArray(w, listenerId),
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);

    /// <summary>
    /// Writes the addClientListener body: the listener id, the include-current-state flag, empty
    /// key/value filter and converter factory names (this client registers none), the use-raw-data
    /// flag (raw, so event keys come back in the cache's storage format), and the interest bitmask.
    /// </summary>
    private static void WriteAddListenerBody(System.Buffers.IBufferWriter<byte> w, byte[] listenerId, ClientListenerOptions options)
    {
        HotRodCodec.WriteArray(w, listenerId);
        HotRodCodec.WriteByte(w, (byte)(options.IncludeCurrentState ? 1 : 0));
        HotRodCodec.WriteArray(w, []); // key/value filter factory name: none
        HotRodCodec.WriteArray(w, []); // converter factory name: none
        HotRodCodec.WriteByte(w, 1);   // use raw data
        HotRodCodec.WriteVInt(w, (int)(options.Interests & ClientListenerInterest.All));
    }

    /// <summary>Projects a decoded wire event onto the public event type.</summary>
    private static ClientCacheEntryEvent Map(in Protocol.RawClientEvent ev) =>
        new(ev.Opcode switch
            {
                Constants.CacheEntryCreatedEvent => ClientEventType.Created,
                Constants.CacheEntryModifiedEvent => ClientEventType.Modified,
                Constants.CacheEntryRemovedEvent => ClientEventType.Removed,
                _ => ClientEventType.Expired,
            },
            ev.Key,
            ev.HasVersion ? ev.Version : null,
            ev.CommandRetried);

    // -- Near cache (a local, listener-invalidated copy of read entries) -----

    /// <summary>
    /// Turns on the invalidated near cache for this cache: registers a client listener for Modified,
    /// Removed, and Expired events that drops the local copy of each changed key, then routes reads
    /// through the local store. Awaited so the listener is confirmed before the cache is handed back,
    /// and so no read populates the store before invalidation can arrive. Called once per cache.
    /// </summary>
    internal async Task EnableNearCacheAsync(NearCacheOptions options, CancellationToken ct)
    {
        options.Validate();
        var near = new NearCache(options.MaxEntries);

        // Created is deliberately not subscribed: a near cache only needs to be told when an entry it
        // may hold stops being valid, and it populates itself from reads rather than from create events.
        ClientListener listener = await AddListenerAsync(
            ev => { near.Remove(ev.Key); return ValueTask.CompletedTask; },
            new ClientListenerOptions
            {
                Interests = ClientListenerInterest.Modified | ClientListenerInterest.Removed | ClientListenerInterest.Expired,
            },
            ct);

        _nearCacheListener = listener;
        _nearCache = near;
    }

    /// <summary>
    /// Serves a metadata read from the near cache, or fetches and populates it. A miss reserves the key
    /// with a placeholder, fetches from the server, then caches the value only if the placeholder is
    /// still there — a concurrent write or invalidation that cleared it makes the fetched value suspect,
    /// so it is left out rather than risk caching a stale entry.
    /// </summary>
    private async ValueTask<MetadataValue<byte[]>?> NearGetAsync(byte[] key, CancellationToken ct)
    {
        NearCache near = _nearCache!;

        MetadataValue<byte[]>? hit = near.Get(key);
        if (hit is not null)
            return hit;

        bool reserved = near.PutIfAbsent(key, NearCache.Placeholder);
        MetadataValue<byte[]>? remote = await FetchMetadataAsync(key, ct);

        if (!reserved)
            return remote; // another read already owns this key's slot; don't compete for it

        if (remote is not null)
            near.Replace(key, NearCache.Placeholder, remote);
        else
            near.Remove(key, NearCache.Placeholder);

        return remote;
    }

    private async ValueTask<byte[]?> NearGetValueAsync(byte[] key, CancellationToken ct) =>
        (await NearGetAsync(key, ct))?.Value;

    private async ValueTask<Versioned<byte[]>?> NearGetVersionedAsync(byte[] key, CancellationToken ct)
    {
        MetadataValue<byte[]>? m = await NearGetAsync(key, ct);
        return m is null ? null : new Versioned<byte[]>(m.Value, m.Version);
    }

    /// <summary>
    /// Stops this cache's near cache if one is enabled: unsubscribes and tears down the client listener,
    /// then empties the local store. Idempotent, and a no-op for a cache without a near cache.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ClientListener? listener = Interlocked.Exchange(ref _nearCacheListener, null);
        if (listener is null)
            return;

        await listener.DisposeAsync();
        _nearCache?.Clear();
    }

    // -- String convenience overloads (via the cache's encoding) ------------

    public ValueTask PutAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        PutAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct);

    public async ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
    {
        byte[]? value = await GetAsync(_marshaller.Marshal(key), ct);
        return value is null ? null : _marshaller.Unmarshal(value);
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) =>
        RemoveAsync(_marshaller.Marshal(key), ct);

    public ValueTask<bool> ContainsKeyAsync(string key, CancellationToken ct = default) =>
        ContainsKeyAsync(_marshaller.Marshal(key), ct);

    public ValueTask<bool> PutIfAbsentAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        PutIfAbsentAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct);

    public ValueTask<bool> ReplaceAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        ReplaceAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct);

    public async ValueTask<Versioned<string>?> GetWithVersionAsync(string key, CancellationToken ct = default)
    {
        Versioned<byte[]>? versioned = await GetWithVersionAsync(_marshaller.Marshal(key), ct);
        return versioned is null ? null : new Versioned<string>(_marshaller.Unmarshal(versioned.Value), versioned.Version);
    }

    public ValueTask<bool> ReplaceWithVersionAsync(string key, string value, long version, Expiration expiration = default, CancellationToken ct = default) =>
        ReplaceWithVersionAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), version, expiration, ct);

    public ValueTask<bool> RemoveWithVersionAsync(string key, long version, CancellationToken ct = default) =>
        RemoveWithVersionAsync(_marshaller.Marshal(key), version, ct);

    public async ValueTask<MetadataValue<string>?> GetWithMetadataAsync(string key, CancellationToken ct = default)
    {
        MetadataValue<byte[]>? meta = await GetWithMetadataAsync(_marshaller.Marshal(key), ct);
        return meta is null
            ? null
            : new MetadataValue<string>(_marshaller.Unmarshal(meta.Value), meta.Version,
                meta.Created, meta.Lifespan, meta.LastUsed, meta.MaxIdle);
    }

    public async ValueTask<string?> PutAndReturnPreviousAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        Unmarshal(await PutAndReturnPreviousAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct));

    public async ValueTask<string?> RemoveAndReturnPreviousAsync(string key, CancellationToken ct = default) =>
        Unmarshal(await RemoveAndReturnPreviousAsync(_marshaller.Marshal(key), ct));

    public async ValueTask<string?> ReplaceAndReturnPreviousAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        Unmarshal(await ReplaceAndReturnPreviousAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct));

    public async ValueTask<string?> PutIfAbsentAndReturnPreviousAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        Unmarshal(await PutIfAbsentAndReturnPreviousAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct));

    /// <summary>Streams every entry decoded through the cache's encoding. See <see cref="IterateAsync"/>.</summary>
    public async IAsyncEnumerable<KeyValuePair<string, string>> IterateStringsAsync(
        int batchSize = 100, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (KeyValuePair<byte[], byte[]> entry in IterateAsync(batchSize, ct))
            yield return new KeyValuePair<string, string>(_marshaller.Unmarshal(entry.Key), _marshaller.Unmarshal(entry.Value));
    }

    /// <summary>Fetches every key decoded through the cache's encoding. See <see cref="GetKeysAsync"/>.</summary>
    public async ValueTask<IReadOnlyList<string>> GetKeyStringsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<byte[]> keys = await GetKeysAsync(ct);
        var result = new List<string>(keys.Count);
        foreach (byte[] key in keys)
            result.Add(_marshaller.Unmarshal(key));
        return result;
    }

    // -- Plumbing -----------------------------------------------------------

    /// <summary>Decodes a previous value through the cache's encoding, preserving null (absent).</summary>
    private string? Unmarshal(byte[]? value) => value is null ? null : _marshaller.Unmarshal(value);

    private ValueTask<T> Execute<T>(
        byte opcode,
        byte[]? routingKey,
        Action<System.Buffers.IBufferWriter<byte>> writeBody,
        Func<byte, System.IO.Pipelines.PipeReader, CancellationToken, ValueTask<T>> readBody,
        CancellationToken ct,
        CacheFlag flags = CacheFlag.None) =>
        _client.ExecuteAsync(_name, opcode, (int)flags, routingKey, _marshaller.DataFormat, writeBody, readBody, ct);
}
