using HotRod.Client;

namespace HotRod.Client.IntegrationTests;

/// <summary>
/// End-to-end smoke tests that drive the real client against a live Infinispan server: a basic
/// connect + put/get round-trip, a near cache that is populated by a read and invalidated by a
/// server-pushed change event, and bulk key retrieval. Tests in a class run sequentially, so each
/// clears the shared cache to isolate itself.
/// </summary>
public sealed class SmokeTests : IClassFixture<InfinispanFixture>
{
    private readonly InfinispanFixture _server;

    public SmokeTests(InfinispanFixture server) => _server = server;

    [Fact]
    public async Task Connect_put_get_roundtrip()
    {
        await using HotRodClient client = await _server.ConnectAsync();
        RemoteCache cache = client.GetCache(InfinispanFixture.CacheName);
        await cache.ClearAsync();

        await cache.PutAsync("greeting", "hello");

        Assert.Equal("hello", await cache.GetAsync("greeting"));
        Assert.Null(await cache.GetAsync("absent"));
        Assert.True(await cache.ContainsKeyAsync("greeting"));
        Assert.Equal(1, await cache.SizeAsync());

        Assert.True(await cache.RemoveAsync("greeting"));
        Assert.False(await cache.ContainsKeyAsync("greeting"));

        Assert.NotEmpty(client.Servers);
    }

    [Fact]
    public async Task NearCache_populates_then_invalidates_on_remote_change()
    {
        await using HotRodClient client = await _server.ConnectAsync();
        await using RemoteCache cache =
            await client.GetCacheAsync(InfinispanFixture.CacheName, nearCache: new NearCacheOptions());
        await cache.ClearAsync();

        await cache.PutAsync("nk", "v1");

        // First read misses locally and populates the near cache; the second is served from it.
        Assert.Equal("v1", await cache.GetAsync("nk"));
        Assert.Equal("v1", await cache.GetAsync("nk"));

        NearCacheStatistics afterHit = cache.NearCacheStats!.Value;
        Assert.True(afterHit.Hits >= 1, $"expected a local hit, stats were {afterHit}");

        // A change made through a separate client must reach this near cache as a server event and
        // drop the stale local copy; the next read then falls through and returns the new value.
        await using HotRodClient writer = await _server.ConnectAsync();
        await writer.GetCache(InfinispanFixture.CacheName).PutAsync("nk", "v2");

        string? latest = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            latest = await cache.GetAsync("nk");
            if (latest == "v2")
                break;
            await Task.Delay(100);
        }

        Assert.Equal("v2", latest);
    }

    [Fact]
    public async Task GetKeys_returns_every_key()
    {
        await using HotRodClient client = await _server.ConnectAsync();
        RemoteCache cache = client.GetCache(InfinispanFixture.CacheName);
        await cache.ClearAsync();

        await cache.PutAsync("k1", "v1");
        await cache.PutAsync("k2", "v2");
        await cache.PutAsync("k3", "v3");

        IReadOnlyList<string> keys = await cache.GetKeyStringsAsync();

        Assert.Equal(new[] { "k1", "k2", "k3" }, keys.OrderBy(k => k).ToArray());
    }
}
