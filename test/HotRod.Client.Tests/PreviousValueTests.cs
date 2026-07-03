using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// A FORCE_RETURN_VALUE write only carries a body for the "with previous" statuses, and that body is
/// a metadata block (flags + optional lifespan/maxIdle + version) followed by the value — a
/// zero-length value meaning there was no previous entry. Verified against a live server, where
/// REPLACE-absent / REMOVE-absent write no body at all. Other statuses must consume nothing.
/// </summary>
public class PreviousValueTests
{
    /// <summary>Builds a metadata-then-value body the way the server frames it.</summary>
    private static ArrayBufferWriter<byte> Body(byte[] value, byte flags = 0x03 /* both infinite */,
        long version = 7, long created = 0, int lifespan = 0, long lastUsed = 0, int maxIdle = 0)
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, flags);
        if ((flags & Constants.InfiniteLifespan) == 0)
        {
            HotRodCodec.WriteLong(w, created);
            HotRodCodec.WriteVInt(w, lifespan);
        }
        if ((flags & Constants.InfiniteMaxIdle) == 0)
        {
            HotRodCodec.WriteLong(w, lastUsed);
            HotRodCodec.WriteVInt(w, maxIdle);
        }
        HotRodCodec.WriteLong(w, version);
        HotRodCodec.WriteArray(w, value);
        return w;
    }

    [Theory]
    [InlineData(Constants.StatusSuccessWithPrevious)]
    [InlineData(Constants.StatusNotExecutedWithPrevious)]
    public async Task Reads_the_value_from_the_metadata_body(byte status)
    {
        ArrayBufferWriter<byte> body = Body("old"u8.ToArray());

        byte[]? previous = await HotRodCodec.ReadPreviousValueAsync(
            status, TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal("old"u8.ToArray(), previous);
    }

    [Fact]
    public async Task A_zero_length_value_means_there_was_no_previous_entry()
    {
        // PUT on an absent key: status is "with previous" but the value field is empty.
        ArrayBufferWriter<byte> body = Body([], version: -1);

        byte[]? previous = await HotRodCodec.ReadPreviousValueAsync(
            Constants.StatusSuccessWithPrevious, TestPipe.Reader(body), CancellationToken.None);

        Assert.Null(previous);
    }

    [Fact]
    public async Task Skips_lifespan_and_maxIdle_when_present_before_the_value()
    {
        // flags 0x00: neither dimension infinite, so created+lifespan and lastUsed+maxIdle precede the version.
        ArrayBufferWriter<byte> body = Body("kept"u8.ToArray(), flags: 0x00,
            created: 111, lifespan: 60, lastUsed: 222, maxIdle: 30, version: 9);

        byte[]? previous = await HotRodCodec.ReadPreviousValueAsync(
            Constants.StatusSuccessWithPrevious, TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal("kept"u8.ToArray(), previous);
    }

    [Theory]
    [InlineData(Constants.StatusSuccess)]
    [InlineData(Constants.StatusNotExecuted)]
    [InlineData(Constants.StatusKeyDoesNotExist)]
    public async Task Returns_null_and_consumes_nothing_without_a_with_previous_status(byte status)
    {
        // These statuses carry no body; a following field must stay readable.
        var writer = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(writer, "next-field"u8.ToArray());
        var reader = TestPipe.Reader(writer);

        byte[]? previous = await HotRodCodec.ReadPreviousValueAsync(status, reader, CancellationToken.None);

        Assert.Null(previous);
        byte[] following = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        Assert.Equal("next-field"u8.ToArray(), following);
    }

    [Fact]
    public async Task Reads_metadata_fields_back()
    {
        ArrayBufferWriter<byte> body = Body("v"u8.ToArray(), flags: 0x00,
            created: 111, lifespan: 60, lastUsed: 222, maxIdle: 30, version: 99);

        EntryMetadata meta = await HotRodCodec.ReadMetadataAsync(TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal(111, meta.Created);
        Assert.Equal(60, meta.Lifespan);
        Assert.Equal(222, meta.LastUsed);
        Assert.Equal(30, meta.MaxIdle);
        Assert.Equal(99, meta.Version);
    }
}
