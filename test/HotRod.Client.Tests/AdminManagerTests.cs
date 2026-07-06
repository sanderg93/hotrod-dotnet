using System.Text;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Covers cache administration: the encoding of admin flags to the server's token list, the parsing of
/// the "@@cache@names" JSON result, and (opt-in) a live create/exists/remove lifecycle. The layout
/// tests run without a server; the <c>Live_</c> tests exercise a real server end-to-end and are skipped
/// unless <c>HOTROD_LIVE=1</c> (host/port overridable via <c>HOTROD_HOST</c>/<c>HOTROD_PORT</c>).
/// </summary>
public class AdminManagerTests
{
    [Fact]
    public void No_flags_encode_to_null_so_the_parameter_is_omitted()
    {
        Assert.Null(AdminCodec.EncodeFlags(AdminFlags.None));
    }

    [Theory]
    [InlineData(AdminFlags.Volatile, "VOLATILE")]
    [InlineData(AdminFlags.Update, "UPDATE")]
    [InlineData(AdminFlags.Volatile | AdminFlags.Update, "VOLATILE,UPDATE")]
    public void Flags_encode_to_a_comma_separated_upper_case_token_list(AdminFlags flags, string expected)
    {
        Assert.Equal(expected, AdminCodec.EncodeFlags(flags));
    }

    [Fact]
    public void Cache_names_parse_from_a_json_string_array()
    {
        byte[] json = Encoding.UTF8.GetBytes("[\"cache1\",\"cache2\",\"___script_cache\"]");

        IReadOnlyCollection<string> names = AdminCodec.ParseCacheNames(json);

        Assert.Equal(new[] { "cache1", "cache2", "___script_cache" }, names);
    }

    [Fact]
    public void Empty_cache_names_result_parses_to_an_empty_collection()
    {
        Assert.Empty(AdminCodec.ParseCacheNames([]));
        Assert.Empty(AdminCodec.ParseCacheNames(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public void Default_templates_are_inline_json_cache_configurations()
    {
        Assert.Equal("{\"local-cache\":{\"statistics\":\"true\"}}", DefaultTemplate.Local);
        Assert.Equal("{\"distributed-cache\":{\"mode\":\"SYNC\",\"statistics\":\"true\"}}", DefaultTemplate.DistributedSync);
        Assert.Equal("{\"replicated-cache\":{\"mode\":\"SYNC\",\"statistics\":\"true\"}}", DefaultTemplate.ReplicatedSync);
    }

    // -- Live end-to-end (opt-in) ------------------------------------------

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("HOTROD_LIVE") == "1";

    private static async Task<HotRodClient> ConnectLiveAsync()
    {
        string host = Environment.GetEnvironmentVariable("HOTROD_HOST") ?? "127.0.0.1";
        int port = int.TryParse(Environment.GetEnvironmentVariable("HOTROD_PORT"), out int p) ? p : 11222;
        return await HotRodClient.ConnectAsync(host, port);
    }

    [Fact]
    public async Task Live_create_use_and_remove_a_cache()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        AdminManager admin = client.Administration;
        string name = "admin-" + Guid.NewGuid().ToString("N");

        Assert.False(await admin.CacheExistsAsync(name));

        await admin.CreateCacheFromConfigurationAsync(name, DefaultTemplate.Local);
        try
        {
            Assert.True(await admin.CacheExistsAsync(name));
            Assert.Contains(name, await admin.GetCacheNamesAsync());

            // The created cache is usable through the normal cache API.
            RemoteCache cache = client.GetCache(name);
            await cache.PutAsync("k", "v");
            Assert.Equal("v", await cache.GetAsync("k"));
        }
        finally
        {
            await admin.RemoveCacheAsync(name);
        }

        Assert.False(await admin.CacheExistsAsync(name));
    }

    [Fact]
    public async Task Live_get_or_create_is_idempotent()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        AdminManager admin = client.Administration;
        string name = "admin-goc-" + Guid.NewGuid().ToString("N");

        await admin.GetOrCreateCacheFromConfigurationAsync(name, DefaultTemplate.Local);
        try
        {
            await admin.GetOrCreateCacheFromConfigurationAsync(name, DefaultTemplate.Local); // second call is a no-op
            Assert.True(await admin.CacheExistsAsync(name));
        }
        finally
        {
            await admin.RemoveCacheAsync(name);
        }
    }
}
