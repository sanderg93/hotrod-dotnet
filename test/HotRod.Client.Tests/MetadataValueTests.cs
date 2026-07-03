using HotRod.Client;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Maps the raw <see cref="EntryMetadata"/> read off the wire into the public
/// <see cref="MetadataValue{T}"/>: created/lastUsed are epoch milliseconds, lifespan/maxIdle are
/// seconds, and the -1 sentinel (infinite) becomes null.
/// </summary>
public class MetadataValueTests
{
    [Fact]
    public void Finite_metadata_maps_timestamps_and_durations()
    {
        var meta = new EntryMetadata(Created: 1_000, Lifespan: 60, LastUsed: 2_000, MaxIdle: 30, Version: 99);

        MetadataValue<string> mv = MetadataValues.From(meta, "v");

        Assert.Equal("v", mv.Value);
        Assert.Equal(99, mv.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_000), mv.Created);
        Assert.Equal(TimeSpan.FromSeconds(60), mv.Lifespan);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2_000), mv.LastUsed);
        Assert.Equal(TimeSpan.FromSeconds(30), mv.MaxIdle);
    }

    [Fact]
    public void Infinite_dimensions_map_to_null()
    {
        var meta = new EntryMetadata(Created: -1, Lifespan: -1, LastUsed: -1, MaxIdle: -1, Version: 5);

        MetadataValue<string> mv = MetadataValues.From(meta, "v");

        Assert.Null(mv.Created);
        Assert.Null(mv.Lifespan);
        Assert.Null(mv.LastUsed);
        Assert.Null(mv.MaxIdle);
        Assert.Equal(5, mv.Version);
    }
}
