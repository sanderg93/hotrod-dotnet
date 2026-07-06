using HotRod.Client.Protocol;

namespace HotRod.Client.Marshalling;

/// <summary>
/// The registry that turns typed values into the ProtoStream bytes a cache stores, and back.
/// Out of the box it handles the scalar types the Java client wraps directly (int, long, double,
/// float, bool, string, byte[]). Registering an <see cref="IProtoStreamMarshaller{T}"/> for a custom
/// type teaches it to encode that type as a Protobuf <c>WrappedMessage</c>: the type name in field 16
/// (<c>WRAPPED_TYPE_NAME</c>) and the marshalled body in field 17 (<c>WRAPPED_MESSAGE</c>), which is
/// the form the Java <c>ProtoStreamMarshaller</c> produces and reads.
/// </summary>
public sealed class SerializationContext
{
    // WrappedMessage field numbers from protostream's message-wrapping.proto.
    private const int WrappedTypeName = 16;
    private const int WrappedMessage = 17;

    private readonly Dictionary<Type, IWrappedTypeMarshaller> _byType = new();

    /// <summary>A context with only the built-in scalar support; safe to share and never mutated.</summary>
    public static SerializationContext Default { get; } = new();

    /// <summary>
    /// Registers <paramref name="marshaller"/> as the encoder for values of type <typeparamref name="T"/>,
    /// replacing any previous registration for that type. Returns this context so registrations chain.
    /// </summary>
    public SerializationContext Register<T>(IProtoStreamMarshaller<T> marshaller)
    {
        ArgumentNullException.ThrowIfNull(marshaller);
        _byType[typeof(T)] = new WrappedTypeMarshaller<T>(marshaller);
        return this;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a ProtoStream <c>WrappedMessage</c>: a registered custom type
    /// is wrapped as a named nested message; a built-in scalar is wrapped in its matching scalar field.
    /// </summary>
    internal byte[] Marshal<T>(T value)
    {
        if (value is null)
            throw new HotRodException("Cannot marshal a null value; remove the key instead");

        if (_byType.TryGetValue(typeof(T), out IWrappedTypeMarshaller? custom))
        {
            var wrapper = new ProtoStreamWriter();
            wrapper.WriteString(WrappedTypeName, custom.TypeName);

            var body = new ProtoStreamWriter();
            custom.Write(body, value);
            wrapper.WriteBytes(WrappedMessage, body.ToArray());
            return wrapper.ToArray();
        }

        return ProtoStreamMarshaller.Instance.MarshalValue(value);
    }

    /// <summary>Decodes the bytes produced by <see cref="Marshal{T}"/> (or the Java client) back to a value.</summary>
    internal T Unmarshal<T>(byte[] bytes)
    {
        if (_byType.TryGetValue(typeof(T), out IWrappedTypeMarshaller? custom))
        {
            byte[]? body = ExtractWrappedMessage(bytes);
            if (body is null)
                throw new HotRodException($"ProtoStream value did not carry a wrapped message for {custom.TypeName}");
            return (T)custom.Read(new ProtoStreamReader(body));
        }

        return (T)ProtoStreamMarshaller.Instance.UnmarshalValue(bytes);
    }

    /// <summary>Pulls the field-17 nested message bytes out of a <c>WrappedMessage</c>, skipping the type name and anything else.</summary>
    private static byte[]? ExtractWrappedMessage(byte[] bytes)
    {
        var reader = new ProtoStreamReader(bytes);
        byte[]? body = null;
        while (reader.ReadTag(out int field))
        {
            if (field == WrappedMessage)
                body = reader.ReadBytes();
            else
                reader.SkipField();
        }
        return body;
    }

    /// <summary>Type-erased view of an <see cref="IProtoStreamMarshaller{T}"/> so the context can hold them uniformly.</summary>
    private interface IWrappedTypeMarshaller
    {
        string TypeName { get; }
        void Write(ProtoStreamWriter writer, object value);
        object Read(ProtoStreamReader reader);
    }

    private sealed class WrappedTypeMarshaller<T>(IProtoStreamMarshaller<T> inner) : IWrappedTypeMarshaller
    {
        public string TypeName => inner.TypeName;
        public void Write(ProtoStreamWriter writer, object value) => inner.WriteTo(writer, (T)value);
        public object Read(ProtoStreamReader reader) => inner.ReadFrom(reader)!;
    }
}
