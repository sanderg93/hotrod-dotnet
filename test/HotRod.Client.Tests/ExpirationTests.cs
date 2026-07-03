using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Verifies how <see cref="Expiration"/> encodes onto the wire: a single time-units byte (lifespan
/// in the high nibble, maxIdle in the low) followed by a vLong of milliseconds only for the units
/// that are actually set. A default expiration is the single byte 0x77 with no values.
/// </summary>
public class ExpirationTests
{
    private static byte[] Encode(Expiration expiration)
    {
        var writer = new ArrayBufferWriter<byte>();
        expiration.WriteTo(writer);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void Default_writes_only_the_default_default_units_byte()
    {
        byte[] bytes = Encode(Expiration.Default);

        Assert.Equal(new byte[] { Constants.TimeUnitDefaultBoth }, bytes);
        Assert.Equal(0x77, Constants.TimeUnitDefaultBoth);
    }

    [Fact]
    public void Lifespan_sets_the_high_nibble_to_milliseconds_and_appends_the_value()
    {
        byte[] bytes = Encode(new Expiration(lifespan: TimeSpan.FromSeconds(30)));

        byte expectedUnits = (Constants.TimeUnitMilliseconds << 4) | Constants.TimeUnitDefault;
        Assert.Equal(expectedUnits, bytes[0]);                 // 0x17: lifespan=ms, maxIdle=default
        Assert.Equal(30_000L, ReadVLong(bytes, start: 1));     // 30s as milliseconds
    }

    [Fact]
    public void MaxIdle_sets_the_low_nibble_to_milliseconds_and_appends_the_value()
    {
        byte[] bytes = Encode(new Expiration(maxIdle: TimeSpan.FromMinutes(5)));

        byte expectedUnits = (Constants.TimeUnitDefault << 4) | Constants.TimeUnitMilliseconds;
        Assert.Equal(expectedUnits, bytes[0]);                 // 0x71: lifespan=default, maxIdle=ms
        Assert.Equal(300_000L, ReadVLong(bytes, start: 1));
    }

    [Fact]
    public void Both_durations_append_lifespan_then_maxIdle()
    {
        byte[] bytes = Encode(new Expiration(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)));

        byte expectedUnits = (Constants.TimeUnitMilliseconds << 4) | Constants.TimeUnitMilliseconds;
        Assert.Equal(expectedUnits, bytes[0]);                 // 0x11

        int offset = 1;
        Assert.Equal(10_000L, ReadVLong(bytes, ref offset));   // lifespan first
        Assert.Equal(2_000L, ReadVLong(bytes, ref offset));    // maxIdle second
        Assert.Equal(bytes.Length, offset);                    // nothing trailing
    }

    private static long ReadVLong(byte[] bytes, int start)
    {
        int offset = start;
        return ReadVLong(bytes, ref offset);
    }

    private static long ReadVLong(byte[] bytes, ref int offset)
    {
        long value = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[offset++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }
}
