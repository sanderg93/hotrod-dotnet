using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using HotRod.Client;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the transaction wire format against Infinispan's encoding: the XID (a signed-vInt format id
/// followed by the global id and branch qualifier as length-prefixed arrays), the PREPARE_TX body
/// (XID, one-phase and recoverable flags, an 8-byte timeout, then the modification count and each
/// modification), and the per-modification control byte that decides whether a read version, an
/// expiration, and a value are present. Confirmed against <c>ByteBufUtil.writeXid</c>,
/// <c>PrepareTransactionOperation</c>, <c>Modification</c>, and <c>ControlByte</c>.
/// </summary>
public class TransactionCodecTests
{
    private static byte[] Bytes(ArrayBufferWriter<byte> w) => w.WrittenMemory.ToArray();

    private static readonly byte[] Key = "k"u8.ToArray();     // 0x6B
    private static readonly byte[] Value = "v"u8.ToArray();   // 0x76
    private const byte ExpirationDefaultByte = 0x77;          // DEFAULT lifespan (high nibble) + DEFAULT maxIdle (low)

    private static byte[] Long(long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        return b;
    }

    // -- XID ----------------------------------------------------------------

    [Fact]
    public async Task Xid_is_a_signed_vint_format_id_then_two_arrays()
    {
        var xid = new TransactionXid(TransactionManager.RemoteFormatId, [1, 2, 3], [9, 8]);
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteXid(w, xid);

        PipeReader reader = TestPipe.Reader(w);
        int formatId = await HotRodCodec.ReadSignedVIntAsync(reader, CancellationToken.None);
        byte[] gid = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        byte[] bqual = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);

        Assert.Equal(TransactionManager.RemoteFormatId, formatId);
        Assert.Equal(new byte[] { 1, 2, 3 }, gid);
        Assert.Equal(new byte[] { 9, 8 }, bqual);
    }

    // -- Modification control-byte cases ------------------------------------

    [Fact]
    public void Blind_put_writes_control_not_read_without_a_version()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = TransactionCodec.ControlNotRead,
            Value = Value,
        });

        Assert.Equal(new byte[] { 0x01, 0x6B, TransactionCodec.ControlNotRead, ExpirationDefaultByte, 0x01, 0x76 }, Bytes(w));
    }

    [Fact]
    public void Put_of_a_read_present_key_writes_control_zero_and_the_read_version()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = 0x00, // read and present
            VersionRead = 0x0102030405060708,
            Value = Value,
        });

        var expected = new List<byte> { 0x01, 0x6B, 0x00 };
        expected.AddRange(Long(0x0102030405060708));
        expected.AddRange(new byte[] { ExpirationDefaultByte, 0x01, 0x76 });
        Assert.Equal(expected.ToArray(), Bytes(w));
    }

    [Fact]
    public void Put_of_a_read_absent_key_writes_control_non_existing_without_a_version()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = TransactionCodec.ControlNonExisting,
            Value = Value,
        });

        Assert.Equal(new byte[] { 0x01, 0x6B, TransactionCodec.ControlNonExisting, ExpirationDefaultByte, 0x01, 0x76 }, Bytes(w));
    }

    [Fact]
    public void Blind_remove_writes_no_version_no_value_and_no_expiration()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = (byte)(TransactionCodec.ControlNotRead | TransactionCodec.ControlRemoveOp),
            Value = null,
        });

        Assert.Equal(new byte[] { 0x01, 0x6B, 0x05 }, Bytes(w));
    }

    [Fact]
    public void Remove_of_a_read_present_key_writes_the_version_then_stops()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = TransactionCodec.ControlRemoveOp, // read and present, removed
            VersionRead = 0x1122334455667788,
            Value = null,
        });

        var expected = new List<byte> { 0x01, 0x6B, TransactionCodec.ControlRemoveOp };
        expected.AddRange(Long(0x1122334455667788));
        Assert.Equal(expected.ToArray(), Bytes(w));
    }

    [Fact]
    public void Remove_of_a_read_absent_key_writes_no_version_and_no_value()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = (byte)(TransactionCodec.ControlNonExisting | TransactionCodec.ControlRemoveOp),
            Value = null,
        });

        Assert.Equal(new byte[] { 0x01, 0x6B, 0x06 }, Bytes(w));
    }

    [Fact]
    public void Put_writes_the_expiration_between_control_and_value()
    {
        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WriteModification(w, new TransactionModification
        {
            Key = Key,
            Control = TransactionCodec.ControlNotRead,
            Value = Value,
            Expiration = new Expiration(lifespan: TimeSpan.FromSeconds(1)),
        });

        // Lifespan set as milliseconds (high nibble 0x01), maxIdle default (low nibble 0x07): 0x17, then vLong 1000.
        var expected = new List<byte> { 0x01, 0x6B, TransactionCodec.ControlNotRead, 0x17 };
        var vlong = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteVLong(vlong, 1000);
        expected.AddRange(vlong.WrittenMemory.ToArray());
        expected.AddRange(new byte[] { 0x01, 0x76 });
        Assert.Equal(expected.ToArray(), Bytes(w));
    }

    // -- PREPARE_TX body framing --------------------------------------------

    [Fact]
    public async Task Prepare_body_frames_xid_flags_timeout_and_modification_list()
    {
        var xid = new TransactionXid(TransactionManager.RemoteFormatId, [7], [8]);
        var modifications = new List<TransactionModification>
        {
            new() { Key = Key, Control = TransactionCodec.ControlNotRead, Value = Value },
        };

        var w = new ArrayBufferWriter<byte>();
        TransactionCodec.WritePrepareBody(w, xid, onePhaseCommit: false, recoverable: true, timeoutMs: 60000, modifications);

        PipeReader reader = TestPipe.Reader(w);
        int formatId = await HotRodCodec.ReadSignedVIntAsync(reader, CancellationToken.None);
        byte[] gid = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        byte[] bqual = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        byte onePhase = await HotRodCodec.ReadByteAsync(reader, CancellationToken.None);
        byte recoverable = await HotRodCodec.ReadByteAsync(reader, CancellationToken.None);
        long timeout = await HotRodCodec.ReadLongAsync(reader, CancellationToken.None);
        int count = await HotRodCodec.ReadVIntAsync(reader, CancellationToken.None);
        // The single modification: key, control, expiration, value.
        byte[] key = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        byte control = await HotRodCodec.ReadByteAsync(reader, CancellationToken.None);
        byte expiration = await HotRodCodec.ReadByteAsync(reader, CancellationToken.None);
        byte[] value = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);

        Assert.Equal(TransactionManager.RemoteFormatId, formatId);
        Assert.Equal(new byte[] { 7 }, gid);
        Assert.Equal(new byte[] { 8 }, bqual);
        Assert.Equal(0, onePhase);
        Assert.Equal(1, recoverable);
        Assert.Equal(60000, timeout);
        Assert.Equal(1, count);
        Assert.Equal(Key, key);
        Assert.Equal(TransactionCodec.ControlNotRead, control);
        Assert.Equal(ExpirationDefaultByte, expiration);
        Assert.Equal(Value, value);
    }

    // -- XA return code -----------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(3)]        // XA_RDONLY
    [InlineData(-3)]       // XAER_RMERR
    [InlineData(int.MaxValue)]
    public async Task Xa_return_code_reads_four_big_endian_bytes(int code)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, code);

        int read = await TransactionCodec.ReadXaReturnCodeAsync(TestPipe.Reader(b), CancellationToken.None);

        Assert.Equal(code, read);
    }
}
