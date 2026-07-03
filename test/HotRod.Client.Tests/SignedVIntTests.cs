using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// A signed vInt is zigzag-encoded (Infinispan's <c>SignedNumeric</c>) then written as an unsigned
/// vInt — used by iteration for the "-1 = none" segments and filter sizes. encode(-1)=1, encode(0)=0,
/// encode(1)=2; the round-trip must recover the original signed value.
/// </summary>
public class SignedVIntTests
{
    [Theory]
    [InlineData(0, 0)]    // encode(0) = 0
    [InlineData(-1, 1)]   // encode(-1) = 1  (the "none" sentinel)
    [InlineData(1, 2)]    // encode(1) = 2
    [InlineData(-2, 3)]
    [InlineData(63, 126)]
    public void Zigzag_encodes_to_the_expected_unsigned_value(int signed, int expectedUnsigned)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteSignedVInt(writer, signed);

        // The unsigned reader sees the zigzag-encoded value.
        var unsignedWriter = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(unsignedWriter, expectedUnsigned);
        Assert.Equal(unsignedWriter.WrittenSpan.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(-256)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task Signed_vInt_round_trips(int value)
    {
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteSignedVInt(writer, value);

        int read = await HotRodCodec.ReadSignedVIntAsync(TestPipe.Reader(writer), CancellationToken.None);

        Assert.Equal(value, read);
    }
}
