using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// A cache value together with the server-side metadata returned by <c>GetWithMetadata</c>: its
/// version (for optimistic concurrency), when it was created and last used, and its remaining
/// lifespan/max-idle. A null timestamp or duration means that dimension never expires.
/// </summary>
public sealed record MetadataValue<T>(
    T Value,
    long Version,
    DateTimeOffset? Created,
    TimeSpan? Lifespan,
    DateTimeOffset? LastUsed,
    TimeSpan? MaxIdle);

/// <summary>Builds a <see cref="MetadataValue{T}"/> from the wire-level <see cref="EntryMetadata"/>.</summary>
internal static class MetadataValues
{
    public static MetadataValue<T> From<T>(in EntryMetadata m, T value) =>
        new(value, m.Version, Timestamp(m.Created), Duration(m.Lifespan), Timestamp(m.LastUsed), Duration(m.MaxIdle));

    /// <summary>Epoch milliseconds to a point in time; the -1 sentinel (infinite) becomes null.</summary>
    internal static DateTimeOffset? Timestamp(long epochMillis) =>
        epochMillis < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(epochMillis);

    /// <summary>Seconds to a duration; the -1 sentinel (infinite) becomes null.</summary>
    internal static TimeSpan? Duration(int seconds) =>
        seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
}
