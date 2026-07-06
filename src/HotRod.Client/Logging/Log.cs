using Microsoft.Extensions.Logging;

namespace HotRod.Client.Logging;

/// <summary>
/// Source-generated, zero-allocation log methods for the client's key lifecycle events: connection
/// open/close, SASL authentication success/failure, cluster topology change, node failover/retry,
/// and connection-pool exhaustion. Each maps to a fixed <see cref="HotRodEventIds"/> id. Callers pass
/// the <see cref="ILogger"/> for their category; a <c>NullLogger</c> makes every call a no-op.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = HotRodEventIds.ConnectionOpened,
        EventName = nameof(HotRodEventIds.ConnectionOpened),
        Level = LogLevel.Debug,
        Message = "Opened connection to {Host}:{Port}")]
    public static partial void ConnectionOpened(ILogger logger, string host, int port);

    [LoggerMessage(
        EventId = HotRodEventIds.ConnectionClosed,
        EventName = nameof(HotRodEventIds.ConnectionClosed),
        Level = LogLevel.Debug,
        Message = "Closed connection to {Host}:{Port}")]
    public static partial void ConnectionClosed(ILogger logger, string host, int port);

    [LoggerMessage(
        EventId = HotRodEventIds.AuthenticationSucceeded,
        EventName = nameof(HotRodEventIds.AuthenticationSucceeded),
        Level = LogLevel.Debug,
        Message = "SASL authentication succeeded for user {User} using {Mechanism}")]
    public static partial void AuthenticationSucceeded(ILogger logger, string user, SaslMechanism mechanism);

    [LoggerMessage(
        EventId = HotRodEventIds.AuthenticationFailed,
        EventName = nameof(HotRodEventIds.AuthenticationFailed),
        Level = LogLevel.Error,
        Message = "SASL authentication failed for user {User} using {Mechanism}")]
    public static partial void AuthenticationFailed(ILogger logger, string user, SaslMechanism mechanism, Exception exception);

    [LoggerMessage(
        EventId = HotRodEventIds.TopologyChanged,
        EventName = nameof(HotRodEventIds.TopologyChanged),
        Level = LogLevel.Information,
        Message = "Cluster topology for cache '{CacheName}' changed to id {TopologyId} with {NodeCount} node(s)")]
    public static partial void TopologyChanged(ILogger logger, string cacheName, int topologyId, int nodeCount);

    [LoggerMessage(
        EventId = HotRodEventIds.NodeFailover,
        EventName = nameof(HotRodEventIds.NodeFailover),
        Level = LogLevel.Warning,
        Message = "Retrying operation on node {Host}:{Port} (attempt {Attempt})")]
    public static partial void NodeFailover(ILogger logger, string host, int port, int attempt);

    [LoggerMessage(
        EventId = HotRodEventIds.PoolExhausted,
        EventName = nameof(HotRodEventIds.PoolExhausted),
        Level = LogLevel.Warning,
        Message = "Connection pool for {Host}:{Port} exhausted; timed out after {Timeout} waiting for a free connection")]
    public static partial void PoolExhausted(ILogger logger, string host, int port, TimeSpan timeout);
}
