using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// The ProtoStream marshaller wraps a primitive as a <c>WrappedMessage</c> whose scalar field number
/// (from protostream's message-wrapping.proto) selects the CLR type. These tests round-trip each
/// supported type and pin the leading field tag for the numeric wrappers.
/// </summary>
public class ProtoStreamMarshallingTests
{
    private static readonly ProtoStreamMarshaller Marshaller = ProtoStreamMarshaller.Instance;

    [Fact]
    public void Round_trips_an_int()
    {
        byte[] wire = Marshaller.MarshalValue(42);
        Assert.Equal(42, Assert.IsType<int>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void Round_trips_a_negative_int()
    {
        byte[] wire = Marshaller.MarshalValue(-7);
        Assert.Equal(-7, Assert.IsType<int>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void Round_trips_a_long()
    {
        long value = 9_000_000_000L;
        byte[] wire = Marshaller.MarshalValue(value);
        Assert.Equal(value, Assert.IsType<long>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void Round_trips_a_double()
    {
        byte[] wire = Marshaller.MarshalValue(3.14159d);
        Assert.Equal(3.14159d, Assert.IsType<double>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void Round_trips_a_float()
    {
        byte[] wire = Marshaller.MarshalValue(2.5f);
        Assert.Equal(2.5f, Assert.IsType<float>(Marshaller.UnmarshalValue(wire)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Round_trips_a_bool(bool value)
    {
        byte[] wire = Marshaller.MarshalValue(value);
        Assert.Equal(value, Assert.IsType<bool>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void Round_trips_a_string()
    {
        byte[] wire = Marshaller.MarshalValue("hello");
        Assert.Equal("hello", Assert.IsType<string>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void Round_trips_bytes()
    {
        byte[] value = [1, 2, 3, 250];
        byte[] wire = Marshaller.MarshalValue(value);
        Assert.Equal(value, Assert.IsType<byte[]>(Marshaller.UnmarshalValue(wire)));
    }

    [Fact]
    public void The_string_typed_overrides_still_round_trip()
    {
        byte[] wire = Marshaller.Marshal("console-visible");
        Assert.Equal("console-visible", Marshaller.Unmarshal(wire));
    }

    [Fact]
    public void An_int_leads_with_the_wrappedInt32_tag()
    {
        // wrappedInt32 is field 5, wire type 0 (varint): tag = (5 << 3) | 0 = 0x28.
        byte[] wire = Marshaller.MarshalValue(1);
        Assert.Equal(0x28, wire[0]);
        Assert.Equal(0x01, wire[1]); // the varint payload
    }

    [Fact]
    public void A_long_leads_with_the_wrappedInt64_tag()
    {
        // wrappedInt64 is field 3, wire type 0 (varint): tag = (3 << 3) | 0 = 0x18.
        byte[] wire = Marshaller.MarshalValue(1L);
        Assert.Equal(0x18, wire[0]);
    }

    [Fact]
    public void A_double_leads_with_the_wrappedDouble_tag()
    {
        // wrappedDouble is field 1, wire type 1 (fixed64): tag = (1 << 3) | 1 = 0x09.
        byte[] wire = Marshaller.MarshalValue(1.0d);
        Assert.Equal(0x09, wire[0]);
        Assert.Equal(9, wire.Length); // one tag byte + eight little-endian bytes
    }

    [Fact]
    public void A_float_leads_with_the_wrappedFloat_tag()
    {
        // wrappedFloat is field 2, wire type 5 (fixed32): tag = (2 << 3) | 5 = 0x15.
        byte[] wire = Marshaller.MarshalValue(1.0f);
        Assert.Equal(0x15, wire[0]);
        Assert.Equal(5, wire.Length); // one tag byte + four little-endian bytes
    }

    [Fact]
    public void An_unsupported_type_is_rejected()
    {
        Assert.Throws<HotRodException>(() => Marshaller.MarshalValue(new object()));
    }
}
