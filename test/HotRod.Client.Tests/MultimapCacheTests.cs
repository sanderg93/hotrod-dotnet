using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Covers the multimap wire format (<see cref="MultimapCodec"/>) and the
/// <see cref="MultimapMetadataValue{T}"/> mapping without a server, plus (opt-in) a live end-to-end
/// exercise of <see cref="MultimapCache"/>. The layout tests run unconditionally; the <c>Live_</c> tests
/// require a real server and are skipped unless <c>HOTROD_LIVE=1</c> (host/port overridable via
/// <c>HOTROD_HOST</c>/<c>HOTROD_PORT</c>), matching the pattern in <see cref="AdminManagerTests"/>.
/// </summary>
public class MultimapCacheTests
{
    // -- MultimapCodec: layout -------------------------------------------------

    [Theory]
    [InlineData(false, 0x00)]
    [InlineData(true, 0x01)]
    public void Supports_duplicates_flag_is_a_single_byte(bool supportsDuplicates, byte expected)
    {
        var writer = new ArrayBufferWriter<byte>();
        MultimapCodec.WriteSupportsDuplicates(writer, supportsDuplicates);

        Assert.Equal(new[] { expected }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Infinite_expiration_is_both_nibbles_set_to_the_infinite_time_unit_with_no_trailing_vlongs()
    {
        var writer = new ArrayBufferWriter<byte>();
        MultimapCodec.WriteInfiniteExpiration(writer);

        byte expected = (byte)((Constants.TimeUnitInfinite << 4) | Constants.TimeUnitInfinite);
        Assert.Equal(new[] { expected }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task Value_collection_round_trips_a_vint_count_and_that_many_arrays()
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(writer, 3);
        HotRodCodec.WriteArray(writer, "a"u8.ToArray());
        HotRodCodec.WriteArray(writer, "bb"u8.ToArray());
        HotRodCodec.WriteArray(writer, []);

        IReadOnlyList<byte[]> values = await MultimapCodec.ReadValueCollectionAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(3, values.Count);
        Assert.Equal("a"u8.ToArray(), values[0]);
        Assert.Equal("bb"u8.ToArray(), values[1]);
        Assert.Empty(values[2]);
    }

    [Fact]
    public async Task Empty_value_collection_reads_a_zero_count_and_no_arrays()
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(writer, 0);

        IReadOnlyList<byte[]> values = await MultimapCodec.ReadValueCollectionAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Empty(values);
    }

    [Fact]
    public async Task Bool_response_is_false_without_a_body_when_the_key_does_not_exist()
    {
        // No body follows this status; a following field must stay readable.
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(writer, "next-field"u8.ToArray());
        var reader = TestPipe.Reader(writer);

        bool result = await MultimapCodec.ReadBoolResponseAsync(Constants.StatusKeyDoesNotExist, reader, CancellationToken.None);

        Assert.False(result);
        byte[] following = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        Assert.Equal("next-field"u8.ToArray(), following);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task Bool_response_reads_a_single_byte_on_success(byte wireByte, bool expected)
    {
        bool result = await MultimapCodec.ReadBoolResponseAsync(Constants.StatusSuccess, TestPipe.Reader(wireByte), CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // -- MultimapMetadataValue mapping -----------------------------------------

    [Fact]
    public void Finite_metadata_maps_timestamps_durations_and_values()
    {
        var meta = new EntryMetadata(Created: 1_000, Lifespan: 60, LastUsed: 2_000, MaxIdle: 30, Version: 99);
        IReadOnlyList<string> values = ["a", "b"];

        MultimapMetadataValue<string> mv = MultimapMetadataValues.From(meta, values);

        Assert.Equal(values, mv.Values);
        Assert.Equal(99, mv.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000), mv.Created);
        Assert.Equal(TimeSpan.FromSeconds(60), mv.Lifespan);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2_000), mv.LastUsed);
        Assert.Equal(TimeSpan.FromSeconds(30), mv.MaxIdle);
    }

    [Fact]
    public void Infinite_dimensions_map_to_null()
    {
        var meta = new EntryMetadata(Created: -1, Lifespan: -1, LastUsed: -1, MaxIdle: -1, Version: 5);

        MultimapMetadataValue<string> mv = MultimapMetadataValues.From(meta, (IReadOnlyList<string>)["v"]);

        Assert.Null(mv.Created);
        Assert.Null(mv.Lifespan);
        Assert.Null(mv.LastUsed);
        Assert.Null(mv.MaxIdle);
        Assert.Equal(5, mv.Version);
    }

    // -- Live end-to-end (opt-in) ----------------------------------------------

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("HOTROD_LIVE") == "1";

    private static async Task<HotRodClient> ConnectLiveAsync()
    {
        string host = Environment.GetEnvironmentVariable("HOTROD_HOST") ?? "127.0.0.1";
        int port = int.TryParse(Environment.GetEnvironmentVariable("HOTROD_PORT"), out int p) ? p : 11222;
        return await HotRodClient.ConnectAsync(host, port);
    }

    [Fact]
    public async Task Live_put_get_and_remove_values_under_a_key()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        MultimapCache multimap = client.GetMultimapCache("multimap-" + Guid.NewGuid().ToString("N"));
        string key = "k" + Guid.NewGuid().ToString("N");

        Assert.False(await multimap.ContainsKeyAsync(key));
        Assert.Empty(await multimap.GetAsync(key));

        await multimap.PutAsync(key, "v1");
        await multimap.PutAsync(key, "v2");

        Assert.True(await multimap.ContainsKeyAsync(key));
        Assert.True(await multimap.ContainsEntryAsync(key, "v1"));
        Assert.True(await multimap.ContainsValueAsync("v2"));
        Assert.False(await multimap.ContainsEntryAsync(key, "nope"));

        IReadOnlyList<string> values = await multimap.GetAsync(key);
        Assert.Equal(2, values.Count);
        Assert.Contains("v1", values);
        Assert.Contains("v2", values);

        Assert.True(await multimap.RemoveEntryAsync(key, "v1"));
        Assert.False(await multimap.RemoveEntryAsync(key, "v1")); // already gone

        Assert.True(await multimap.RemoveAsync(key));
        Assert.False(await multimap.ContainsKeyAsync(key));
    }

    [Fact]
    public async Task Live_get_with_metadata_returns_values_and_version()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        MultimapCache multimap = client.GetMultimapCache("multimap-meta-" + Guid.NewGuid().ToString("N"));
        string key = "k" + Guid.NewGuid().ToString("N");

        Assert.Null(await multimap.GetWithMetadataAsync(key));

        await multimap.PutAsync(key, "v1");

        MultimapMetadataValue<string>? meta = await multimap.GetWithMetadataAsync(key);
        Assert.NotNull(meta);
        Assert.Contains("v1", meta!.Values);
    }

    [Fact]
    public async Task Live_size_counts_key_value_pairs_across_the_multimap()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        string cacheName = "multimap-size-" + Guid.NewGuid().ToString("N");
        MultimapCache multimap = client.GetMultimapCache(cacheName);

        Assert.Equal(0, await multimap.SizeAsync());

        await multimap.PutAsync("k1", "v1");
        await multimap.PutAsync("k1", "v2");
        await multimap.PutAsync("k2", "v1");

        Assert.Equal(3, await multimap.SizeAsync());
    }
}
