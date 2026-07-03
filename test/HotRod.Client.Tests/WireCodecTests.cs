using System.Buffers;
using System.IO.Pipelines;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Round-trips the wire primitives in <see cref="HotRodCodec"/>: what the write side encodes,
/// the read side must decode back to the same value, including the edge cases (zero, multi-byte
/// varints, negative values, empty arrays) that have historically shifted the whole frame by a byte.
/// </summary>
public class WireCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]      // largest single-byte varint
    [InlineData(128)]      // first two-byte varint
    [InlineData(300)]
    [InlineData(16_384)]   // first three-byte varint
    [InlineData(int.MaxValue)]
    [InlineData(-1)]       // written as uint, so it round-trips as the same bits
    public async Task VInt_round_trips(int value)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(writer, value);

        int read = await HotRodCodec.ReadVIntAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(30_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public async Task VLong_round_trips(long value)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVLong(writer, value);

        long read = await HotRodCodec.ReadVLongAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(0x0123456789ABCDEFL)]
    public async Task Long_round_trips_big_endian(long value)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteLong(writer, value);

        Assert.Equal(8, writer.WrittenCount);
        long read = await HotRodCodec.ReadLongAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(value, read);
    }

    [Fact]
    public void Long_is_written_most_significant_byte_first()
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteLong(writer, 1);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, writer.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 0, 255, 7, 42, 128 })]
    public async Task Array_round_trips(byte[] data)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(writer, data);

        byte[] read = await HotRodCodec.ReadArrayAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(data, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Amsterdam")]
    [InlineData("naïve café — €")] // multi-byte UTF-8
    public async Task String_round_trips(string value)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(writer, System.Text.Encoding.UTF8.GetBytes(value));

        string read = await HotRodCodec.ReadStringAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(value, read);
    }

    [Theory]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(11222, 0x2B, 0xD6)]
    [InlineData(65535, 0xFF, 0xFF)]
    public async Task UShort_reads_big_endian(int expected, byte high, byte low)
    {
        int read = await HotRodCodec.ReadUShortAsync(TestPipe.Reader(high, low), CancellationToken.None);

        Assert.Equal(expected, read);
    }

    [Fact]
    public async Task Reading_past_the_end_throws_HotRodException()
    {
        PipeReader empty = TestPipe.Reader();

        await Assert.ThrowsAsync<HotRodException>(
            async () => await HotRodCodec.ReadByteAsync(empty, CancellationToken.None));
    }

    [Fact]
    public async Task An_overlong_varint_throws_HotRodException()
    {
        // Six continuation bytes: more than a 32-bit varint may span.
        PipeReader reader = TestPipe.Reader(0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        await Assert.ThrowsAsync<HotRodException>(
            async () => await HotRodCodec.ReadVIntAsync(reader, CancellationToken.None));
    }
}
