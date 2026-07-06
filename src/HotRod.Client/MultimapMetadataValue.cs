using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// A multimap key's values together with the server-side metadata returned by
/// <see cref="MultimapCache.GetWithMetadataAsync(byte[], CancellationToken)"/>: the entry's version (for
/// optimistic concurrency), when it was created and last used, and its remaining lifespan/max-idle. A
/// null timestamp or duration means that dimension never expires.
/// </summary>
public sealed record MultimapMetadataValue<T>(
    IReadOnlyList<T> Values,
    long Version,
    DateTimeOffset? Created,
    TimeSpan? Lifespan,
    DateTimeOffset? LastUsed,
    TimeSpan? MaxIdle);

/// <summary>Builds a <see cref="MultimapMetadataValue{T}"/> from the wire-level <see cref="EntryMetadata"/>.</summary>
internal static class MultimapMetadataValues
{
    public static MultimapMetadataValue<T> From<T>(in EntryMetadata m, IReadOnlyList<T> values) =>
        new(values, m.Version, MetadataValues.Timestamp(m.Created), MetadataValues.Duration(m.Lifespan),
            MetadataValues.Timestamp(m.LastUsed), MetadataValues.Duration(m.MaxIdle));
}
