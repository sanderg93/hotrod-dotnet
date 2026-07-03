using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// How much cluster awareness the client advertises in each request header, which decides what the
/// server pushes back. Higher levels are supersets: the server only sends what the chosen level asks
/// for, and the client falls back gracefully when a cache offers less (e.g. a non-distributed cache
/// sends no hash table even under <see cref="HashAware"/>).
/// </summary>
public enum ClientIntelligence : byte
{
    /// <summary>The client talks only to its seed node; the server never pushes topology updates.</summary>
    Basic = Constants.IntelligenceBasic,

    /// <summary>The server pushes the node list on change, so the client keeps a pool per node and round-robins.</summary>
    TopologyAware = Constants.IntelligenceTopologyAware,

    /// <summary>The server also pushes the consistent-hash segment table, enabling single-hop routing to a key's owner.</summary>
    HashAware = Constants.IntelligenceHashAware,
}
