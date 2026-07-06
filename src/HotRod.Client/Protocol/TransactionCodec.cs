using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace HotRod.Client.Protocol;

/// <summary>
/// Encodes and decodes the transaction-specific parts of the HotRod wire format: the XID that
/// identifies a transaction, the modification set streamed in a PREPARE_TX body, and the 4-byte XA
/// return code the server sends back for prepare/commit/rollback. Built on <see cref="HotRodCodec"/>
/// for the primitive fields. This matches Infinispan's <c>ByteBufUtil.writeXid</c>,
/// <c>PrepareTransactionOperation</c>, <c>Modification</c>, and <c>ControlByte</c>.
/// </summary>
internal static class TransactionCodec
{
    // Per-modification control byte (Infinispan's ControlByte). It records how the key was seen during
    // the transaction so the server can validate the write set at prepare time: NotRead = the key was
    // written blind (never read in the transaction), NonExisting = the key was read and found absent,
    // and neither set = the key was read and present (its read version follows). RemoveOp additionally
    // marks the modification as a removal, in which case no value (and no expiration) is written.
    public const byte ControlNotRead = 0x01;
    public const byte ControlNonExisting = 0x02;
    public const byte ControlRemoveOp = 0x04;

    /// <summary>
    /// Writes an XID: a signed vInt format id, then the global transaction id and the branch qualifier
    /// each as a vInt-length-prefixed byte array (Infinispan's <c>writeXid</c>).
    /// </summary>
    public static void WriteXid(IBufferWriter<byte> writer, TransactionXid xid)
    {
        HotRodCodec.WriteSignedVInt(writer, xid.FormatId);
        HotRodCodec.WriteArray(writer, xid.GlobalId);
        HotRodCodec.WriteArray(writer, xid.BranchQualifier);
    }

    /// <summary>
    /// Writes a PREPARE_TX body: the XID, the one-phase-commit and recoverable flags, the transaction
    /// timeout in milliseconds (8 bytes), then the modification count and each modification.
    /// </summary>
    public static void WritePrepareBody(
        IBufferWriter<byte> writer,
        TransactionXid xid,
        bool onePhaseCommit,
        bool recoverable,
        long timeoutMs,
        IReadOnlyList<TransactionModification> modifications)
    {
        WriteXid(writer, xid);
        HotRodCodec.WriteByte(writer, (byte)(onePhaseCommit ? 1 : 0));
        HotRodCodec.WriteByte(writer, (byte)(recoverable ? 1 : 0));
        HotRodCodec.WriteLong(writer, timeoutMs);
        HotRodCodec.WriteVInt(writer, modifications.Count);
        foreach (TransactionModification modification in modifications)
            WriteModification(writer, modification);
    }

    /// <summary>
    /// Writes one modification: the key, the control byte, the read version (only when the key was read
    /// and present), and — for a write rather than a remove — the expiration parameters and the value.
    /// </summary>
    public static void WriteModification(IBufferWriter<byte> writer, TransactionModification modification)
    {
        HotRodCodec.WriteArray(writer, modification.Key);
        HotRodCodec.WriteByte(writer, modification.Control);

        if ((modification.Control & (ControlNonExisting | ControlNotRead)) == 0)
            HotRodCodec.WriteLong(writer, modification.VersionRead);

        if ((modification.Control & ControlRemoveOp) != 0)
            return;

        modification.Expiration.WriteTo(writer);
        HotRodCodec.WriteArray(writer, modification.Value!);
    }

    /// <summary>
    /// Reads the 4-byte big-endian XA return code that the server appends to a successful prepare,
    /// commit, or rollback response.
    /// </summary>
    public static async ValueTask<int> ReadXaReturnCodeAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= 4)
            {
                Span<byte> span = stackalloc byte[4];
                buffer.Slice(0, 4).CopyTo(span);
                reader.AdvanceTo(buffer.GetPosition(4));
                return BinaryPrimitives.ReadInt32BigEndian(span);
            }
            if (result.IsCompleted)
                throw new HotRodException("Unexpected end of stream while reading a transaction return code from the server");
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}

/// <summary>
/// A single key's final state in a transaction's write set, ready to be serialized into a PREPARE_TX
/// body. A remove carries no value; a write carries its value and expiration. The control byte and the
/// read version record how the key was observed during the transaction so the server can validate it.
/// </summary>
internal readonly struct TransactionModification
{
    public required byte[] Key { get; init; }
    public required byte Control { get; init; }
    public long VersionRead { get; init; }
    public byte[]? Value { get; init; }
    public Expiration Expiration { get; init; }
}
