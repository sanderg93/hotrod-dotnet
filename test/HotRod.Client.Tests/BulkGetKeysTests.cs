using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// A bulkGetKeys response body: a run of [1-byte more-flag, length-prefixed key array] pairs,
/// terminated by a zero more-flag. <see cref="HotRodCodec.ReadBulkKeysAsync"/> reads one such body.
/// </summary>
public class BulkGetKeysTests
{
    [Fact]
    public async Task Reads_keys_until_the_terminating_zero_flag()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 1);              // more
        HotRodCodec.WriteArray(w, "k1"u8.ToArray());
        HotRodCodec.WriteByte(w, 1);              // more
        HotRodCodec.WriteArray(w, "k2"u8.ToArray());
        HotRodCodec.WriteByte(w, 1);              // more
        HotRodCodec.WriteArray(w, "k3"u8.ToArray());
        HotRodCodec.WriteByte(w, 0);              // done

        IReadOnlyList<byte[]> keys = await HotRodCodec.ReadBulkKeysAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(3, keys.Count);
        Assert.Equal("k1"u8.ToArray(), keys[0]);
        Assert.Equal("k2"u8.ToArray(), keys[1]);
        Assert.Equal("k3"u8.ToArray(), keys[2]);
    }

    [Fact]
    public async Task An_empty_cache_reads_no_keys_and_stops_at_the_zero_flag()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 0);              // done immediately
        // A trailing byte that must NOT be consumed (would be the next response's first byte).
        HotRodCodec.WriteByte(w, 0xA1);
        var reader = TestPipe.Reader(w);

        IReadOnlyList<byte[]> keys = await HotRodCodec.ReadBulkKeysAsync(reader, CancellationToken.None);

        Assert.Empty(keys);
        Assert.Equal(0xA1, await HotRodCodec.ReadByteAsync(reader, CancellationToken.None));
    }
}
