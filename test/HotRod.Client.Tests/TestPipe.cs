using System.Buffers;
using System.IO.Pipelines;

namespace HotRod.Client.Tests;

/// <summary>
/// Helpers for driving the codec's read side in tests: turn already-written bytes into a
/// <see cref="PipeReader"/> the async read primitives can pull from.
/// </summary>
internal static class TestPipe
{
    /// <summary>A reader over a fixed byte sequence.</summary>
    public static PipeReader Reader(params byte[] bytes) =>
        PipeReader.Create(new ReadOnlySequence<byte>(bytes));

    /// <summary>A reader over whatever was written into <paramref name="writer"/>.</summary>
    public static PipeReader Reader(ArrayBufferWriter<byte> writer) =>
        PipeReader.Create(new ReadOnlySequence<byte>(writer.WrittenMemory));
}
