using HotRod.Client;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the behaviour of the near-cache store that backs an invalidated near cache: reads populate and
/// count hits/misses, change events and writes invalidate, byte-array keys compare by content, the
/// placeholder-guarded populate refuses to cache once its slot was cleared, and a positive entry cap
/// evicts the least-recently-used entry.
/// </summary>
public class NearCacheTests
{
    private static MetadataValue<byte[]> Value(byte[] value, long version = 1) =>
        new(value, version, null, null, null, null);

    [Fact]
    public void Get_after_populate_is_a_hit_and_returns_the_value()
    {
        var near = new NearCache(maxEntries: 0);
        byte[] key = "Stad"u8.ToArray();
        MetadataValue<byte[]> value = Value("Amsterdam"u8.ToArray());

        Assert.True(near.PutIfAbsent(key, NearCache.Placeholder));
        Assert.True(near.Replace(key, NearCache.Placeholder, value));

        Assert.Same(value, near.Get(key));
        NearCacheStatistics stats = near.Snapshot();
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Size);
    }

    [Fact]
    public void Get_of_an_absent_key_is_a_miss()
    {
        var near = new NearCache(maxEntries: 0);

        Assert.Null(near.Get("missing"u8.ToArray()));
        Assert.Equal(1, near.Snapshot().Misses);
    }

    [Fact]
    public void A_bare_placeholder_reads_as_a_miss()
    {
        var near = new NearCache(maxEntries: 0);
        byte[] key = "k"u8.ToArray();

        near.PutIfAbsent(key, NearCache.Placeholder);

        Assert.Null(near.Get(key)); // in-flight populate is not yet a value
        Assert.Equal(1, near.Snapshot().Misses);
    }

    [Fact]
    public void Keys_are_compared_by_content_not_reference()
    {
        var near = new NearCache(maxEntries: 0);
        MetadataValue<byte[]> value = Value([1, 2, 3]);

        near.PutIfAbsent([1, 2, 3], NearCache.Placeholder);
        near.Replace([1, 2, 3], NearCache.Placeholder, value);

        Assert.Same(value, near.Get([1, 2, 3])); // a different array instance with the same bytes
    }

    [Fact]
    public void Remove_invalidates_the_entry()
    {
        var near = new NearCache(maxEntries: 0);
        byte[] key = "k"u8.ToArray();
        near.PutIfAbsent(key, NearCache.Placeholder);
        near.Replace(key, NearCache.Placeholder, Value([9]));

        Assert.True(near.Remove(key));
        Assert.Null(near.Get(key));
        Assert.Equal(0, near.Snapshot().Size);
    }

    [Fact]
    public void Replace_fails_once_the_placeholder_was_invalidated()
    {
        var near = new NearCache(maxEntries: 0);
        byte[] key = "k"u8.ToArray();

        near.PutIfAbsent(key, NearCache.Placeholder);
        near.Remove(key); // an invalidation event lands mid-populate

        Assert.False(near.Replace(key, NearCache.Placeholder, Value([1])));
        Assert.Null(near.Get(key)); // the fetched value was therefore not cached
    }

    [Fact]
    public void PutIfAbsent_does_not_overwrite_an_existing_entry()
    {
        var near = new NearCache(maxEntries: 0);
        byte[] key = "k"u8.ToArray();
        MetadataValue<byte[]> first = Value([1]);
        near.PutIfAbsent(key, NearCache.Placeholder);
        near.Replace(key, NearCache.Placeholder, first);

        Assert.False(near.PutIfAbsent(key, Value([2])));
        Assert.Same(first, near.Get(key));
    }

    [Fact]
    public void A_bounded_cache_evicts_the_least_recently_used_entry()
    {
        var near = new NearCache(maxEntries: 2);
        Populate(near, "a"u8.ToArray(), [1]);
        Populate(near, "b"u8.ToArray(), [2]);

        // Touch "a" so "b" becomes the least-recently-used, then admit "c".
        Assert.NotNull(near.Get("a"u8.ToArray()));
        Populate(near, "c"u8.ToArray(), [3]);

        Assert.Equal(2, near.Snapshot().Size);
        Assert.Null(near.Get("b"u8.ToArray())); // evicted
        Assert.NotNull(near.Get("a"u8.ToArray()));
        Assert.NotNull(near.Get("c"u8.ToArray()));
    }

    [Fact]
    public void Clear_empties_the_store()
    {
        var near = new NearCache(maxEntries: 0);
        Populate(near, "a"u8.ToArray(), [1]);
        Populate(near, "b"u8.ToArray(), [2]);

        near.Clear();

        Assert.Equal(0, near.Snapshot().Size);
        Assert.Null(near.Get("a"u8.ToArray()));
    }

    private static void Populate(NearCache near, byte[] key, byte[] value)
    {
        near.PutIfAbsent(key, NearCache.Placeholder);
        near.Replace(key, NearCache.Placeholder, Value(value));
    }
}
