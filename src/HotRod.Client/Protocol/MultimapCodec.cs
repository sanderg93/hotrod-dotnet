using System.Collections.Generic;
using System.IO.Pipelines;

namespace HotRod.Client.Protocol;

/// <summary>
/// Encodes and decodes the multimap-specific parts of the HotRod wire format, built on
/// <see cref="HotRodCodec"/> for the primitive fields. Modelled on the Java client's
/// <c>AbstractMultimapKeyOperation</c>/<c>AbstractMultimapKeyValueOperation</c> and their concrete
/// operations (<c>client-hotrod</c> module, <c>impl.multimap.operations</c> package):
/// <list type="bullet">
/// <item>Every multimap request ends with a one-byte "supports duplicates" flag (0/1) — set once per
/// cache handle to match how the multimap was configured server-side (list semantics with duplicates
/// allowed, or set semantics that collapse them) and sent with every operation.</item>
/// <item>Operations that carry a value but conceptually have no expiration of their own
/// (<c>removeEntry</c>, <c>containsEntry</c>, <c>containsValue</c>) still write an expiration block —
/// the Java client hardcodes it to "infinite" (lifespan and max-idle both encode as
/// <see cref="Constants.TimeUnitInfinite"/>, with no following vLongs) rather than expose it, since the
/// server does not use it for a lookup/removal-by-value.</item>
/// <item>A value collection on the wire is a vInt count followed by that many length-prefixed byte
/// arrays — the same shape <c>GetAllRequest</c> uses for a key/value list, just for values alone.</item>
/// <item>A boolean response (removeKey, removeEntry, containsKey, containsEntry, containsValue) is a
/// single byte (1/0) — except when the status is key-does-not-exist, where the body is empty and the
/// answer is always false.</item>
/// </list>
/// </summary>
internal static class MultimapCodec
{
    /// <summary>0x88: both lifespan and max-idle encoded as <see cref="Constants.TimeUnitInfinite"/>, no vLongs follow.</summary>
    private const byte InfiniteExpirationByte = (Constants.TimeUnitInfinite << 4) | Constants.TimeUnitInfinite;

    /// <summary>Writes the one-byte "supports duplicates" flag that ends every multimap request.</summary>
    public static void WriteSupportsDuplicates(System.Buffers.IBufferWriter<byte> writer, bool supportsDuplicates) =>
        HotRodCodec.WriteByte(writer, (byte)(supportsDuplicates ? 1 : 0));

    /// <summary>
    /// Writes a hardcoded "infinite" expiration block for the multimap operations that carry a value but
    /// have no expiration of their own (removeEntry, containsEntry, containsValue) — see the Java
    /// client's <c>RemoveEntryMultimapOperation</c>/<c>ContainsEntryMultimapOperation</c>/
    /// <c>ContainsValueMultimapOperation</c>, which all pass lifespan = maxIdle = -1.
    /// </summary>
    public static void WriteInfiniteExpiration(System.Buffers.IBufferWriter<byte> writer) =>
        HotRodCodec.WriteByte(writer, InfiniteExpirationByte);

    /// <summary>Reads a vInt count followed by that many length-prefixed value arrays.</summary>
    public static async ValueTask<IReadOnlyList<byte[]>> ReadValueCollectionAsync(PipeReader reader, CancellationToken ct)
    {
        int count = await HotRodCodec.ReadVIntAsync(reader, ct);
        var values = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
            values.Add(await HotRodCodec.ReadArrayAsync(reader, ct));
        return values;
    }

    /// <summary>
    /// Reads a boolean response: key-does-not-exist means the key (or, for containsValue, any key) never
    /// held the value, so the answer is false without a body; otherwise a single byte carries it.
    /// </summary>
    public static async ValueTask<bool> ReadBoolResponseAsync(byte status, PipeReader reader, CancellationToken ct) =>
        !ResponseStatus.KeyDoesNotExist(status) && await HotRodCodec.ReadByteAsync(reader, ct) == 1;
}
