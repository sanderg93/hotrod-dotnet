using HotRod.Client.Marshalling;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// The serialization context backs the generic typed cache overloads. For the built-in scalar types it
/// must produce exactly the ProtoStream <c>WrappedMessage</c> bytes the primitive marshaller already
/// pins (so a typed <c>PutAsync&lt;T&gt;</c> is wire-compatible with the Java client), and round-trip
/// every one of them.
/// </summary>
public class GenericValueApiTests
{
    private static readonly SerializationContext Context = SerializationContext.Default;

    [Fact]
    public void Round_trips_an_int() =>
        Assert.Equal(42, Context.Unmarshal<int>(Context.Marshal(42)));

    [Fact]
    public void Round_trips_a_negative_int() =>
        Assert.Equal(-7, Context.Unmarshal<int>(Context.Marshal(-7)));

    [Fact]
    public void Round_trips_a_long() =>
        Assert.Equal(9_000_000_000L, Context.Unmarshal<long>(Context.Marshal(9_000_000_000L)));

    [Fact]
    public void Round_trips_a_double() =>
        Assert.Equal(3.14159d, Context.Unmarshal<double>(Context.Marshal(3.14159d)));

    [Fact]
    public void Round_trips_a_float() =>
        Assert.Equal(2.5f, Context.Unmarshal<float>(Context.Marshal(2.5f)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Round_trips_a_bool(bool value) =>
        Assert.Equal(value, Context.Unmarshal<bool>(Context.Marshal(value)));

    [Fact]
    public void Round_trips_a_string() =>
        Assert.Equal("hello", Context.Unmarshal<string>(Context.Marshal("hello")));

    [Fact]
    public void Round_trips_bytes()
    {
        byte[] value = [1, 2, 3, 250];
        Assert.Equal(value, Context.Unmarshal<byte[]>(Context.Marshal(value)));
    }

    [Fact]
    public void A_scalar_encodes_to_the_same_bytes_as_the_wrapped_primitive_marshaller()
    {
        // The typed overloads must be wire-identical to the already-pinned WrappedMessage scalar form.
        Assert.Equal(ProtoStreamMarshaller.Instance.MarshalValue(1234), Context.Marshal(1234));
        Assert.Equal(ProtoStreamMarshaller.Instance.MarshalValue("x"), Context.Marshal("x"));
        Assert.Equal(ProtoStreamMarshaller.Instance.MarshalValue(1.0d), Context.Marshal(1.0d));
    }

    [Fact]
    public void Marshalling_null_is_rejected() =>
        Assert.Throws<HotRodException>(() => Context.Marshal<string>(null!));

    [Fact]
    public void Marshalling_an_unregistered_type_is_rejected() =>
        Assert.Throws<HotRodException>(() => Context.Marshal(new object()));
}
