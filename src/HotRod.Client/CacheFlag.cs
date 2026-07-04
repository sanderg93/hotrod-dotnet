namespace HotRod.Client;

/// <summary>
/// Per-operation flags OR-ed into the request header's flags field. They tune a single call's
/// behaviour — most notably <see cref="ForceReturnValue"/>, which makes a write return the value it
/// replaced or removed. Values mirror Infinispan's <c>org.infinispan.client.hotrod.Flag</c> bits.
/// </summary>
[Flags]
public enum CacheFlag
{
    /// <summary>No flags set; default request behaviour.</summary>
    None = 0,

    /// <summary>Make a write return the previous value (otherwise the body carries no prior value).</summary>
    ForceReturnValue = 0x0001,

    /// <summary>Ignore any supplied lifespan and use the cache's configured default instead.</summary>
    DefaultLifespan = 0x0002,

    /// <summary>Ignore any supplied max-idle and use the cache's configured default instead.</summary>
    DefaultMaxIdle = 0x0004,

    /// <summary>Do not load the entry from a cache store/loader before the operation.</summary>
    SkipCacheLoad = 0x0008,

    /// <summary>Skip indexing the entry written by this operation.</summary>
    SkipIndexing = 0x0010,

    /// <summary>Do not fire client/server listener notifications for this operation.</summary>
    SkipListenerNotification = 0x0020,
}
