using System.Buffers.Binary;
using System.Text;

namespace HotRod.Client.Marshalling;

/// <summary>
/// Reads tagged Protobuf fields from a byte buffer. A custom <see cref="IProtoStreamMarshaller{T}"/>
/// drives one of these in a loop: <see cref="ReadTag"/> advances to the next field and reports its
/// number, then the matching typed reader consumes the value (or <see cref="SkipField"/> discards an
/// unrecognized one). Fields absent from the buffer are simply never visited, so a reader tolerates
/// forward- and backward-compatible schema changes.
/// </summary>
public sealed class ProtoStreamReader
{
    private readonly byte[] _bytes;
    private int _pos;
    private int _wireType;

    /// <summary>Reads over <paramref name="bytes"/>, a single serialized message with no surrounding framing.</summary>
    public ProtoStreamReader(byte[] bytes) => _bytes = bytes;

    /// <summary>
    /// Advances to the next field and reports its number, returning false at the end of the buffer.
    /// The field's wire type is retained so the following typed read (or <see cref="SkipField"/>) knows
    /// how much to consume.
    /// </summary>
    public bool ReadTag(out int fieldNumber)
    {
        if (_pos >= _bytes.Length)
        {
            fieldNumber = 0;
            return false;
        }

        int tag = (int)ReadVarint();
        fieldNumber = tag >> 3;
        _wireType = tag & 7;
        return true;
    }

    /// <summary>Reads the current field as a signed 32-bit varint.</summary>
    public int ReadInt32() => (int)ReadVarint();

    /// <summary>Reads the current field as a signed 64-bit varint.</summary>
    public long ReadInt64() => ReadVarint();

    /// <summary>Reads the current field as an unsigned 32-bit varint.</summary>
    public uint ReadUInt32() => (uint)ReadVarint();

    /// <summary>Reads the current field as a boolean varint.</summary>
    public bool ReadBool() => ReadVarint() != 0;

    /// <summary>Reads the current field as a fixed64 double.</summary>
    public double ReadDouble()
    {
        double value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan(_pos, 8)));
        _pos += 8;
        return value;
    }

    /// <summary>Reads the current field as a fixed32 float.</summary>
    public float ReadFloat()
    {
        float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(_pos, 4)));
        _pos += 4;
        return value;
    }

    /// <summary>Reads the current field as a length-prefixed UTF-8 string.</summary>
    public string ReadString()
    {
        int length = (int)ReadVarint();
        string value = Encoding.UTF8.GetString(_bytes, _pos, length);
        _pos += length;
        return value;
    }

    /// <summary>Reads the current field as a length-prefixed byte array (also a nested message's bytes).</summary>
    public byte[] ReadBytes()
    {
        int length = (int)ReadVarint();
        byte[] value = _bytes[_pos..(_pos + length)];
        _pos += length;
        return value;
    }

    /// <summary>Discards the current field's value, using its wire type to know how much to skip.</summary>
    public void SkipField()
    {
        switch (_wireType)
        {
            case 0: ReadVarint(); break;                          // varint
            case 1: _pos += 8; break;                             // fixed64
            case 2: int length = (int)ReadVarint(); _pos += length; break; // length-delimited
            case 5: _pos += 4; break;                             // fixed32
            default: throw new HotRodException($"Unsupported protobuf wire type {_wireType}");
        }
    }

    private long ReadVarint()
    {
        long result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _bytes[_pos++];
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
    }
}
