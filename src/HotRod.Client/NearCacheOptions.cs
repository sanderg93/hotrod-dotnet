namespace HotRod.Client;

/// <summary>
/// Enables and tunes a client-side near cache for a single <see cref="RemoteCache"/>. A near cache keeps
/// a local copy of entries returned by reads, so repeated reads of the same key are served locally
/// instead of from the server. It runs in invalidated mode: a client listener watches the cache and
/// drops the local copy the moment the entry is changed, removed, or expired on the server, and the
/// cache's own writes invalidate their key too. Pass this to
/// <see cref="HotRodClient.GetCacheAsync(string?, CacheEncoding?, NearCacheOptions?, System.Threading.CancellationToken)"/>.
/// </summary>
public sealed record NearCacheOptions
{
    /// <summary>
    /// The maximum number of entries to hold locally. When the cache is full the least-recently-used
    /// entry is evicted to admit a new one. Zero (the default) leaves the near cache unbounded.
    /// </summary>
    public int MaxEntries { get; init; }

    internal void Validate()
    {
        if (MaxEntries < 0)
            throw new ArgumentException("MaxEntries cannot be negative (use 0 for an unbounded near cache).", nameof(MaxEntries));
    }
}

/// <summary>
/// A point-in-time snapshot of a near cache's activity: <see cref="Hits"/> reads served locally,
/// <see cref="Misses"/> reads that fell through to the server, and <see cref="Size"/> entries currently
/// held (including any in-flight placeholders).
/// </summary>
public readonly record struct NearCacheStatistics(long Hits, long Misses, int Size);
