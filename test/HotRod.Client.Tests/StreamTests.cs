using System.Buffers;
using System.IO.Pipelines;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Covers the GetStreamStart/Next/End and PutStreamStart/Next/End wire format (opcodes 0xE4-0xEF,
/// response = request - 1 — unusual for this protocol, where every other operation uses response =
/// request + 1) that replaced GetStream/PutStream (0x37/0x39) as of Hot Rod protocol 4.1. An encode/
/// decode layout suite that runs without a server, plus a <c>Live_</c> round-trip test that writes a
/// value larger than one chunk via <see cref="RemoteCache.PutStreamAsync(byte[], Stream, long, Expiration, int, CancellationToken)"/>
/// and reads it back via <see cref="RemoteCache.GetStreamAsync(byte[], int, CancellationToken)"/>,
/// verified against this project's own Testcontainers image (<c>quay.io/infinispan/server:16.0</c>).
/// See <see cref="StreamCodec"/> for the exact field layout this is checked against, and
/// <c>Constants.cs</c>'s streaming opcode comment for how the earlier (0x37/0x39) attempt was found to
/// be dead against a current server.
/// </summary>
public class StreamTests
{
    // -- GetStreamStart request/response layout ------------------------------

    [Fact]
    public void GetStreamStart_request_writes_the_key_then_the_batch_size()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] key = "the-key"u8.ToArray();

        StreamCodec.WriteGetStreamStart(writer, key, batchSize: 8192);

        var expected = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(expected, key);
        HotRodCodec.WriteVInt(expected, 8192);
        Assert.Equal(expected.WrittenSpan.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task GetStreamStart_response_is_null_when_the_key_is_absent()
    {
        GetStreamStart? start = await StreamCodec.ReadGetStreamStartAsync(
            Constants.StatusKeyDoesNotExist, TestPipe.Reader(), CancellationToken.None);

        Assert.Null(start);
    }

    [Fact]
    public async Task GetStreamStart_response_reads_id_complete_metadata_and_the_first_chunk()
    {
        var body = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteInt(body, 42);            // stream id
        HotRodCodec.WriteByte(body, 1);             // complete = true
        HotRodCodec.WriteByte(body, 0x03);          // metadata flags: both infinite
        HotRodCodec.WriteLong(body, 99);            // entry version
        HotRodCodec.WriteArray(body, "hello"u8.ToArray()); // first chunk
        HotRodCodec.WriteArray(body, "TAIL"u8.ToArray());  // simulates a following field

        PipeReader reader = TestPipe.Reader(body);
        GetStreamStart? start = await StreamCodec.ReadGetStreamStartAsync(Constants.StatusSuccess, reader, CancellationToken.None);

        Assert.NotNull(start);
        Assert.Equal(42, start.Value.Id);
        Assert.True(start.Value.Complete);
        Assert.Equal(99, start.Value.Version);
        Assert.Equal("hello"u8.ToArray(), start.Value.Chunk);

        // Nothing beyond the documented fields was consumed.
        byte[] next = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        Assert.Equal("TAIL"u8.ToArray(), next);
    }

    [Fact]
    public async Task GetStreamStart_response_with_finite_expiration_reads_the_extra_metadata_fields()
    {
        var body = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteInt(body, 1);
        HotRodCodec.WriteByte(body, 0); // complete = false
        HotRodCodec.WriteByte(body, 0x00); // neither dimension infinite
        HotRodCodec.WriteLong(body, 111); // created
        HotRodCodec.WriteVInt(body, 60);  // lifespan
        HotRodCodec.WriteLong(body, 222); // lastUsed
        HotRodCodec.WriteVInt(body, 30);  // maxIdle
        HotRodCodec.WriteLong(body, 7);   // version
        HotRodCodec.WriteArray(body, "x"u8.ToArray());

        GetStreamStart? start = await StreamCodec.ReadGetStreamStartAsync(
            Constants.StatusSuccess, TestPipe.Reader(body), CancellationToken.None);

        Assert.False(start!.Value.Complete);
        Assert.Equal(7, start.Value.Version);
        Assert.Equal("x"u8.ToArray(), start.Value.Chunk);
    }

    // -- GetStreamNext / GetStreamEnd / PutStreamEnd request layout ("just an id") --------------------

    [Fact]
    public void Stream_id_request_writes_a_4_byte_big_endian_int()
    {
        var writer = new ArrayBufferWriter<byte>();
        StreamCodec.WriteStreamId(writer, 0x0102_0304);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task GetStreamNext_response_reads_complete_flag_and_chunk_after_the_echoed_id()
    {
        var body = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteInt(body, 42);   // echoed stream id
        HotRodCodec.WriteByte(body, 0);   // complete = false
        HotRodCodec.WriteArray(body, "chunk-2"u8.ToArray());

        GetStreamChunk chunk = await StreamCodec.ReadGetStreamNextAsync(Constants.StatusSuccess, TestPipe.Reader(body), CancellationToken.None);

        Assert.False(chunk.Complete);
        Assert.Equal("chunk-2"u8.ToArray(), chunk.Chunk);
    }

    [Fact]
    public async Task GetStreamNext_throws_when_the_cursor_no_longer_exists()
    {
        await Assert.ThrowsAsync<HotRodException>(async () =>
            await StreamCodec.ReadGetStreamNextAsync(Constants.StatusKeyDoesNotExist, TestPipe.Reader(), CancellationToken.None));
    }

    // -- PutStreamStart request/response layout ------------------------------

    [Fact]
    public void PutStreamStart_request_writes_key_then_expiration_then_the_8_byte_version()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] key = "k"u8.ToArray();
        var expiration = new Expiration(TimeSpan.FromSeconds(60));

        StreamCodec.WritePutStreamStart(writer, key, expiration, version: 99);

        var expected = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(expected, key);
        expiration.WriteTo(expected);
        HotRodCodec.WriteLong(expected, 99);
        Assert.Equal(expected.WrittenSpan.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void PutStreamStart_expiration_is_byte_identical_to_a_normal_PutRequest_expiration_field()
    {
        // Confirms this reuses Expiration.WriteTo verbatim (as the Java source's writeExpirationParams
        // does for a normal Put), rather than inventing a separate encoding for the stream variant.
        var writer = new ArrayBufferWriter<byte>();
        var expiration = new Expiration(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        StreamCodec.WritePutStreamStart(writer, "k"u8.ToArray(), expiration, version: 0);

        var putRequestStyle = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(putRequestStyle, "k"u8.ToArray());
        expiration.WriteTo(putRequestStyle);

        Assert.Equal(putRequestStyle.WrittenSpan.ToArray(), writer.WrittenSpan.ToArray()[..putRequestStyle.WrittenCount]);
    }

    [Theory]
    [InlineData(StreamCodec.UnconditionalPut)]
    [InlineData(StreamCodec.PutIfAbsent)]
    public void PutStream_version_sentinels_match_the_documented_values(long version)
    {
        Assert.True(version is 0 or -1);
    }

    [Fact]
    public async Task PutStreamStart_response_is_just_the_stream_id()
    {
        var body = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteInt(body, 7);

        int id = await StreamCodec.ReadPutStreamStartAsync(TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal(7, id);
    }

    // -- PutStreamNext request layout -----------------------------------------

    [Fact]
    public void PutStream_chunk_writes_id_complete_flag_then_length_prefixed_bytes()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] chunk = [1, 2, 3, 4];

        StreamCodec.WritePutStreamChunk(writer, id: 5, complete: true, chunk);

        var expected = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteInt(expected, 5);
        HotRodCodec.WriteByte(expected, 1);
        HotRodCodec.WriteVInt(expected, 4);
        expected.Write(chunk);
        Assert.Equal(expected.WrittenSpan.ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void PutStream_final_chunk_may_be_empty_but_still_carries_complete_true()
    {
        var writer = new ArrayBufferWriter<byte>();
        StreamCodec.WritePutStreamChunk(writer, id: 5, complete: true, ReadOnlySpan<byte>.Empty);

        var expected = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteInt(expected, 5);
        HotRodCodec.WriteByte(expected, 1);
        HotRodCodec.WriteVInt(expected, 0);
        Assert.Equal(expected.WrittenSpan.ToArray(), writer.WrittenSpan.ToArray());
    }

    // -- Live end-to-end (opt-in) --------------------------------------------

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("HOTROD_LIVE") == "1";

    private static async Task<HotRodClient> ConnectLiveAsync()
    {
        string host = Environment.GetEnvironmentVariable("HOTROD_HOST") ?? "127.0.0.1";
        int port = int.TryParse(Environment.GetEnvironmentVariable("HOTROD_PORT"), out int p) ? p : 11222;
        return await HotRodClient.ConnectAsync(host, port);
    }

    [Fact]
    public async Task Live_PutStream_then_GetStream_round_trips_a_value_spanning_several_chunks()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        RemoteCache cache = client.GetCache();
        byte[] key = System.Text.Encoding.UTF8.GetBytes("stream-roundtrip-" + Guid.NewGuid().ToString("N"));

        // Several times the chunk size, so both directions genuinely span multiple Next exchanges.
        const int chunkSize = 1024;
        byte[] value = new byte[chunkSize * 5 + 123];
        new Random(42).NextBytes(value);

        using (var source = new MemoryStream(value))
        {
            bool stored = await cache.PutStreamAsync(key, source, chunkSize: chunkSize);
            Assert.True(stored);
        }

        await using HotRodValueStream? stream = await cache.GetStreamAsync(key, batchSize: chunkSize);
        Assert.NotNull(stream);

        using var readBack = new MemoryStream();
        await stream!.CopyToAsync(readBack);

        Assert.Equal(value, readBack.ToArray());
    }

    [Fact]
    public async Task Live_GetStream_returns_null_for_an_absent_key()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        RemoteCache cache = client.GetCache();

        HotRodValueStream? stream = await cache.GetStreamAsync("definitely-absent-" + Guid.NewGuid());
        Assert.Null(stream);
    }
}
