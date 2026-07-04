using System.Security.Cryptography;

namespace HotRod.Client;

/// <summary>
/// A live client-listener registration. It owns the dedicated connection the events stream over (held
/// as a lease for its whole lifetime) and the background loop reading them. Dispose it to unsubscribe:
/// the server is told to remove the listener, the read loop is stopped, and the spent connection is
/// returned to its pool to be discarded. Disposal is idempotent.
/// </summary>
public sealed class ClientListener : IAsyncDisposable
{
    private readonly RemoteCache _cache;
    private readonly byte[] _listenerId;
    private readonly Cluster.ConnectionLease _lease;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts;
    private int _disposed;

    internal ClientListener(RemoteCache cache, byte[] listenerId, Cluster.ConnectionLease lease, Task loop, CancellationTokenSource cts)
    {
        _cache = cache;
        _listenerId = listenerId;
        _lease = lease;
        _loop = loop;
        _cts = cts;
    }

    /// <summary>The server-wide id this listener was registered under (a random 16-byte token).</summary>
    public byte[] ListenerId => _listenerId;

    /// <summary>Generates the random id a new listener registers itself under.</summary>
    internal static byte[] NewId() => RandomNumberGenerator.GetBytes(16);

    /// <summary>Unregisters the listener from the server and stops the background read loop. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Tell the server to stop sending events (on a regular connection — a removal works clusterwide
        // by id), then stop the read loop and hand the now mid-stream connection back to be discarded.
        try { await _cache.RemoveListenerAsync(_listenerId, CancellationToken.None); }
        catch { /* best effort: a failed removal still tears the local registration down */ }

        await _cts.CancelAsync();
        try { await _loop; } catch { /* observe the loop's completion (cancellation or fault) */ }

        await _lease.DisposeAsync();
        _cts.Dispose();
    }
}
