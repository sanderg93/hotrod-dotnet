namespace HotRod.Client.Marshalling;

/// <summary>
/// Serializes a custom type to and from ProtoStream. A consumer implements one per type and registers
/// it on a <see cref="SerializationContext"/>; the context then wraps the type as a Protobuf
/// <c>WrappedMessage</c> (the type name in field 16, the marshalled body in field 17) so values
/// interoperate with the Java client and the Infinispan console. Implementations write and read the
/// message's own fields by number through <see cref="ProtoStreamWriter"/>/<see cref="ProtoStreamReader"/>.
/// </summary>
public interface IProtoStreamMarshaller<T>
{
    /// <summary>
    /// The fully qualified Protobuf message name (for example <c>tutorial.Person</c>) that identifies
    /// this type on the wire. Must match the name of a message the server's schema knows for the value
    /// to be queryable and readable by other clients.
    /// </summary>
    string TypeName { get; }

    /// <summary>Writes <paramref name="value"/>'s fields into <paramref name="writer"/>.</summary>
    void WriteTo(ProtoStreamWriter writer, T value);

    /// <summary>Reconstructs a value by reading fields from <paramref name="reader"/>.</summary>
    T ReadFrom(ProtoStreamReader reader);
}
