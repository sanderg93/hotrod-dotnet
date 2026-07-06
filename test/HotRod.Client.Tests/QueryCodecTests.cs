using System.Buffers;
using HotRod.Client;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// The query codec serializes an Ickle QueryRequest and deserializes the QueryResponse. These tests pin
/// the request's protobuf field layout (query string, paging, named parameter) and round-trip a response
/// built with the same ProtoStream wrapping the server uses, covering scalar projections, entity rows
/// surfaced as bytes, null columns, and the hit-count fields.
/// </summary>
public class QueryCodecTests
{
    private static readonly ProtoStreamMarshaller Wrap = ProtoStreamMarshaller.Instance;

    [Fact]
    public void Encodes_a_bare_query_as_field_one_only()
    {
        byte[] request = QueryCodec.EncodeRequest("FROM x", startOffset: 0, maxResults: -1, namedParameters: null, hitCountAccuracy: -1);

        // Field 1 (query string), wire type 2 => tag 0x0A, then the length-prefixed UTF-8 string.
        Assert.Equal(new byte[] { 0x0A, 0x06, (byte)'F', (byte)'R', (byte)'O', (byte)'M', (byte)' ', (byte)'x' }, request);
    }

    [Fact]
    public void Encodes_paging_only_when_set()
    {
        byte[] request = QueryCodec.EncodeRequest("FROM x", startOffset: 5, maxResults: 10, namedParameters: null, hitCountAccuracy: -1);

        Assert.Equal(new byte[]
        {
            0x0A, 0x06, (byte)'F', (byte)'R', (byte)'O', (byte)'M', (byte)' ', (byte)'x',
            0x18, 0x05, // field 3 startOffset = 5
            0x20, 0x0A, // field 4 maxResults = 10
        }, request);
    }

    [Fact]
    public void Omits_a_zero_start_offset_and_negative_max_results()
    {
        byte[] request = QueryCodec.EncodeRequest("q", startOffset: 0, maxResults: -1, namedParameters: null, hitCountAccuracy: -1);
        Assert.Equal(new byte[] { 0x0A, 0x01, (byte)'q' }, request);
    }

    [Fact]
    public void Encodes_a_named_parameter_as_a_nested_message()
    {
        var parameters = new Dictionary<string, object> { ["age"] = 30 };
        byte[] request = QueryCodec.EncodeRequest("q", startOffset: 0, maxResults: -1, namedParameters: parameters, hitCountAccuracy: -1);

        // A NamedParameter is: field 1 name ("age"), field 2 value as a WrappedMessage (wrappedInt32 30).
        byte[] expected =
        {
            0x0A, 0x01, (byte)'q',
            0x2A, 0x09,                          // field 5 named parameter, length 9
            0x0A, 0x03, (byte)'a', (byte)'g', (byte)'e', // param field 1: name
            0x12, 0x02, 0x28, 0x1E,             // param field 2: WrappedMessage int32(30) = [0x28,0x1E]
        };
        Assert.Equal(expected, request);
    }

    [Fact]
    public void Encodes_a_boolean_parameter_as_its_string_form()
    {
        var parameters = new Dictionary<string, object> { ["active"] = true };
        byte[] request = QueryCodec.EncodeRequest("q", startOffset: 0, maxResults: -1, namedParameters: parameters, hitCountAccuracy: -1);

        // The value is wrapped as the string "true" (wrappedString tag 0x4A), matching the Java client.
        byte[] wrappedTrue = Wrap.MarshalValue("true");
        int idx = IndexOf(request, wrappedTrue);
        Assert.True(idx >= 0, "expected the boolean parameter to be wrapped as the string \"true\"");
    }

    [Fact]
    public void Encodes_hit_count_accuracy_when_set()
    {
        byte[] request = QueryCodec.EncodeRequest("q", startOffset: 0, maxResults: -1, namedParameters: null, hitCountAccuracy: 100);
        // field 7 (hitCountAccuracy), varint => tag 0x38, then 100 = 0x64.
        Assert.Equal(new byte[] { 0x0A, 0x01, (byte)'q', 0x38, 0x64 }, request);
    }

    [Fact]
    public void Decodes_a_scalar_projection_into_rows()
    {
        byte[] response = new ResponseBuilder()
            .NumResults(2)
            .ProjectionSize(2)
            .Result(Wrap.MarshalValue("Alice")).Result(Wrap.MarshalValue(30))
            .Result(Wrap.MarshalValue("Bob")).Result(Wrap.MarshalValue(25))
            .HitCount(2).HitCountExact(true)
            .Build();

        QueryResult result = QueryCodec.DecodeResponse(response);

        Assert.Equal(2, result.HitCount);
        Assert.True(result.HitCountExact);
        Assert.Equal(2, result.ProjectionSize);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { "Alice", 30 }, result.Rows[0].Columns);
        Assert.Equal(new object?[] { "Bob", 25 }, result.Rows[1].Columns);
    }

    [Fact]
    public void Decodes_an_entity_query_as_a_single_bytes_column()
    {
        byte[] entityMessage = { 0x08, 0x2A, 0x12, 0x03, (byte)'a', (byte)'b', (byte)'c' };
        byte[] wrappedEntity = WrapEntity("tutorial.Person", entityMessage);

        byte[] response = new ResponseBuilder()
            .NumResults(1)
            .ProjectionSize(0)
            .Result(wrappedEntity)
            .HitCount(1).HitCountExact(true)
            .Build();

        QueryResult result = QueryCodec.DecodeResponse(response);

        Assert.Equal(0, result.ProjectionSize);
        QueryResultRow row = Assert.Single(result.Rows);
        // The row's single value is the entity's marshalled message bytes, unwrapped from the WrappedMessage.
        Assert.Equal(entityMessage, Assert.IsType<byte[]>(row.Value));
    }

    [Fact]
    public void Decodes_an_empty_wrapped_value_as_null()
    {
        byte[] response = new ResponseBuilder()
            .NumResults(1)
            .ProjectionSize(1)
            .Result([]) // an empty WrappedMessage => an absent column
            .HitCount(1)
            .Build();

        QueryResult result = QueryCodec.DecodeResponse(response);
        Assert.Null(Assert.Single(result.Rows).Value);
    }

    [Fact]
    public void Decodes_an_empty_result_set_with_a_hit_count()
    {
        byte[] response = new ResponseBuilder().NumResults(0).ProjectionSize(0).HitCount(0).HitCountExact(true).Build();

        QueryResult result = QueryCodec.DecodeResponse(response);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.HitCount);
        Assert.True(result.HitCountExact);
    }

    [Fact]
    public void An_inexact_hit_count_is_reported_as_a_lower_bound()
    {
        byte[] response = new ResponseBuilder().NumResults(0).ProjectionSize(0).HitCount(1000).HitCountExact(false).Build();

        QueryResult result = QueryCodec.DecodeResponse(response);
        Assert.Equal(1000, result.HitCount);
        Assert.False(result.HitCountExact);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match)
                return i;
        }
        return -1;
    }

    /// <summary>Builds a WrappedMessage carrying a type name (field 16) and a nested message (field 17), as an entity result.</summary>
    private static byte[] WrapEntity(string typeName, byte[] message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(buffer, (16 << 3) | 2); // wrappedTypeName tag
        HotRodCodec.WriteArray(buffer, System.Text.Encoding.UTF8.GetBytes(typeName));
        HotRodCodec.WriteVInt(buffer, (17 << 3) | 2); // wrappedMessage tag
        HotRodCodec.WriteArray(buffer, message);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Assembles a protobuf QueryResponse the way the server would, for the decode tests.</summary>
    private sealed class ResponseBuilder
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public ResponseBuilder NumResults(int value) => Varint(1, value);
        public ResponseBuilder ProjectionSize(int value) => Varint(2, value);
        public ResponseBuilder HitCount(int value) => Varint(4, value);
        public ResponseBuilder HitCountExact(bool value) => Varint(5, value ? 1 : 0);

        public ResponseBuilder Result(byte[] wrapped)
        {
            HotRodCodec.WriteVInt(_buffer, (3 << 3) | 2); // field 3, length-delimited
            HotRodCodec.WriteArray(_buffer, wrapped);
            return this;
        }

        private ResponseBuilder Varint(int field, int value)
        {
            HotRodCodec.WriteVInt(_buffer, (field << 3) | 0);
            HotRodCodec.WriteVInt(_buffer, value);
            return this;
        }

        public byte[] Build() => _buffer.WrittenSpan.ToArray();
    }
}
