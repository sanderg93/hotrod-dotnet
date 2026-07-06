using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// Encodes and decodes the counter-specific parts of the HotRod wire format: the counter name that
/// prefixes every counter request body, and the counter configuration exchanged by the create and
/// get-configuration operations. Built on <see cref="HotRodCodec"/> for the primitive fields.
/// <para>
/// The configuration is a single flags byte followed by the type-specific fields and the initial
/// value. The flags byte packs the type in its low two bits (0 = unbounded strong, 1 = weak,
/// 2 = bounded strong) and the storage mode in bit 2 (set = persistent). A weak counter then carries
/// its concurrency level as a vInt; a bounded strong counter carries its lower and upper bounds as
/// 8-byte values; an unbounded strong counter carries nothing extra. The initial value (8 bytes)
/// always comes last. This matches Infinispan's <c>CounterEncodeUtil</c>.
/// </para>
/// </summary>
internal static class CounterCodec
{
    private const byte WeakType = 0x01;      // low two bits: 1 = weak
    private const byte BoundedType = 0x02;   // low two bits: 2 = bounded strong (0 = unbounded strong)
    private const byte TypeMask = 0x03;
    private const byte PersistentFlag = 0x04; // bit 2: storage mode

    /// <summary>Writes a counter name as a vInt-length-prefixed UTF-8 string.</summary>
    public static void WriteCounterName(IBufferWriter<byte> writer, string name) =>
        HotRodCodec.WriteArray(writer, Encoding.UTF8.GetBytes(name));

    /// <summary>Writes a counter configuration: the flags byte, the type-specific fields, then the initial value.</summary>
    public static void WriteConfiguration(IBufferWriter<byte> writer, CounterConfiguration configuration)
    {
        HotRodCodec.WriteByte(writer, EncodeFlags(configuration));
        switch (configuration.Type)
        {
            case CounterType.Weak:
                HotRodCodec.WriteVInt(writer, configuration.ConcurrencyLevel);
                break;
            case CounterType.BoundedStrong:
                HotRodCodec.WriteLong(writer, configuration.LowerBound);
                HotRodCodec.WriteLong(writer, configuration.UpperBound);
                break;
        }
        HotRodCodec.WriteLong(writer, configuration.InitialValue);
    }

    /// <summary>Reads a counter configuration written by <see cref="WriteConfiguration"/>.</summary>
    public static async ValueTask<CounterConfiguration> ReadConfigurationAsync(PipeReader reader, CancellationToken ct)
    {
        byte flags = await HotRodCodec.ReadByteAsync(reader, ct);
        CounterType type = DecodeType(flags);
        CounterStorage storage = (flags & PersistentFlag) != 0 ? CounterStorage.Persistent : CounterStorage.Volatile;

        int concurrencyLevel = 0;
        long lowerBound = long.MinValue, upperBound = long.MaxValue;
        switch (type)
        {
            case CounterType.Weak:
                concurrencyLevel = await HotRodCodec.ReadVIntAsync(reader, ct);
                break;
            case CounterType.BoundedStrong:
                lowerBound = await HotRodCodec.ReadLongAsync(reader, ct);
                upperBound = await HotRodCodec.ReadLongAsync(reader, ct);
                break;
        }
        long initialValue = await HotRodCodec.ReadLongAsync(reader, ct);
        return new CounterConfiguration(type, initialValue, lowerBound, upperBound, concurrencyLevel, storage);
    }

    /// <summary>Packs a configuration's type and storage mode into the flags byte.</summary>
    public static byte EncodeFlags(CounterConfiguration configuration)
    {
        byte type = configuration.Type switch
        {
            CounterType.Weak => WeakType,
            CounterType.BoundedStrong => BoundedType,
            _ => 0x00, // unbounded strong
        };
        byte storage = configuration.Storage == CounterStorage.Persistent ? PersistentFlag : (byte)0x00;
        return (byte)(type | storage);
    }

    private static CounterType DecodeType(byte flags) => (flags & TypeMask) switch
    {
        WeakType => CounterType.Weak,
        BoundedType => CounterType.BoundedStrong,
        0x00 => CounterType.UnboundedStrong,
        _ => throw new HotRodException($"Unsupported counter type in configuration flags 0x{flags:X2}."),
    };

    /// <summary>
    /// Reads a counter event body, given the event opcode (0x66) already taken from the header: a
    /// status byte and a topology byte (both ignored — like cache events, a counter event never carries
    /// a topology update), the counter's name, the listener id, one state byte packing the old state in
    /// its low two bits and the new state in the next two, then the old and new values as 8-byte longs.
    /// This matches Infinispan's <c>Codec30.readCounterEvent</c>.
    /// </summary>
    public static async ValueTask<RawCounterEvent> ReadCounterEventAsync(PipeReader reader, CancellationToken ct)
    {
        await HotRodCodec.ReadByteAsync(reader, ct); // status: always success on an event
        await HotRodCodec.ReadByteAsync(reader, ct); // topology marker: events carry no topology update

        string counterName = await HotRodCodec.ReadStringAsync(reader, ct);
        byte[] listenerId = await HotRodCodec.ReadArrayAsync(reader, ct);
        byte encodedState = await HotRodCodec.ReadByteAsync(reader, ct);
        long oldValue = await HotRodCodec.ReadLongAsync(reader, ct);
        long newValue = await HotRodCodec.ReadLongAsync(reader, ct);

        return new RawCounterEvent(
            counterName, listenerId, oldValue, DecodeState(encodedState & 0x03), newValue, DecodeState((encodedState >> 2) & 0x03));
    }

    private static CounterEventState DecodeState(int bits) => bits switch
    {
        0x00 => CounterEventState.Valid,
        0x01 => CounterEventState.LowerBoundReached,
        0x02 => CounterEventState.UpperBoundReached,
        _ => throw new HotRodException($"Unsupported counter event state bits 0x{bits:X2}."),
    };
}
