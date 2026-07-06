using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using HotRod.Client.Protocol;

namespace HotRod.Client.Marshalling;

/// <summary>
/// Writes tagged Protobuf fields into an in-memory buffer. A custom
/// <see cref="IProtoStreamMarshaller{T}"/> uses one of these to serialize its type's fields by number,
/// matching the field numbers of the corresponding <c>.proto</c> message. Each write emits the field
/// tag (<c>(fieldNumber &lt;&lt; 3) | wireType</c>) followed by the value in that wire type's encoding.
/// </summary>
public sealed class ProtoStreamWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(32);

    // Protobuf wire types: 0 varint, 1 fixed64, 2 length-delimited, 5 fixed32.
    private const int VarintWire = 0;
    private const int Fixed64Wire = 1;
    private const int LengthDelimitedWire = 2;
    private const int Fixed32Wire = 5;

    /// <summary>Writes a signed 32-bit field. Negative values sign-extend to a ten-byte varint, as Protobuf specifies.</summary>
    public void WriteInt32(int fieldNumber, int value)
    {
        WriteTag(fieldNumber, VarintWire);
        HotRodCodec.WriteVLong(_buffer, value); // sign-extend to 64 bits before varint encoding
    }

    /// <summary>Writes a signed 64-bit field as a varint.</summary>
    public void WriteInt64(int fieldNumber, long value)
    {
        WriteTag(fieldNumber, VarintWire);
        HotRodCodec.WriteVLong(_buffer, value);
    }

    /// <summary>Writes an unsigned 32-bit field as a varint.</summary>
    public void WriteUInt32(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, VarintWire);
        HotRodCodec.WriteVLong(_buffer, value);
    }

    /// <summary>Writes a boolean field as a single-byte varint.</summary>
    public void WriteBool(int fieldNumber, bool value)
    {
        WriteTag(fieldNumber, VarintWire);
        HotRodCodec.WriteByte(_buffer, (byte)(value ? 1 : 0));
    }

    /// <summary>Writes a double field as eight little-endian bytes (fixed64).</summary>
    public void WriteDouble(int fieldNumber, double value)
    {
        WriteTag(fieldNumber, Fixed64Wire);
        Span<byte> span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)BitConverter.DoubleToInt64Bits(value));
        _buffer.Advance(8);
    }

    /// <summary>Writes a float field as four little-endian bytes (fixed32).</summary>
    public void WriteFloat(int fieldNumber, float value)
    {
        WriteTag(fieldNumber, Fixed32Wire);
        Span<byte> span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)BitConverter.SingleToInt32Bits(value));
        _buffer.Advance(4);
    }

    /// <summary>Writes a UTF-8 string field prefixed with its byte length.</summary>
    public void WriteString(int fieldNumber, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        WriteTag(fieldNumber, LengthDelimitedWire);
        HotRodCodec.WriteVInt(_buffer, utf8.Length);
        _buffer.Write(utf8);
    }

    /// <summary>Writes a length-prefixed byte field. Also used to carry a nested message's serialized bytes.</summary>
    public void WriteBytes(int fieldNumber, byte[] value)
    {
        WriteTag(fieldNumber, LengthDelimitedWire);
        HotRodCodec.WriteVInt(_buffer, value.Length);
        _buffer.Write(value);
    }

    /// <summary>The bytes written so far.</summary>
    internal byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    private void WriteTag(int fieldNumber, int wireType) =>
        HotRodCodec.WriteVInt(_buffer, (fieldNumber << 3) | wireType);
}
