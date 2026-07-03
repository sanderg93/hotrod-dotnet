using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// A cache event body (matching the Java client's <c>readCacheEvent</c> decode order): a status byte
/// and a topology byte that are both ignored, the listener id, an <c>isCustom</c> marker, a
/// command-retried flag, then the key — and, for created/modified events only, the 8-byte version.
/// <see cref="HotRodCodec.ReadClientEventAsync"/> reads one event given its opcode.
/// </summary>
public class ClientEventTests
{
    [Fact]
    public async Task Reads_a_created_event_with_key_and_version()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 0x00);                  // status (ignored)
        HotRodCodec.WriteByte(w, 0x00);                  // topology byte (ignored)
        HotRodCodec.WriteArray(w, [1, 2, 3]);            // listener id
        HotRodCodec.WriteByte(w, 0x00);                  // isCustom = no
        HotRodCodec.WriteByte(w, 0x00);                  // command retried = no
        HotRodCodec.WriteArray(w, "Stad"u8.ToArray());   // key
        HotRodCodec.WriteLong(w, 42);                    // version

        RawClientEvent ev = await HotRodCodec.ReadClientEventAsync(
            TestPipe.Reader(w), Constants.CacheEntryCreatedEvent, CancellationToken.None);

        Assert.Equal(Constants.CacheEntryCreatedEvent, ev.Opcode);
        Assert.Equal([1, 2, 3], ev.ListenerId);
        Assert.False(ev.CommandRetried);
        Assert.Equal("Stad"u8.ToArray(), ev.Key);
        Assert.True(ev.HasVersion);
        Assert.Equal(42, ev.Version);
    }

    [Fact]
    public async Task Reads_a_modified_event_with_version_and_retried_flag()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteArray(w, [9]);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteByte(w, 0x01);                  // command retried = yes
        HotRodCodec.WriteArray(w, "k"u8.ToArray());
        HotRodCodec.WriteLong(w, 7);

        RawClientEvent ev = await HotRodCodec.ReadClientEventAsync(
            TestPipe.Reader(w), Constants.CacheEntryModifiedEvent, CancellationToken.None);

        Assert.True(ev.CommandRetried);
        Assert.True(ev.HasVersion);
        Assert.Equal(7, ev.Version);
    }

    [Fact]
    public async Task Reads_a_removed_event_without_a_version()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteArray(w, [9]);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteArray(w, "k"u8.ToArray());
        // No version follows for removed events.
        HotRodCodec.WriteByte(w, 0xA1);                  // next message's magic must stay unread

        var reader = TestPipe.Reader(w);
        RawClientEvent ev = await HotRodCodec.ReadClientEventAsync(
            reader, Constants.CacheEntryRemovedEvent, CancellationToken.None);

        Assert.False(ev.HasVersion);
        Assert.Equal("k"u8.ToArray(), ev.Key);
        Assert.Equal(0xA1, await HotRodCodec.ReadByteAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Reads_an_expired_event_without_a_version()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteArray(w, [9]);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteByte(w, 0x00);
        HotRodCodec.WriteArray(w, "k"u8.ToArray());
        HotRodCodec.WriteByte(w, 0xA1);

        var reader = TestPipe.Reader(w);
        RawClientEvent ev = await HotRodCodec.ReadClientEventAsync(
            reader, Constants.CacheEntryExpiredEvent, CancellationToken.None);

        Assert.False(ev.HasVersion);
        Assert.Equal(0xA1, await HotRodCodec.ReadByteAsync(reader, CancellationToken.None));
    }
}
