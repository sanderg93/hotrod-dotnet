using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace HotRod.Client.Protocol;

/// <summary>
/// Low-level read/write primitives for the HotRod wire format: variable-length integers,
/// length-prefixed byte arrays and strings. Writes target an <see cref="IBufferWriter{T}"/>
/// (the request is built in memory and flushed once); reads pull asynchronously from a
/// <see cref="PipeReader"/>, buffering until each field is complete.
/// </summary>
internal static class HotRodCodec
{
    // -- Write side (synchronous, into the pipe's buffer) -------------------

    public static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    /// <summary>Writes an unsigned 32-bit value as a variable-length int (7 bits per byte, MSB = continuation).</summary>
    public static void WriteVInt(IBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(5);
        uint v = (uint)value;
        int i = 0;
        while ((v & ~0x7Fu) != 0)
        {
            span[i++] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        span[i++] = (byte)v;
        writer.Advance(i);
    }

    /// <summary>Writes an unsigned 64-bit value as a variable-length long.</summary>
    public static void WriteVLong(IBufferWriter<byte> writer, long value)
    {
        Span<byte> span = writer.GetSpan(10);
        ulong v = (ulong)value;
        int i = 0;
        while ((v & ~0x7Ful) != 0)
        {
            span[i++] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        span[i++] = (byte)v;
        writer.Advance(i);
    }

    /// <summary>
    /// Writes a signed int as a zigzag-encoded vInt (Infinispan's <c>SignedNumeric</c>): small
    /// magnitudes stay small regardless of sign, so -1 ("none") is a single byte rather than five.
    /// </summary>
    public static void WriteSignedVInt(IBufferWriter<byte> writer, int value) =>
        WriteVInt(writer, (value << 1) ^ (value >> 31));

    /// <summary>Writes a byte array prefixed with its length as a vInt.</summary>
    public static void WriteArray(IBufferWriter<byte> writer, byte[] data)
    {
        WriteVInt(writer, data.Length);
        writer.Write(data);
    }

    /// <summary>Writes a signed 64-bit value as 8 big-endian bytes (used for entry versions).</summary>
    public static void WriteLong(IBufferWriter<byte> writer, long value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteInt64BigEndian(span, value);
        writer.Advance(8);
    }

    /// <summary>Writes a signed 32-bit value as 4 big-endian bytes (used for GetStream/PutStream stream ids).</summary>
    public static void WriteInt(IBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        writer.Advance(4);
    }

    // -- Read side (asynchronous, from the pipe) ---------------------------

    public static async ValueTask<byte> ReadByteAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= 1)
            {
                byte value = buffer.FirstSpan[0];
                reader.AdvanceTo(buffer.GetPosition(1));
                return value;
            }
            EnsureNotCompleted(result, "byte");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public static async ValueTask<int> ReadVIntAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (TryReadVarint(buffer, maxBytes: 5, out long value, out SequencePosition consumed))
            {
                reader.AdvanceTo(consumed);
                return (int)value;
            }
            EnsureNotCompleted(result, "vInt");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public static async ValueTask<long> ReadVLongAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (TryReadVarint(buffer, maxBytes: 10, out long value, out SequencePosition consumed))
            {
                reader.AdvanceTo(consumed);
                return value;
            }
            EnsureNotCompleted(result, "vLong");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads a zigzag-encoded signed vInt (the inverse of <see cref="WriteSignedVInt"/>).</summary>
    public static async ValueTask<int> ReadSignedVIntAsync(PipeReader reader, CancellationToken ct)
    {
        int v = await ReadVIntAsync(reader, ct);
        return (int)((uint)v >> 1) ^ -(v & 1);
    }

    /// <summary>Reads a vInt length prefix followed by that many bytes.</summary>
    public static async ValueTask<byte[]> ReadArrayAsync(PipeReader reader, CancellationToken ct)
    {
        int length = await ReadVIntAsync(reader, ct);
        if (length == 0)
            return [];

        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= length)
            {
                ReadOnlySequence<byte> slice = buffer.Slice(0, length);
                byte[] bytes = slice.ToArray();
                reader.AdvanceTo(slice.End);
                return bytes;
            }
            EnsureNotCompleted(result, "array");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads a length-prefixed UTF-8 string.</summary>
    public static async ValueTask<string> ReadStringAsync(PipeReader reader, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await ReadArrayAsync(reader, ct));

    /// <summary>
    /// Reads the entry metadata that precedes a value in a metadata response: a flags byte, then the
    /// created+lifespan and lastUsed+maxIdle pairs only for the dimensions that are not infinite, then
    /// the 8-byte version. Absent dimensions come back as -1.
    /// </summary>
    public static async ValueTask<EntryMetadata> ReadMetadataAsync(PipeReader reader, CancellationToken ct)
    {
        byte flags = await ReadByteAsync(reader, ct);
        long created = -1, lastUsed = -1;
        int lifespan = -1, maxIdle = -1;

        if ((flags & Constants.InfiniteLifespan) == 0)
        {
            created = await ReadLongAsync(reader, ct);
            lifespan = await ReadVIntAsync(reader, ct);
        }
        if ((flags & Constants.InfiniteMaxIdle) == 0)
        {
            lastUsed = await ReadLongAsync(reader, ct);
            maxIdle = await ReadVIntAsync(reader, ct);
        }

        long version = await ReadLongAsync(reader, ct);
        return new EntryMetadata(created, lifespan, lastUsed, maxIdle, version);
    }

    /// <summary>
    /// Reads the prior value a FORCE_RETURN_VALUE write left in the response. Only the "with previous"
    /// statuses carry a body — entry metadata followed by the value — and a zero-length value there
    /// means there was no previous entry (returns null). Every other status carries no body.
    /// </summary>
    public static async ValueTask<byte[]?> ReadPreviousValueAsync(byte status, PipeReader reader, CancellationToken ct)
    {
        if (!ResponseStatus.HasPrevious(status))
            return null;

        await ReadMetadataAsync(reader, ct);
        byte[] value = await ReadArrayAsync(reader, ct);
        return value.Length == 0 ? null : value;
    }

    /// <summary>Reads a string→string map: a vInt count followed by that many name/value string pairs.</summary>
    public static async ValueTask<IReadOnlyDictionary<string, string>> ReadStringMapAsync(PipeReader reader, CancellationToken ct)
    {
        int count = await ReadVIntAsync(reader, ct);
        var map = new Dictionary<string, string>(count);
        for (int i = 0; i < count; i++)
        {
            string name = await ReadStringAsync(reader, ct);
            map[name] = await ReadStringAsync(reader, ct);
        }
        return map;
    }

    /// <summary>
    /// Reads a bulkGetKeys response: a run of [1-byte more-flag, key array] pairs terminated by a zero
    /// more-flag. Keys come back in the cache's storage format and in no defined order.
    /// </summary>
    public static async ValueTask<IReadOnlyList<byte[]>> ReadBulkKeysAsync(PipeReader reader, CancellationToken ct)
    {
        var keys = new List<byte[]>();
        while (await ReadByteAsync(reader, ct) != 0)
            keys.Add(await ReadArrayAsync(reader, ct));
        return keys;
    }

    /// <summary>
    /// Reads one iterationNext batch, matching the Java client's decode order: the finished-segments
    /// bitset, the entry count, then — only when the count is non-zero — the projection size and the
    /// entries. Each entry always begins with a metadata-present byte (and, if set, the standard entry
    /// metadata), then the key, then one value (or <c>projectionSize</c> values for a converter; the
    /// first is kept).
    /// </summary>
    public static async ValueTask<IterationBatch> ReadIterationBatchAsync(PipeReader reader, CancellationToken ct)
    {
        byte[] finishedSegments = await ReadArrayAsync(reader, ct);
        int count = await ReadVIntAsync(reader, ct);
        if (count == 0)
            return new IterationBatch(finishedSegments, []);

        int projectionSize = await ReadVIntAsync(reader, ct);
        var entries = new List<KeyValuePair<byte[], byte[]>>(count);
        for (int i = 0; i < count; i++)
        {
            if (await ReadByteAsync(reader, ct) == 1)
                await ReadMetadataAsync(reader, ct);

            byte[] key = await ReadArrayAsync(reader, ct);
            byte[] value = await ReadArrayAsync(reader, ct);
            for (int p = 1; p < projectionSize; p++)
                await ReadArrayAsync(reader, ct); // extra converter projections; the first value is kept
            entries.Add(new KeyValuePair<byte[], byte[]>(key, value));
        }
        return new IterationBatch(finishedSegments, entries);
    }

    /// <summary>
    /// Reads one cache event body, given the event opcode already taken from the header. Matching the
    /// Java client's decode order: a status byte and a topology byte (both ignored — events never carry
    /// a topology update), the listener id, an <c>isCustom</c> marker, the command-retried flag, then
    /// the key. Created and modified events additionally carry the entry's new 8-byte version; removed
    /// and expired events do not. Only non-custom events are produced (this client registers no
    /// filter/converter factories), so a non-zero <c>isCustom</c> marker is rejected rather than misread.
    /// </summary>
    public static async ValueTask<RawClientEvent> ReadClientEventAsync(PipeReader reader, byte opcode, CancellationToken ct)
    {
        await ReadByteAsync(reader, ct); // status: always success on an event
        await ReadByteAsync(reader, ct); // topology marker: events carry no topology update

        byte[] listenerId = await ReadArrayAsync(reader, ct);
        byte isCustom = await ReadByteAsync(reader, ct);
        if (isCustom != 0)
            throw new HotRodException($"Custom cache events are not supported (isCustom=0x{isCustom:X2}).");

        bool retried = await ReadByteAsync(reader, ct) == 1;
        byte[] key = await ReadArrayAsync(reader, ct);

        bool hasVersion = opcode is Constants.CacheEntryCreatedEvent or Constants.CacheEntryModifiedEvent;
        long version = hasVersion ? await ReadLongAsync(reader, ct) : 0;
        return new RawClientEvent(opcode, listenerId, retried, key, version, hasVersion);
    }

    /// <summary>Reads a signed 32-bit value from 4 big-endian bytes (used for GetStream/PutStream stream ids).</summary>
    public static async ValueTask<int> ReadIntAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            var seq = new SequenceReader<byte>(buffer);
            if (seq.TryReadBigEndian(out int value))
            {
                reader.AdvanceTo(seq.Position);
                return value;
            }
            EnsureNotCompleted(result, "int");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads a signed 64-bit value from 8 big-endian bytes (used for entry versions).</summary>
    public static async ValueTask<long> ReadLongAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            var seq = new SequenceReader<byte>(buffer);
            if (seq.TryReadBigEndian(out long value))
            {
                reader.AdvanceTo(seq.Position);
                return value;
            }
            EnsureNotCompleted(result, "long");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads a 16-bit big-endian unsigned integer (used for ports in topology updates).</summary>
    public static async ValueTask<int> ReadUShortAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= 2)
            {
                var seq = new SequenceReader<byte>(buffer);
                seq.TryRead(out byte high);
                seq.TryRead(out byte low);
                reader.AdvanceTo(seq.Position);
                return (high << 8) | low;
            }
            EnsureNotCompleted(result, "ushort");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Tries to decode a varint from the buffer, reporting how many bytes it consumed.</summary>
    private static bool TryReadVarint(ReadOnlySequence<byte> buffer, int maxBytes, out long value, out SequencePosition consumed)
    {
        var reader = new SequenceReader<byte>(buffer);
        value = 0;
        int shift = 0;
        while (reader.TryRead(out byte b))
        {
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                consumed = reader.Position;
                return true;
            }
            shift += 7;
            if (shift >= maxBytes * 7)
                throw new HotRodException("Varint is too long");
        }
        consumed = default;
        return false;
    }

    private static void EnsureNotCompleted(ReadResult result, string field)
    {
        if (result.IsCompleted)
            throw new HotRodException($"Unexpected end of stream while reading a {field} from the server");
    }
}
