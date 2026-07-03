namespace HotRod.Client.Protocol;

/// <summary>
/// One iterationNext response: the bitset of segments the server has finished iterating (so the
/// caller knows when the iteration is exhausted) and the entries in this batch.
/// </summary>
internal sealed record IterationBatch(byte[] FinishedSegments, IReadOnlyList<KeyValuePair<byte[], byte[]>> Entries);
