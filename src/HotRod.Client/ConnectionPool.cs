using System.Collections.Concurrent;
using HotRod.Client.Logging;
using Microsoft.Extensions.Logging;

namespace HotRod.Client;

/// <summary>
/// A bounded pool of <see cref="HotRodConnection"/>. A capacity semaphore caps the number of
/// live connections at <see cref="HotRodClientOptions.MaxConnections"/>; idle connections are
/// reused, expired ones are retired, and faulted ones are discarded so a fresh connection takes
/// their place. Borrowing prefers an idle connection and only opens a new one when none is free.
/// </summary>
internal sealed class ConnectionPool : IAsyncDisposable
{
    private readonly HotRodClientOptions _options;
    private readonly ServerAddress _server;
    private readonly ITopologyCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _capacity;
    private readonly ConcurrentQueue<PooledConnection> _idle = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _reaper;
    private volatile bool _disposed;

    private ConnectionPool(HotRodClientOptions options, ServerAddress server, ITopologyCoordinator coordinator)
    {
        _options = options;
        _server = server;
        _coordinator = coordinator;
        _logger = options.LoggerFactory.CreateLogger("HotRod.Client.ConnectionPool");
        _capacity = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
        _reaper = options.MaintenanceInterval > TimeSpan.Zero ? ReapLoopAsync() : Task.CompletedTask;
    }

    /// <summary>The node this pool connects to.</summary>
    public ServerAddress Server => _server;

    /// <summary>Creates a pool for one node and eagerly opens <see cref="HotRodClientOptions.MinConnections"/> connections.</summary>
    public static async ValueTask<ConnectionPool> CreateAsync(
        HotRodClientOptions options, ServerAddress server, ITopologyCoordinator coordinator, CancellationToken ct)
    {
        var pool = new ConnectionPool(options, server, coordinator);
        try
        {
            for (int i = 0; i < options.MinConnections; i++)
                pool._idle.Enqueue(await pool.OpenAsync(ct));
            return pool;
        }
        catch
        {
            await pool.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Borrows a connection, waiting up to <see cref="HotRodClientOptions.AcquireTimeout"/> for a
    /// free slot. Reuses an idle connection when one is still good, otherwise opens a new one.
    /// </summary>
    public async ValueTask<PooledConnection> BorrowAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await _capacity.WaitAsync(_options.AcquireTimeout, ct))
        {
            Log.PoolExhausted(_logger, _server.Host, _server.Port, _options.AcquireTimeout);
            throw new HotRodException($"Timed out waiting {_options.AcquireTimeout} for a pooled connection.");
        }

        try
        {
            while (_idle.TryDequeue(out PooledConnection? pooled))
            {
                if (!pooled.IsReusable(_options, DateTime.UtcNow) || !await IsLiveAsync(pooled, ct))
                {
                    await pooled.Connection.DisposeAsync();
                    continue;
                }
                return pooled;
            }
            return await OpenAsync(ct);
        }
        catch
        {
            _capacity.Release();
            throw;
        }
    }

    /// <summary>When validation is on, PINGs a reused connection so a dead one is replaced, not handed out.</summary>
    private async ValueTask<bool> IsLiveAsync(PooledConnection pooled, CancellationToken ct)
    {
        if (!_options.ValidateOnBorrow)
            return true;
        try
        {
            await pooled.Connection.PingAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Returns a borrowed connection to the pool, retiring it if faulted or past its lifetime.</summary>
    public async ValueTask ReturnAsync(PooledConnection pooled)
    {
        bool keep = !_disposed
            && !pooled.Connection.IsFaulted
            && DateTime.UtcNow - pooled.CreatedAt < _options.MaxLifetime;

        if (keep)
        {
            pooled.MarkUsed(DateTime.UtcNow);
            _idle.Enqueue(pooled);
        }
        else
        {
            await pooled.Connection.DisposeAsync();
        }

        _capacity.Release();
    }

    private async ValueTask<PooledConnection> OpenAsync(CancellationToken ct)
    {
        HotRodConnection connection = await HotRodConnection.ConnectAsync(_options, _server, _coordinator, ct);
        return new PooledConnection(connection);
    }

    /// <summary>Periodically retires idle connections that have passed their idle/lifetime limit.</summary>
    private async Task ReapLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.MaintenanceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                // Sweep at most the current idle count; reusable connections go back, the rest are closed.
                int sweep = _idle.Count;
                for (int i = 0; i < sweep && _idle.TryDequeue(out PooledConnection? pooled); i++)
                {
                    if (pooled.IsReusable(_options, DateTime.UtcNow))
                        _idle.Enqueue(pooled);
                    else
                        await pooled.Connection.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // pool disposed
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _shutdown.CancelAsync();
        try { await _reaper; } catch (OperationCanceledException) { }

        while (_idle.TryDequeue(out PooledConnection? pooled))
            await pooled.Connection.DisposeAsync();

        _capacity.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>A pooled connection plus the timestamps used to decide whether it may be reused.</summary>
    internal sealed class PooledConnection
    {
        public PooledConnection(HotRodConnection connection)
        {
            Connection = connection;
            CreatedAt = DateTime.UtcNow;
            LastUsedAt = CreatedAt;
        }

        public HotRodConnection Connection { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastUsedAt { get; private set; }

        public void MarkUsed(DateTime now) => LastUsedAt = now;

        public bool IsReusable(HotRodClientOptions options, DateTime now) =>
            !Connection.IsFaulted
            && now - CreatedAt < options.MaxLifetime
            && now - LastUsedAt < options.MaxIdleTime;
    }
}
