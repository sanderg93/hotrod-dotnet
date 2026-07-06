using System.Buffers;
using System.IO.Pipelines;
using HotRod.Client;

namespace HotRod.Client.Protocol;

/// <summary>
/// Wire encode/decode for GetStreamStart/Next/End and PutStreamStart/Next/End — the operations that, as
/// of Hot Rod protocol 4.1 (the version this client negotiates, see <see cref="Constants.Version41"/>),
/// replaced the older single-exchange GetStream/PutStream (0x37/0x39; see the opcode comment in
/// <c>Constants.cs</c>). Reference: the "Hot Rod Protocol 4.1" section of
/// <c>documentation/src/main/asciidoc/topics/hotrod_protocol.adoc</c>, cross-checked against the Java
/// client's operation classes
/// (<c>client/hotrod-client/.../impl/operations/{GetStream,PutStream}{Start,Next,End}Operation.java</c>)
/// and <c>StreamingRemoteCacheImpl.java</c>, since the doc has at least one copy-paste error (see
/// <see cref="Constants.GetStreamEndRequest"/>'s comment) and also duplicates the GetStreamStart
/// response's optional-metadata fields into the PutStreamStart request table where they do not belong —
/// the real PutStreamStart request body was read from
/// <c>PutStreamStartOperation.writeOperationRequest</c>, which calls the very same
/// <c>Codec.writeExpirationParams</c> a normal PutRequest uses (byte-identical to
/// <see cref="Expiration.WriteTo"/> here: a time-units byte then optional vLong lifespan/maxIdle), not a
/// conditionally-present metadata block.
/// <para>
/// A get is a server-side cursor: GetStreamStart(key, batchSize) returns a stream id, whether the first
/// chunk already is the whole value, the entry metadata (version), and the first chunk;
/// GetStreamNext(id) returns further chunks the same way (metadata is sent only once, on Start);
/// GetStreamEnd(id) closes it — the Java client's <c>GetInputStream.close()</c> always sends End, even
/// when the read already completed, so this client's stream does the same.
/// </para>
/// <para>
/// A put is the mirror: PutStreamStart(key, expiration, version) returns a stream id; PutStreamNext(id,
/// complete, chunk) pushes one chunk — the server performs the write once a chunk with complete=true
/// arrives, and that chunk may be empty. PutStreamEnd is <em>not</em> part of the normal, successful
/// flow at all — the Java client's <c>StreamingRemoteCacheImpl</c> never calls it after a completed
/// write; only the final complete=true PutStreamNext finishes the operation — so it is used here only to
/// abandon a write that failed before that final chunk could be sent.
/// </para>
/// </summary>
internal static class StreamCodec
{
    /// <summary>Entry version sentinel for an unconditional PutStream write (the default).</summary>
    public const long UnconditionalPut = 0;

    /// <summary>Entry version sentinel for a PutStream write that only stores if the key is absent.</summary>
    public const long PutIfAbsent = -1;

    // -- GetStreamStart -------------------------------------------------------

    /// <summary>Writes a GetStreamStart request body: the key, then the batch size (bytes per chunk).</summary>
    public static void WriteGetStreamStart(IBufferWriter<byte> writer, byte[] key, int batchSize)
    {
        HotRodCodec.WriteArray(writer, key);
        HotRodCodec.WriteVInt(writer, batchSize);
    }

    /// <summary>
    /// Reads a GetStreamStart response: null if the key is absent (nothing was started server-side),
    /// otherwise the stream id, whether the first chunk already is the whole value, the entry's version,
    /// and the first chunk itself.
    /// </summary>
    public static async ValueTask<GetStreamStart?> ReadGetStreamStartAsync(byte status, PipeReader reader, CancellationToken ct)
    {
        if (ResponseStatus.KeyDoesNotExist(status))
            return null;

        int id = await HotRodCodec.ReadIntAsync(reader, ct);
        bool complete = await HotRodCodec.ReadByteAsync(reader, ct) != 0;
        EntryMetadata metadata = await HotRodCodec.ReadMetadataAsync(reader, ct);
        byte[] chunk = await HotRodCodec.ReadArrayAsync(reader, ct);
        return new GetStreamStart(id, complete, metadata.Version, chunk);
    }

    // -- GetStreamNext / GetStreamEnd, PutStreamEnd (share a "just the id" request) ------------------

    /// <summary>Writes a request body that is just the 4-byte stream id (GetStreamNext/End, PutStreamEnd).</summary>
    public static void WriteStreamId(IBufferWriter<byte> writer, int id) => HotRodCodec.WriteInt(writer, id);

    /// <summary>
    /// Reads a GetStreamNext response: the echoed stream id (discarded), whether this was the last
    /// chunk, and the chunk itself. A status reporting the id does not exist means the cursor is gone
    /// (expired or already closed) — surfaced as an exception since mid-read that is always unexpected.
    /// </summary>
    public static async ValueTask<GetStreamChunk> ReadGetStreamNextAsync(byte status, PipeReader reader, CancellationToken ct)
    {
        if (ResponseStatus.KeyDoesNotExist(status))
            throw new HotRodException("The GetStream cursor no longer exists on the server (it may have expired or been closed already).");

        await HotRodCodec.ReadIntAsync(reader, ct); // echoed stream id
        bool complete = await HotRodCodec.ReadByteAsync(reader, ct) != 0;
        byte[] chunk = await HotRodCodec.ReadArrayAsync(reader, ct);
        return new GetStreamChunk(complete, chunk);
    }

    // -- PutStreamStart -------------------------------------------------------

    /// <summary>
    /// Writes a PutStreamStart request body: the key, the expiration (byte-identical to a normal
    /// PutRequest's — see <see cref="Expiration.WriteTo"/>), then the 8-byte entry version (0 =
    /// unconditional put, -1 = put-if-absent, anything else = a conditional replace).
    /// </summary>
    public static void WritePutStreamStart(IBufferWriter<byte> writer, byte[] key, Expiration expiration, long version)
    {
        HotRodCodec.WriteArray(writer, key);
        expiration.WriteTo(writer);
        HotRodCodec.WriteLong(writer, version);
    }

    /// <summary>Reads a PutStreamStart response: just the stream id.</summary>
    public static ValueTask<int> ReadPutStreamStartAsync(PipeReader reader, CancellationToken ct) =>
        HotRodCodec.ReadIntAsync(reader, ct);

    // -- PutStreamNext --------------------------------------------------------

    /// <summary>
    /// Writes a PutStreamNext request body: the stream id, whether this is the last chunk, then the
    /// chunk's vInt length and bytes (a zero-length final chunk is valid — it still carries
    /// <paramref name="complete"/> = true, which is what actually triggers the write).
    /// </summary>
    public static void WritePutStreamChunk(IBufferWriter<byte> writer, int id, bool complete, ReadOnlySpan<byte> chunk)
    {
        HotRodCodec.WriteInt(writer, id);
        HotRodCodec.WriteByte(writer, (byte)(complete ? 1 : 0));
        HotRodCodec.WriteVInt(writer, chunk.Length);
        if (chunk.Length > 0)
            writer.Write(chunk);
    }
}

/// <summary>A GetStreamStart response once the key is confirmed present.</summary>
internal readonly record struct GetStreamStart(int Id, bool Complete, long Version, byte[] Chunk);

/// <summary>A GetStreamNext response.</summary>
internal readonly record struct GetStreamChunk(bool Complete, byte[] Chunk);
