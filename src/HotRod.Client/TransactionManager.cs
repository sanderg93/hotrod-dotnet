using System.Buffers.Binary;

namespace HotRod.Client;

/// <summary>
/// The entry point to client-side transactions over HotRod. A transaction buffers its writes on the
/// client and, on commit, drives the two-phase protocol against a transactional cache: it stages the
/// whole modification set under an XID with PREPARE_TX, then completes it with COMMIT_TX or ROLLBACK_TX.
/// Begin one with <see cref="Begin"/> (or <see cref="RemoteCache.BeginTransaction"/>) and operate on the
/// returned <see cref="HotRodTransaction"/>. The manager is thread-safe and cheap; obtain it from
/// <see cref="HotRodClient.Transactions"/>.
/// <para>
/// A transaction is scoped to a single cache, which is the model a hand-built client can support without
/// an external <c>System.Transactions</c> transaction manager coordinating multiple resources. It mints
/// XIDs the same way as Infinispan's <c>RemoteXid</c> — a per-manager id, the creation time, and a
/// monotonic counter — so each XID is unique across the manager's lifetime.
/// </para>
/// </summary>
public sealed class TransactionManager
{
    // "HRTX" in ASCII, the format id Infinispan's RemoteXid uses so the server recognises the XID as a
    // HotRod remote transaction.
    internal const int RemoteFormatId = 0x48525458;

    /// <summary>The default transaction timeout sent to the server in a PREPARE_TX body.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HotRodClient _client;
    private readonly Guid _managerId = Guid.NewGuid();
    private long _globalIdCounter;
    private long _branchQualifierCounter;

    internal TransactionManager(HotRodClient client) => _client = client;

    /// <summary>
    /// Begins a transaction against <paramref name="cache"/>. The returned transaction buffers its writes
    /// locally and applies them atomically on <see cref="HotRodTransaction.CommitAsync"/>; disposing it or
    /// calling <see cref="HotRodTransaction.RollbackAsync"/> discards them. The cache must be configured
    /// server-side as transactional (transaction mode NON_XA or NON_DURABLE_XA).
    /// </summary>
    public HotRodTransaction Begin(RemoteCache cache, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return new HotRodTransaction(_client, cache.Name, cache.Marshaller, NewXid(), timeout ?? DefaultTimeout);
    }

    /// <summary>
    /// Mints a fresh XID, mirroring Infinispan's <c>RemoteXid.create</c>: the global id and branch
    /// qualifier are each 32 bytes holding the manager id (two 8-byte halves), the creation time, and a
    /// per-part monotonic counter, so no two XIDs from this manager collide.
    /// </summary>
    private TransactionXid NewXid()
    {
        long creationTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] globalId = BuildId(creationTime, Interlocked.Increment(ref _globalIdCounter));
        byte[] branchQualifier = BuildId(creationTime, Interlocked.Increment(ref _branchQualifierCounter));
        return new TransactionXid(RemoteFormatId, globalId, branchQualifier);
    }

    private byte[] BuildId(long creationTime, long sequence)
    {
        var field = new byte[32]; // four big-endian longs, matching RemoteXid's layout
        (long mostSignificant, long leastSignificant) = GuidHalves(_managerId);
        BinaryPrimitives.WriteInt64BigEndian(field.AsSpan(0), leastSignificant);
        BinaryPrimitives.WriteInt64BigEndian(field.AsSpan(8), mostSignificant);
        BinaryPrimitives.WriteInt64BigEndian(field.AsSpan(16), creationTime);
        BinaryPrimitives.WriteInt64BigEndian(field.AsSpan(24), sequence);
        return field;
    }

    /// <summary>Splits a <see cref="Guid"/> into its most- and least-significant 64-bit halves.</summary>
    private static (long MostSignificant, long LeastSignificant) GuidHalves(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        long most = BinaryPrimitives.ReadInt64BigEndian(bytes);
        long least = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        return (most, least);
    }
}

/// <summary>
/// Identifies a transaction on the wire: an X/Open XID of a format id, a global transaction id, and a
/// branch qualifier (Infinispan's <c>RemoteXid</c>). The client generates these; they are opaque to
/// callers and are carried on every prepare, commit, rollback, and forget request for the transaction.
/// </summary>
public sealed class TransactionXid
{
    internal TransactionXid(int formatId, byte[] globalId, byte[] branchQualifier)
    {
        FormatId = formatId;
        GlobalId = globalId;
        BranchQualifier = branchQualifier;
    }

    /// <summary>The XID format identifier.</summary>
    public int FormatId { get; }

    /// <summary>The global transaction id.</summary>
    public byte[] GlobalId { get; }

    /// <summary>The transaction branch qualifier.</summary>
    public byte[] BranchQualifier { get; }
}
