namespace HotRod.Client;

/// <summary>
/// Configuration for a <see cref="HotRodClient"/>: which server to reach, how to authenticate
/// and encrypt, and how to size the connection pool. Every setting has a sensible default, so
/// only the host (and credentials, if the endpoint is secured) usually need to be set.
/// </summary>
public sealed class HotRodClientOptions
{
    // -- Connection target ---------------------------------------------------

    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 11222;

    // -- Security ------------------------------------------------------------

    /// <summary>Username for SASL authentication; leave null for an unauthenticated endpoint.</summary>
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>SASL mechanism used when credentials are supplied. Defaults to SCRAM-SHA-256.</summary>
    public SaslMechanism Mechanism { get; init; } = SaslMechanism.ScramSha256;

    /// <summary>TLS settings; leave null to connect in plaintext.</summary>
    public TlsOptions? Tls { get; init; }

    // -- Cluster awareness ---------------------------------------------------

    /// <summary>
    /// How much cluster topology the client tracks. Defaults to <see cref="ClientIntelligence.HashAware"/>
    /// (single-hop routing to each key's owner); lower it only to disable topology tracking or hash routing.
    /// </summary>
    public ClientIntelligence Intelligence { get; init; } = ClientIntelligence.HashAware;

    /// <summary>
    /// The encoding used by caches obtained via <see cref="HotRodClient.GetCache"/> when none is
    /// given. Defaults to ProtoStream for interoperability with the Java client and the console.
    /// </summary>
    public CacheEncoding DefaultEncoding { get; init; } = CacheEncoding.ProtoStream;

    // -- Resilience ----------------------------------------------------------

    /// <summary>
    /// How many additional attempts an operation makes, on other connections/nodes, after a retriable
    /// failure (a dropped connection, a protocol desync, or a node-unavailable server status) before
    /// the last error surfaces. Zero disables retry. Bounds the failover so a persistent failure cannot
    /// loop; retries spread across the known nodes. Defaults to 10, matching the Java HotRod client.
    /// </summary>
    public int MaxRetries { get; init; } = 10;

    // -- Pool sizing ---------------------------------------------------------

    /// <summary>Connections opened eagerly at startup to validate connectivity and stay warm.</summary>
    public int MinConnections { get; init; } = 1;

    /// <summary>Hard cap on concurrent connections; further callers wait up to <see cref="AcquireTimeout"/>.</summary>
    public int MaxConnections { get; init; } = 10;

    /// <summary>How long to wait for a free connection when the pool is at its maximum.</summary>
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An idle connection unused for longer than this is closed rather than reused.</summary>
    public TimeSpan MaxIdleTime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A connection older than this is retired on return, so connections rotate over time.</summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When true, a pooled connection is PING-checked before being handed out and replaced if it
    /// fails. Catches connections a peer closed while idle, at the cost of a round-trip per borrow.
    /// </summary>
    public bool ValidateOnBorrow { get; init; }

    /// <summary>
    /// How often a background sweep retires idle/expired connections. Set to <see cref="TimeSpan.Zero"/>
    /// to disable the sweep and rely only on eviction at borrow time.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaxConnections < 1)
            throw new ArgumentException("MaxConnections must be at least 1.", nameof(MaxConnections));
        if (MinConnections < 0 || MinConnections > MaxConnections)
            throw new ArgumentException("MinConnections must be between 0 and MaxConnections.", nameof(MinConnections));
        if (AcquireTimeout <= TimeSpan.Zero)
            throw new ArgumentException("AcquireTimeout must be positive.", nameof(AcquireTimeout));
        if (MaxRetries < 0)
            throw new ArgumentException("MaxRetries cannot be negative.", nameof(MaxRetries));
    }
}
