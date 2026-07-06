using System.Buffers;
using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// Encodes an Ickle query into the protobuf-encoded <c>QueryRequest</c> that forms a QUERY_REQUEST body,
/// and decodes the protobuf-encoded <c>QueryResponse</c> from a QUERY_RESPONSE body into a
/// <see cref="QueryResult"/>. Both messages are plain protobuf (not a HotRod frame), so this reads and
/// writes protobuf tags, varints, and length-delimited fields directly.
/// <para>
/// Request fields (from Infinispan's <c>QueryRequest</c>): 1 = query string, 3 = start offset (written
/// only when positive), 4 = max results (written only when non-negative), 5 = repeated named parameter
/// (each a nested message: 1 = name, 2 = value as a ProtoStream <c>WrappedMessage</c>), 7 = hit-count
/// accuracy (written only when non-negative). Response fields (from <c>QueryResponse</c>): 1 = result
/// count, 2 = projection size, 3 = repeated result (each a <c>WrappedMessage</c>), 4 = hit count,
/// 5 = hit-count-exact flag.
/// </para>
/// <para>
/// Each result <c>WrappedMessage</c> is decoded through <see cref="ProtoStreamMarshaller"/>: a wrapped
/// scalar (number, bool, string, bytes) comes back as its CLR value; an entity or nested message that
/// carries no scalar wrapper is surfaced as the raw marshalled message bytes for a richer decoder to
/// interpret. Projection results are grouped into rows of <c>projectionSize</c> columns; a
/// non-projection (entity) query has a projection size of zero and yields one column per row.
/// </para>
/// </summary>
internal static class QueryCodec
{
    // Request protobuf tags: (fieldNumber << 3) | wireType. Wire types: 0 varint, 2 length-delimited.
    private const byte TagQueryString = (1 << 3) | 2;      // 0x0A
    private const byte TagStartOffset = (3 << 3) | 0;      // 0x18
    private const byte TagMaxResults = (4 << 3) | 0;       // 0x20
    private const byte TagNamedParameter = (5 << 3) | 2;   // 0x2A
    private const byte TagHitCountAccuracy = (7 << 3) | 0; // 0x38
    private const byte TagParameterName = (1 << 3) | 2;    // 0x0A within a NamedParameter
    private const byte TagParameterValue = (2 << 3) | 2;   // 0x12 within a NamedParameter

    // WrappedMessage field carrying a nested message's marshalled bytes (protostream message-wrapping.proto).
    private const int WrappedMessageField = 17;

    /// <summary>
    /// Serializes a QueryRequest body. <paramref name="startOffset"/> is written only when positive and
    /// <paramref name="maxResults"/>/<paramref name="hitCountAccuracy"/> only when non-negative (a
    /// negative value means "unset", matching the Java client, so the server applies its default).
    /// </summary>
    public static byte[] EncodeRequest(
        string queryString,
        long startOffset,
        int maxResults,
        IReadOnlyDictionary<string, object>? namedParameters,
        int hitCountAccuracy)
    {
        var buffer = new ArrayBufferWriter<byte>(queryString.Length + 16);

        HotRodCodec.WriteByte(buffer, TagQueryString);
        HotRodCodec.WriteArray(buffer, Encoding.UTF8.GetBytes(queryString));

        if (startOffset > 0)
        {
            HotRodCodec.WriteByte(buffer, TagStartOffset);
            HotRodCodec.WriteVLong(buffer, startOffset);
        }
        if (maxResults >= 0)
        {
            HotRodCodec.WriteByte(buffer, TagMaxResults);
            HotRodCodec.WriteVInt(buffer, maxResults);
        }
        if (namedParameters is { Count: > 0 })
        {
            foreach (KeyValuePair<string, object> parameter in namedParameters)
            {
                HotRodCodec.WriteByte(buffer, TagNamedParameter);
                HotRodCodec.WriteArray(buffer, EncodeNamedParameter(parameter.Key, parameter.Value));
            }
        }
        if (hitCountAccuracy >= 0)
        {
            HotRodCodec.WriteByte(buffer, TagHitCountAccuracy);
            HotRodCodec.WriteVInt(buffer, hitCountAccuracy);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Serializes one named parameter: its name and its value as a <c>WrappedMessage</c>.</summary>
    private static byte[] EncodeNamedParameter(string name, object value)
    {
        // A boolean parameter is passed as its string form, matching the Java client's ProtoStream path.
        object marshalled = value is bool b ? (b ? "true" : "false") : value;

        var buffer = new ArrayBufferWriter<byte>(name.Length + 16);
        HotRodCodec.WriteByte(buffer, TagParameterName);
        HotRodCodec.WriteArray(buffer, Encoding.UTF8.GetBytes(name));
        HotRodCodec.WriteByte(buffer, TagParameterValue);
        HotRodCodec.WriteArray(buffer, ProtoStreamMarshaller.Instance.MarshalValue(marshalled));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes a QueryResponse body into hit-count metadata and decoded result rows.</summary>
    public static QueryResult DecodeResponse(byte[] bytes)
    {
        int projectionSize = 0, hitCount = 0;
        bool hitCountExact = false;
        var results = new List<byte[]>();

        int pos = 0;
        while (pos < bytes.Length)
        {
            int tag = ReadVarint(bytes, ref pos);
            int field = tag >> 3;
            int wireType = tag & 7;
            switch (field)
            {
                case 1 when wireType == 0:
                    ReadVarintLong(bytes, ref pos); // result count: derived from the results list below
                    break;
                case 2 when wireType == 0:
                    projectionSize = (int)ReadVarintLong(bytes, ref pos);
                    break;
                case 3 when wireType == 2:
                    results.Add(ReadLengthDelimited(bytes, ref pos));
                    break;
                case 4 when wireType == 0:
                    hitCount = (int)ReadVarintLong(bytes, ref pos);
                    break;
                case 5 when wireType == 0:
                    hitCountExact = ReadVarintLong(bytes, ref pos) != 0;
                    break;
                default:
                    Skip(bytes, ref pos, wireType);
                    break;
            }
        }

        return new QueryResult(hitCount, hitCountExact, projectionSize, BuildRows(results, projectionSize));
    }

    /// <summary>Groups the flat result list into rows: <c>projectionSize</c> columns each, or one column per row for an entity query.</summary>
    private static List<QueryResultRow> BuildRows(List<byte[]> results, int projectionSize)
    {
        int columns = projectionSize > 0 ? projectionSize : 1;
        var rows = new List<QueryResultRow>(results.Count / columns);
        for (int i = 0; i + columns <= results.Count; i += columns)
        {
            var values = new object?[columns];
            for (int c = 0; c < columns; c++)
                values[c] = DecodeResultValue(results[i + c]);
            rows.Add(new QueryResultRow(values));
        }
        return rows;
    }

    /// <summary>
    /// Decodes one result <c>WrappedMessage</c>: a wrapped scalar becomes its CLR value; an entity or
    /// nested message becomes its marshalled bytes (the inner message when present, else the whole
    /// wrapper); an empty wrapper becomes null.
    /// </summary>
    private static object? DecodeResultValue(byte[] wrapped)
    {
        if (wrapped.Length == 0)
            return null;
        try
        {
            return ProtoStreamMarshaller.Instance.UnmarshalValue(wrapped);
        }
        catch (HotRodException)
        {
            return ExtractWrappedMessage(wrapped) ?? wrapped;
        }
    }

    /// <summary>Returns the bytes of a <c>WrappedMessage</c>'s nested-message field, or null if it carries none.</summary>
    private static byte[]? ExtractWrappedMessage(byte[] bytes)
    {
        int pos = 0;
        while (pos < bytes.Length)
        {
            int tag = ReadVarint(bytes, ref pos);
            int field = tag >> 3;
            int wireType = tag & 7;
            if (field == WrappedMessageField && wireType == 2)
                return ReadLengthDelimited(bytes, ref pos);
            Skip(bytes, ref pos, wireType);
        }
        return null;
    }

    private static byte[] ReadLengthDelimited(byte[] bytes, ref int pos)
    {
        int length = ReadVarint(bytes, ref pos);
        byte[] slice = bytes[pos..(pos + length)];
        pos += length;
        return slice;
    }

    private static void Skip(byte[] bytes, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarintLong(bytes, ref pos); break;      // varint
            case 1: pos += 8; break;                            // 64-bit
            case 2:                                             // length-delimited
                int length = ReadVarint(bytes, ref pos); // advances pos past the length prefix first
                pos += length;
                break;
            case 5: pos += 4; break;                            // 32-bit
            default: throw new HotRodException($"Unsupported protobuf wire type {wireType} in a query response");
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
