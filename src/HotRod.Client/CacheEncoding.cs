namespace HotRod.Client;

/// <summary>
/// How a <see cref="RemoteCache"/> turns values into the bytes on the wire, and which storage
/// MediaType it declares to the server.
/// </summary>
public enum CacheEncoding
{
    /// <summary>
    /// Raw bytes: strings go as UTF-8, byte arrays as-is, no MediaType declared. Fast and fully
    /// under your control, but not interoperable with the Java client and shown as raw bytes in the
    /// console. Suited to binary blobs or a private byte store.
    /// </summary>
    Raw,

    /// <summary>
    /// ProtoStream (Infinispan's Protobuf): strings are wrapped so the value is interoperable with
    /// the Java client and rendered readably (as JSON) by the console and REST. The default.
    /// </summary>
    ProtoStream,
}
