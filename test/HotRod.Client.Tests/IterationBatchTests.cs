using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// An iterationNext response (matching the Java client's decode order): a finished-segments bitset and
/// an entry count, then — only when the count is non-zero — a projection size and the entries. Each
/// entry begins with a metadata-present byte (and, if set, the standard expiration/version metadata),
/// then its key and value. <see cref="HotRodCodec.ReadIterationBatchAsync"/> reads one batch.
/// </summary>
public class IterationBatchTests
{
    [Fact]
    public async Task Reads_entries_each_prefixed_with_a_no_metadata_byte()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(w, [0xFF]);        // finished segments bitset
        HotRodCodec.WriteVInt(w, 2);              // entry count
        HotRodCodec.WriteVInt(w, 1);              // projection size (one value per entry)
        HotRodCodec.WriteByte(w, 0);              // entry 1: no metadata
        HotRodCodec.WriteArray(w, "k1"u8.ToArray());
        HotRodCodec.WriteArray(w, "v1"u8.ToArray());
        HotRodCodec.WriteByte(w, 0);              // entry 2: no metadata
        HotRodCodec.WriteArray(w, "k2"u8.ToArray());
        HotRodCodec.WriteArray(w, "v2"u8.ToArray());

        IterationBatch batch = await HotRodCodec.ReadIterationBatchAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(2, batch.Entries.Count);
        Assert.Equal("k1"u8.ToArray(), batch.Entries[0].Key);
        Assert.Equal("v1"u8.ToArray(), batch.Entries[0].Value);
        Assert.Equal("v2"u8.ToArray(), batch.Entries[1].Value);
    }

    [Fact]
    public async Task An_empty_batch_reads_no_projection_size()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(w, []);   // no finished segments
        HotRodCodec.WriteVInt(w, 0);     // no entries → nothing else follows
        // A trailing byte that must NOT be consumed (would be the next response's first byte).
        HotRodCodec.WriteByte(w, 0xA1);
        var reader = TestPipe.Reader(w);

        IterationBatch batch = await HotRodCodec.ReadIterationBatchAsync(reader, CancellationToken.None);

        Assert.Empty(batch.Entries);
        Assert.Equal(0xA1, await HotRodCodec.ReadByteAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Reads_an_entry_with_a_metadata_block()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(w, []);
        HotRodCodec.WriteVInt(w, 1);              // one entry
        HotRodCodec.WriteVInt(w, 1);              // projection size
        HotRodCodec.WriteByte(w, 1);              // has metadata
        HotRodCodec.WriteByte(w, 0x03);           // both dimensions infinite
        HotRodCodec.WriteLong(w, 123_456);        // version
        HotRodCodec.WriteArray(w, "k"u8.ToArray());
        HotRodCodec.WriteArray(w, "v"u8.ToArray());

        IterationBatch batch = await HotRodCodec.ReadIterationBatchAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Single(batch.Entries);
        Assert.Equal("k"u8.ToArray(), batch.Entries[0].Key);
        Assert.Equal("v"u8.ToArray(), batch.Entries[0].Value);
    }
}
