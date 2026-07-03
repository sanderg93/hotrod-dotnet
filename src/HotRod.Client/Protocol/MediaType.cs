using System.Buffers;

namespace HotRod.Client.Protocol;

/// <summary>
/// A storage MediaType as declared in the HotRod request header. The client states the format of
/// the bytes it sends so the server can transcode to the cache's storage encoding. Encoding
/// (matching Infinispan's <c>Codec.writeMediaType</c>): a marker byte — 0 = none, 1 = predefined id,
/// 2 = custom name — followed by the id/name and then a parameter count (0 here, no params).
/// </summary>
internal sealed class MediaType
{
    private readonly string? _name;

    private MediaType(string? name) => _name = name;

    /// <summary>"No media type set": the server assumes the cache's own storage encoding.</summary>
    public static readonly MediaType None = new(null);

    public static MediaType Custom(string name) => new(name);

    public static readonly MediaType ProtoStream = Custom("application/x-protostream");
    public static readonly MediaType OctetStream = Custom("application/octet-stream");

    public void WriteTo(IBufferWriter<byte> writer)
    {
        if (_name is null)
        {
            HotRodCodec.WriteByte(writer, 0);
            return;
        }

        HotRodCodec.WriteByte(writer, 2); // custom name (we don't rely on the predefined-id registry)
        HotRodCodec.WriteArray(writer, System.Text.Encoding.UTF8.GetBytes(_name));
        HotRodCodec.WriteVInt(writer, 0); // parameter count
    }
}

/// <summary>The key and value media types to declare for one request.</summary>
internal readonly struct DataFormat(MediaType key, MediaType value)
{
    public MediaType Key { get; } = key;
    public MediaType Value { get; } = value;

    /// <summary>Declares nothing — used for auth and for raw caches.</summary>
    public static readonly DataFormat None = new(MediaType.None, MediaType.None);
}
