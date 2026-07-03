using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// Converts values to and from the bytes stored in a cache, and supplies the
/// <see cref="DataFormat"/> (MediaTypes) to declare for them. One marshaller per cache encoding.
/// </summary>
internal abstract class CacheMarshaller
{
    public abstract DataFormat DataFormat { get; }
    public abstract byte[] Marshal(string value);
    public abstract string Unmarshal(byte[] bytes);

    public static CacheMarshaller For(CacheEncoding encoding) => encoding switch
    {
        CacheEncoding.Raw => RawMarshaller.Instance,
        CacheEncoding.ProtoStream => ProtoStreamMarshaller.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown cache encoding"),
    };
}

/// <summary>Strings as UTF-8, byte arrays as-is; no MediaType declared (the server assumes storage).</summary>
internal sealed class RawMarshaller : CacheMarshaller
{
    public static readonly RawMarshaller Instance = new();

    public override DataFormat DataFormat => DataFormat.None;
    public override byte[] Marshal(string value) => Encoding.UTF8.GetBytes(value);
    public override string Unmarshal(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>
/// ProtoStream (Infinispan's Protobuf): a value is encoded as a <c>WrappedMessage</c> whose single
/// scalar field carries the primitive, so the value interoperates with the Java client and the
/// console. The field number selects the CLR type: <c>wrappedDouble</c> (1) → <see cref="double"/>,
/// <c>wrappedFloat</c> (2) → <see cref="float"/>, <c>wrappedInt64</c> (3) → <see cref="long"/>,
/// <c>wrappedInt32</c> (5) → <see cref="int"/>, <c>wrappedBool</c> (8) → <see cref="bool"/>,
/// <c>wrappedString</c> (9) → <see cref="string"/>, <c>wrappedBytes</c> (10) → <see cref="T:byte[]"/>.
/// Registering arbitrary object/POJO schemas is a separate concern and is not handled here.
/// </summary>
internal sealed class ProtoStreamMarshaller : CacheMarshaller
{
    // Protobuf tag = (fieldNumber << 3) | wireType. Field numbers come from protostream's
    // message-wrapping.proto (WrappedMessage). Wire types: 0 varint, 1 fixed64, 2 length-delimited,
    // 5 fixed32.
    private const byte WrappedDoubleTag = (1 << 3) | 1; // 0x09, fixed64
    private const byte WrappedFloatTag = (2 << 3) | 5;  // 0x15, fixed32
    private const byte WrappedInt64Tag = (3 << 3) | 0;  // 0x18, varint
    private const byte WrappedInt32Tag = (5 << 3) | 0;  // 0x28, varint
    private const byte WrappedBoolTag = (8 << 3) | 0;   // 0x40, varint
    private const byte WrappedStringTag = (9 << 3) | 2; // 0x4A, length-delimited
    private const byte WrappedBytesTag = (10 << 3) | 2; // 0x52, length-delimited

    public static readonly ProtoStreamMarshaller Instance = new();

    public override DataFormat DataFormat => new(MediaType.ProtoStream, MediaType.ProtoStream);

    public override byte[] Marshal(string value) => MarshalValue(value);
    public override string Unmarshal(byte[] bytes) => (string)UnmarshalValue(bytes);

    /// <summary>
    /// Wraps a supported primitive (int, long, double, float, bool, string, byte[]) as a
    /// <c>WrappedMessage</c> carrying the matching scalar field.
    /// </summary>
    public byte[] MarshalValue(object value)
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        switch (value)
        {
            case int i:
                HotRodCodec.WriteByte(buffer, WrappedInt32Tag);
                // Protobuf int32 sign-extends to a 64-bit varint, so negatives take ten bytes.
                HotRodCodec.WriteVLong(buffer, i);
                break;
            case long l:
                HotRodCodec.WriteByte(buffer, WrappedInt64Tag);
                HotRodCodec.WriteVLong(buffer, l);
                break;
            case double d:
                HotRodCodec.WriteByte(buffer, WrappedDoubleTag);
                WriteFixed64(buffer, (ulong)BitConverter.DoubleToInt64Bits(d));
                break;
            case float f:
                HotRodCodec.WriteByte(buffer, WrappedFloatTag);
                WriteFixed32(buffer, (uint)BitConverter.SingleToInt32Bits(f));
                break;
            case bool b:
                HotRodCodec.WriteByte(buffer, WrappedBoolTag);
                HotRodCodec.WriteByte(buffer, (byte)(b ? 1 : 0));
                break;
            case string s:
                byte[] utf8 = Encoding.UTF8.GetBytes(s);
                HotRodCodec.WriteByte(buffer, WrappedStringTag);
                HotRodCodec.WriteVInt(buffer, utf8.Length); // protobuf varint == HotRod vInt (LEB128)
                buffer.Write(utf8);
                break;
            case byte[] bytes:
                HotRodCodec.WriteByte(buffer, WrappedBytesTag);
                HotRodCodec.WriteVInt(buffer, bytes.Length);
                buffer.Write(bytes);
                break;
            default:
                throw new HotRodException(
                    $"ProtoStream marshalling does not support values of type {value?.GetType().Name ?? "null"}");
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads a <c>WrappedMessage</c> and returns the CLR value for whichever scalar field it carries,
    /// skipping any other fields (such as a type name) a writer may have added.
    /// </summary>
    public object UnmarshalValue(byte[] bytes)
    {
        int pos = 0;
        while (pos < bytes.Length)
        {
            int tag = ReadVarint(bytes, ref pos);
            int field = tag >> 3;
            int wireType = tag & 7;

            switch (field)
            {
                case 1 when wireType == 1:
                    return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(ReadFixed(bytes, ref pos, 8)));
                case 2 when wireType == 5:
                    return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(ReadFixed(bytes, ref pos, 4)));
                case 3 when wireType == 0:
                    return ReadVarintLong(bytes, ref pos);
                case 5 when wireType == 0:
                    return (int)ReadVarintLong(bytes, ref pos);
                case 8 when wireType == 0:
                    return ReadVarintLong(bytes, ref pos) != 0;
                case 9 when wireType == 2:
                {
                    int length = ReadVarint(bytes, ref pos);
                    string s = Encoding.UTF8.GetString(bytes, pos, length);
                    pos += length;
                    return s;
                }
                case 10 when wireType == 2:
                {
                    int length = ReadVarint(bytes, ref pos);
                    byte[] value = bytes[pos..(pos + length)];
                    pos += length;
                    return value;
                }
                default:
                    Skip(bytes, ref pos, wireType);
                    break;
            }
        }

        throw new HotRodException("ProtoStream value did not contain a supported wrapped primitive");
    }

    private static void WriteFixed64(IBufferWriter<byte> writer, ulong value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        writer.Advance(8);
    }

    private static void WriteFixed32(IBufferWriter<byte> writer, uint value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        writer.Advance(4);
    }

    private static ReadOnlySpan<byte> ReadFixed(byte[] bytes, ref int pos, int count)
    {
        ReadOnlySpan<byte> slice = bytes.AsSpan(pos, count);
        pos += count;
        return slice;
    }

    private static void Skip(byte[] bytes, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarintLong(bytes, ref pos); break;        // varint
            case 1: pos += 8; break;                              // 64-bit
            case 2: pos += ReadVarint(bytes, ref pos); break;     // length-delimited
            case 5: pos += 4; break;                              // 32-bit
            default: throw new HotRodException($"Unsupported protobuf wire type {wireType}");
        }
    }

    private static int ReadVarint(byte[] bytes, ref int pos) => (int)ReadVarintLong(bytes, ref pos);

    private static long ReadVarintLong(byte[] bytes, ref int pos)
    {
        long result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
    }
}
