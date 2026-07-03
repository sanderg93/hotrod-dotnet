namespace HotRod.Client;

/// <summary>A cluster member's HotRod endpoint, as advertised by the server in a topology update.</summary>
internal readonly record struct ServerAddress(string Host, int Port);

/// <summary>
/// Connects a <see cref="HotRodConnection"/> to the cluster view it shares with every other
/// connection: the connection reads the topology id to put in each request header (so the server
/// only resends the topology when it changes), and reports any topology update it parses out of a
/// response so the owning client can add or drop per-node pools.
/// </summary>
internal interface ITopologyCoordinator
{
    /// <summary>The last topology id seen for <paramref name="cacheName"/>, or a value forcing an update.</summary>
    int GetTopologyId(string cacheName);

    /// <summary>
    /// Reports a topology update parsed from a response for <paramref name="cacheName"/>:
    /// the new id, the cluster nodes, and the key→owner hash (null when the cache is not
    /// distribution-aware).
    /// </summary>
    void ReportTopology(string cacheName, int topologyId, IReadOnlyList<ServerAddress> servers, ConsistentHash? hash);
}
