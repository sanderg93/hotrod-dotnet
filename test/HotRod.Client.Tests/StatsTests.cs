using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// The STATS response body is a string→string map: a vInt count followed by that many name/value
/// string pairs. <see cref="HotRodCodec.ReadStringMapAsync"/> reads it back in order.
/// </summary>
public class StatsTests
{
    [Fact]
    public async Task Reads_a_string_map_of_name_value_pairs()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(w, 2);
        HotRodCodec.WriteArray(w, "currentNumberOfEntries"u8.ToArray());
        HotRodCodec.WriteArray(w, "7"u8.ToArray());
        HotRodCodec.WriteArray(w, "timeSinceStart"u8.ToArray());
        HotRodCodec.WriteArray(w, "-1"u8.ToArray());

        IReadOnlyDictionary<string, string> stats =
            await HotRodCodec.ReadStringMapAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(2, stats.Count);
        Assert.Equal("7", stats["currentNumberOfEntries"]);
        Assert.Equal("-1", stats["timeSinceStart"]);
    }

    [Fact]
    public async Task An_empty_map_reads_as_an_empty_dictionary()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVInt(w, 0);

        IReadOnlyDictionary<string, string> stats =
            await HotRodCodec.ReadStringMapAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Empty(stats);
    }
}
