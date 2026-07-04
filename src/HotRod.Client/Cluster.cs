using System.Buffers;
using System.IO.Pipelines;
using HotRod.Client.Logging;
using HotRod.Client.Protocol;
using Microsoft.Extensions.Logging;

namespace HotRod.Client;

/// <summary>
/// Tracks the cluster the client is connected to: one <see cref="ConnectionPool"/> per node, the
/// topology id last seen per cache, and a round-robin cursor for spreading operations across nodes.
/// Connections report topology updates here (via <see cref="ITopologyCoordinator"/>); the node set
/// is reconciled lazily — new nodes get a pool, departed nodes have theirs disposed — just before
/// the next operation picks a pool.
/// </summary>
internal sealed class Cluster : ITopologyCoordinator, IAsyncDisposable
{
    /// <summary>Sent in the first request so the server always pushes the current topology.</summary>
    private const int ForceTopologyUpdate = -1;

    private readonly HotRodClientOptions _options;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly Dictionary<string, int> _topologyIds = new();
    private readonly Dictionary<string, ConsistentHash> _hashes = new();

    private Dictionary<ServerAddress, ConnectionPool> _pools = new();
    private ConnectionPool[] _ordered = [];
    private IReadOnlyList<ServerAddress> _desired = [];
    private bool _dirty;
    private int _roundRobin = -1;

    private Cluster(HotRodClientOptions options)
    {
        _options = options;
        _logger = options.LoggerFactory.CreateLogger("HotRod.Client.Cluster");
    }

    /// <summary>Creates the cluster with one pool for the seed server from <paramref name="options"/>.</summary>
    public static async ValueTask<Cluster> CreateAsync(HotRodClientOptions options, CancellationToken ct)
    {
        var cluster = new Cluster(options);
        var seed = new ServerAddress(options.Host, options.Port);
        try
        {
            ConnectionPool pool = await ConnectionPool.CreateAsync(options, seed, cluster, ct);
            cluster._pools = new Dictionary<ServerAddress, ConnectionPool> { [seed] = pool };
            cluster._ordered = [pool];
            cluster._desired = [seed];
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    /// <summary>The nodes the client currently knows about.</summary>
    public IReadOnlyList<ServerAddress> KnownServers
    {
        get { lock (_sync) return _desired; }
    }

    public int GetTopologyId(string cacheName)
    {
        lock (_sync)
            return _topologyIds.TryGetValue(cacheName, out int id) ? id : ForceTopologyUpdate;
    }

    public void ReportTopology(string cacheName, int topologyId, IReadOnlyList<ServerAddress> servers, ConsistentHash? hash)
    {
        lock (_sync)
        {
            _topologyIds[cacheName] = topologyId;
            if (hash is not null)
                _hashes[cacheName] = hash;
            if (servers.Count > 0 && !SameServers(_desired, servers))
            {
                _desired = servers;
                _dirty = true;
                Log.TopologyChanged(_logger, cacheName, topologyId, servers.Count);
            }
        }
    }

    /// <summary>
    /// Runs an exchange on the node that owns <paramref name="routingKey"/> (single-hop) when the
    /// cache's hash is known, otherwise on the next node in rotation. Keyless operations round-robin.
    /// A retriable failure — a dropped connection, a protocol desync, or a node-unavailable status —
    /// drops the bad connection, refreshes topology if it changed, and retries on another node, up to
    /// <see cref="HotRodClientOptions.MaxRetries"/> extra attempts before the last error surfaces.
    /// </summary>
    public ValueTask<T> ExecuteAsync<T>(
        string cacheName,
        byte opcode,
        int flags,
        byte[]? routingKey,
        DataFormat dataFormat,
        Action<IBufferWriter<byte>> writeBody,
        Func<byte, PipeReader, CancellationToken, ValueTask<T>> readBody,
        CancellationToken ct)
    {
        var tried = new HashSet<ServerAddress>();
        return RetryPolicy.ExecuteAsync(
            _options.MaxRetries + 1,
            async (attempt, c) =>
            {
                // A topology update reported by an earlier (failed) attempt may have changed the node set.
                if (_dirty)
                    await ReconcileAsync(c);

                ConnectionPool pool = attempt == 0
                    ? SelectPool(cacheName, routingKey)
                    : SelectFailoverPool(tried);
                if (attempt > 0)
                    Log.NodeFailover(_logger, pool.Server.Host, pool.Server.Port, attempt);
                tried.Add(pool.Server);

                ConnectionPool.PooledConnection pooled = await pool.BorrowAsync(c);
                try
                {
                    return await pooled.Connection.ExecuteAsync(cacheName, opcode, flags, dataFormat, writeBody, readBody, c);
                }
                finally
                {
                    // A connection faulted by an interrupted exchange is discarded on return, so the next
                    // attempt (and every later borrow) opens a fresh one — reconnecting the broken link.
                    await pool.ReturnAsync(pooled);
                }
            },
            ct);
    }

    /// <summary>
    /// Picks a pool for a retry, preferring a node not yet tried so retries spread across the cluster;
    /// once every node has been tried it falls back to round-robin rather than giving up early.
    /// </summary>
    private ConnectionPool SelectFailoverPool(HashSet<ServerAddress> tried)
    {
        foreach (ConnectionPool pool in _ordered) // reference read is atomic; replaced wholesale on reconcile
            if (!tried.Contains(pool.Server))
                return pool;
        return NextPool();
    }

    /// <summary>
    /// Borrows a single connection and keeps it until the returned lease is disposed. Used by
    /// iteration, whose server-side cursor is bound to one connection across start/next/end.
    /// </summary>
    public async ValueTask<ConnectionLease> LeaseAsync(CancellationToken ct)
    {
        if (_dirty)
            await ReconcileAsync(ct);

        ConnectionPool pool = NextPool();
        ConnectionPool.PooledConnection pooled = await pool.BorrowAsync(ct);
        return new ConnectionLease(pool, pooled);
    }

    /// <summary>A connection held across several exchanges; returns it to its pool when disposed.</summary>
    internal sealed class ConnectionLease(ConnectionPool pool, ConnectionPool.PooledConnection pooled) : IAsyncDisposable
    {
        public HotRodConnection Connection => pooled.Connection;
        public ValueTask DisposeAsync() => pool.ReturnAsync(pooled);
    }

    /// <summary>The node the consistent hash says primarily owns <paramref name="key"/>, if known.</summary>
    public ServerAddress? PrimaryOwner(string cacheName, byte[] key)
    {
        ConsistentHash? hash;
        lock (_sync)
            _hashes.TryGetValue(cacheName, out hash);
        return hash?.PrimaryOwner(key);
    }

    /// <summary>Picks the primary owner's pool for a routable key, falling back to round-robin.</summary>
    private ConnectionPool SelectPool(string cacheName, byte[]? routingKey)
    {
        if (routingKey is not null)
        {
            ConsistentHash? hash;
            lock (_sync)
                _hashes.TryGetValue(cacheName, out hash);

            if (hash?.PrimaryOwner(routingKey) is ServerAddress owner
                && _pools.TryGetValue(owner, out ConnectionPool? pool))
                return pool;
        }
        return NextPool();
    }

    private ConnectionPool NextPool()
    {
        ConnectionPool[] pools = _ordered; // reference read is atomic; replaced wholesale on reconcile
        if (pools.Length == 0)
            throw new HotRodException("No cluster nodes are available.");

        uint next = (uint)Interlocked.Increment(ref _roundRobin);
        return pools[next % (uint)pools.Length];
    }

    /// <summary>Brings the live pools in line with the latest reported node set.</summary>
    private async ValueTask ReconcileAsync(CancellationToken ct)
    {
        await _reconcileLock.WaitAsync(ct);
        try
        {
            IReadOnlyList<ServerAddress> desired;
            lock (_sync)
            {
                if (!_dirty)
                    return;
                desired = _desired;
                _dirty = false;
            }

            Dictionary<ServerAddress, ConnectionPool> current = _pools;
            var next = new Dictionary<ServerAddress, ConnectionPool>();

            foreach (ServerAddress server in desired)
            {
                if (next.ContainsKey(server))
                    continue;

                if (current.TryGetValue(server, out ConnectionPool? existing))
                {
                    next[server] = existing;
                }
                else
                {
                    try { next[server] = await ConnectionPool.CreateAsync(_options, server, this, ct); }
                    catch { /* node unreachable right now; skip it and route to the rest */ }
                }
            }

            // Keep the existing pools if the new set ended up empty (e.g. every new node was unreachable).
            if (next.Count == 0)
                return;

            List<ConnectionPool> removed = current
                .Where(kv => !next.ContainsKey(kv.Key))
                .Select(kv => kv.Value)
                .ToList();

            _pools = next;
            _ordered = [.. next.Values];

            foreach (ConnectionPool pool in removed)
                await pool.DisposeAsync();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private static bool SameServers(IReadOnlyList<ServerAddress> a, IReadOnlyList<ServerAddress> b)
    {
        if (a.Count != b.Count)
            return false;
        var set = new HashSet<ServerAddress>(a);
        return set.SetEquals(b);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ConnectionPool pool in _pools.Values)
            await pool.DisposeAsync();
        _reconcileLock.Dispose();
    }
}
