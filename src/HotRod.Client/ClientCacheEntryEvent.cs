namespace HotRod.Client;

/// <summary>
/// A single cache-entry change pushed by the server to a registered listener. <see cref="Key"/> is the
/// raw key in the cache's storage format (decode it with the same encoding the cache uses).
/// <see cref="Version"/> carries the entry's new version for <see cref="ClientEventType.Created"/> and
/// <see cref="ClientEventType.Modified"/> events and is null for removals and expirations.
/// <see cref="CommandRetried"/> is true when the server re-sent the event after a topology change, so
/// a handler that must be exactly-once can deduplicate.
/// </summary>
public sealed record ClientCacheEntryEvent(
    ClientEventType Type,
    byte[] Key,
    long? Version,
    bool CommandRetried);
