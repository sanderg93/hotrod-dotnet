using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// Maps a key to the cluster node that primarily owns it, the way Infinispan's
/// <c>SegmentConsistentHash</c> does: hash the key with <see cref="MurmurHash3"/>, fold it into a
/// segment number, and look up that segment's primary owner. Built from the segment→owner table the
/// server sends in a hash-aware topology update.
/// </summary>
internal sealed class ConsistentHash
{
    private readonly ServerAddress?[] _primaryBySegment;
    private readonly int _segmentSize;

    public ConsistentHash(ServerAddress?[] primaryBySegment)
    {
        _primaryBySegment = primaryBySegment;
        // Segments evenly divide the positive hash space [0, 2^31); segmentSize is rounded up so
        // the largest normalized hash still lands in the last segment.
        _segmentSize = (int)Math.Ceiling((1L << 31) / (double)primaryBySegment.Length);
    }

    public int SegmentCount => _primaryBySegment.Length;

    /// <summary>The node that primarily owns <paramref name="key"/>, or null if the segment has no owner yet.</summary>
    public ServerAddress? PrimaryOwner(byte[] key)
    {
        int normalized = MurmurHash3.Hash(key) & int.MaxValue;
        int segment = normalized / _segmentSize;
        return _primaryBySegment[segment];
    }
}
