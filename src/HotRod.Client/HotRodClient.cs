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

    public ValueTask DisposeAsync() => _cluster.DisposeAsync();
}
