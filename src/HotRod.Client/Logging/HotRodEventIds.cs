namespace HotRod.Client.Logging;

/// <summary>
/// Stable numeric ids for the client's structured log events, grouped by area (connection 10xx,
/// authentication 11xx, topology 12xx, failover 13xx, pool 14xx). Referenced from the
/// <see cref="Log"/> message definitions so each event keeps a fixed <see cref="Microsoft.Extensions.Logging.EventId"/>.
/// </summary>
internal static class HotRodEventIds
{
    public const int ConnectionOpened = 1000;
    public const int ConnectionClosed = 1001;

    public const int AuthenticationSucceeded = 1100;
    public const int AuthenticationFailed = 1101;

    public const int TopologyChanged = 1200;

    public const int NodeFailover = 1300;

    public const int PoolExhausted = 1400;
}
