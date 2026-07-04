using System.Buffers;
using System.IO.Pipelines;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// The entry point: a pooled, topology-aware, thread-safe HotRod client. It discovers the cluster
/// from the server's topology updates and keeps a connection pool per node; each operation borrows a
/// connection from the next node in rotation, runs on it, and returns it. Many operations can run
/// concurrently (up to <see cref="HotRodClientOptions.MaxConnections"/> per node), while each
/// connection still handles one request at a time. Share a single instance across your application
/// and dispose it on shutdown.
/// </summary>
public sealed class HotRodClient : IAsyncDisposable
{
    private readonly Cluster _cluster;
    private readonly CacheEncoding _defaultEncoding;
    private CounterManager? _counters;
    private TransactionManager? _transactions;
    private AdminManager? _administration;

    private HotRodClient(Cluster cluster, CacheEncoding defaultEncoding)
    {
        _cluster = cluster;
        _defaultEncoding = defaultEncoding;
    }

    /// <summary>Creates a client and connects to the seed server from <paramref name="options"/>.</summary>
    public static async ValueTask<HotRodClient> ConnectAsync(HotRodClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new HotRodClient(await Cluster.CreateAsync(options, ct), options.DefaultEncoding);
    }

    /// <summary>Convenience overload for the common case; pool sizing keeps its defaults.</summary>
    public static ValueTask<HotRodClient> ConnectAsync(
        string host = "127.0.0.1",
        int port = 11222,
        string? username = null,
        string? password = null,
        SaslMechanism mechanism = SaslMechanism.ScramSha256,
        TlsOptions? tls = null,
        CancellationToken ct = default) =>
        ConnectAsync(
            new HotRodClientOptions
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                Mechanism = mechanism,
                Tls = tls,
            },
            ct);

    /// <summary>The cluster nodes the client currently knows about, as "host:port" strings.</summary>
    public IReadOnlyList<string> Servers =>
        _cluster.KnownServers.Select(s => $"{s.Host}:{s.Port}").ToList();

    /// <summary>
    /// The node ("host:port") the consistent hash routes <paramref name="key"/> to, or null when the
    /// cache's hash is not yet known. Diagnostic — reflects the same decision the client makes when routing.
    /// </summary>
    public string? GetPrimaryOwner(string? cacheName, byte[] key) =>
        _cluster.PrimaryOwner(cacheName ?? string.Empty, key) is { } s ? $"{s.Host}:{s.Port}" : null;

    /// <summary>
    /// Returns a handle to a named cache (or the default cache when <paramref name="name"/> is
    /// null/empty), using <paramref name="encoding"/> or the client's default when not given.
    /// </summary>
    public RemoteCache GetCache(string? name = null, CacheEncoding? encoding = null) =>
        new(this, name ?? string.Empty, encoding ?? _defaultEncoding);

    /// <summary>
    /// The manager for Infinispan's clustered counters (strong and weak). Counters are independent of any
    /// cache; use this to define, look up, and remove them. Shared for the life of the client.
    /// </summary>
    public CounterManager Counters => _counters ??= new CounterManager(this);

    /// <summary>
    /// The manager for client-side transactions over a transactional cache. Use it (or
    /// <see cref="RemoteCache.BeginTransaction"/>) to begin a transaction, buffer reads and writes, then
    /// commit or roll them back atomically. Shared for the life of the client.
    /// </summary>
    public TransactionManager Transactions => _transactions ??= new TransactionManager(this);

    /// <summary>
    /// The entry point for Ickle remote queries against <paramref name="cache"/>, modelled on Java's
    /// <c>QueryFactory</c>: build a query with <see cref="QueryFactory.Create"/>, optionally set paging and
    /// named parameters, then execute it. The cache should use ProtoStream encoding and the server must
    /// have the matching protobuf schema registered.
    /// </summary>
    public QueryFactory Query(RemoteCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return new QueryFactory(cache);
    }

    /// <summary>
    /// The entry point for runtime cache administration: create, get-or-create, remove, and enumerate
    /// caches on the server. Independent of any cache handle. Shared for the life of the client.
    /// </summary>
    public AdminManager Administration => _administration ??= new AdminManager(this);

    /// <summary>
    /// Runs the named server-side task (script) and returns its raw result. Parameters are passed as an
    /// already-marshalled name/value map. With <paramref name="cacheName"/> null or empty the task runs
    /// cluster-wide; otherwise it runs in that cache's context, and <paramref name="routingKey"/> (when
    /// given) selects the node that owns the key. The result bytes are the task's raw return value.
    /// </summary>
    public ValueTask<byte[]> ExecuteAsync(
        string scriptName,
        IDictionary<string, byte[]>? parameters = null,
        string? cacheName = null,
        byte[]? routingKey = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scriptName);
        IReadOnlyDictionary<string, byte[]> args = parameters as IReadOnlyDictionary<string, byte[]>
            ?? (parameters is null ? EmptyExecuteParameters : new Dictionary<string, byte[]>(parameters));
        return ScriptExecutor.ExecuteAsync(this, cacheName ?? string.Empty, scriptName, args, routingKey, ct);
    }

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyExecuteParameters = new Dictionary<string, byte[]>();

    /// <summary>
    /// Returns a cache handle like <see cref="GetCache"/>, additionally wiring up a client-side near
    /// cache when <paramref name="nearCache"/> is given. Reads then populate a local copy and change
    /// events invalidate it; the listener is registered before this returns, so events are not missed.
    /// A near-cache handle owns that listener and must be disposed (<see cref="RemoteCache.DisposeAsync"/>)
    /// to unsubscribe it. With <paramref name="nearCache"/> null this is just an async <see cref="GetCache"/>.
    /// </summary>
    public async Task<RemoteCache> GetCacheAsync(
        string? name = null, CacheEncoding? encoding = null, NearCacheOptions? nearCache = null, CancellationToken ct = default)
    {
        var cache = new RemoteCache(this, name ?? string.Empty, encoding ?? _defaultEncoding);
        if (nearCache is not null)
            await cache.EnableNearCacheAsync(nearCache, ct);
        return cache;
    }

    /// <summary>
    /// Runs one request/response exchange, routed to the next node in rotation. Equivalent to a
    /// cache operation but used internally so <see cref="RemoteCache"/> stays protocol-agnostic.
    /// </summary>
    internal ValueTask<T> ExecuteAsync<T>(
        string cacheName,
        byte opcode,
        int flags,
        byte[]? routingKey,
        DataFormat dataFormat,
        Action<IBufferWriter<byte>> writeBody,
        Func<byte, PipeReader, CancellationToken, ValueTask<T>> readBody,
        CancellationToken ct) =>
        _cluster.ExecuteAsync(cacheName, opcode, flags, routingKey, dataFormat, writeBody, readBody, ct);

    /// <summary>Leases a single connection for a multi-exchange operation (iteration) bound to one node.</summary>
    internal ValueTask<Cluster.ConnectionLease> LeaseAsync(CancellationToken ct) => _cluster.LeaseAsync(ct);

    /// <summary>Closes all pooled connections to the cluster.</summary>
    public ValueTask DisposeAsync() => _cluster.DisposeAsync();
}
