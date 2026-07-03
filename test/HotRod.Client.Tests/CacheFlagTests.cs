using HotRod.Client;

namespace HotRod.Client.Tests;

/// <summary>
/// The public <see cref="CacheFlag"/> values are OR-ed into the request header's flags field, so each
/// must equal Infinispan's protocol bit exactly and combine as a normal bit set.
/// </summary>
public class CacheFlagTests
{
    [Fact]
    public void Values_match_the_protocol_bits()
    {
        Assert.Equal(0x0000, (int)CacheFlag.None);
        Assert.Equal(0x0001, (int)CacheFlag.ForceReturnValue);
        Assert.Equal(0x0002, (int)CacheFlag.DefaultLifespan);
        Assert.Equal(0x0004, (int)CacheFlag.DefaultMaxIdle);
        Assert.Equal(0x0008, (int)CacheFlag.SkipCacheLoad);
        Assert.Equal(0x0010, (int)CacheFlag.SkipIndexing);
        Assert.Equal(0x0020, (int)CacheFlag.SkipListenerNotification);
    }

    [Fact]
    public void Flags_combine_with_bitwise_or()
    {
        CacheFlag combined = CacheFlag.ForceReturnValue | CacheFlag.SkipCacheLoad;

        Assert.Equal(0x09, (int)combined);
        Assert.True(combined.HasFlag(CacheFlag.ForceReturnValue));
        Assert.True(combined.HasFlag(CacheFlag.SkipCacheLoad));
        Assert.False(combined.HasFlag(CacheFlag.SkipIndexing));
    }
}
