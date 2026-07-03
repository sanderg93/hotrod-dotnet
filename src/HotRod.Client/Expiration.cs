using System.Buffers;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// Entry expiration. A null <see cref="Lifespan"/> or <see cref="MaxIdle"/> uses the cache's
/// configured default; a value sets that duration (encoded in milliseconds).
/// </summary>
public readonly struct Expiration(TimeSpan? lifespan = null, TimeSpan? maxIdle = null)
{
    public TimeSpan? Lifespan { get; } = lifespan;
    public TimeSpan? MaxIdle { get; } = maxIdle;

    /// <summary>Uses the cache's configured expiration for both lifespan and maxIdle.</summary>
    public static Expiration Default => default;

    /// <summary>Writes the time-units byte followed by any lifespan/maxIdle values (milliseconds).</summary>
    internal void WriteTo(IBufferWriter<byte> writer)
    {
        byte lifespanUnit = Lifespan is null ? Constants.TimeUnitDefault : Constants.TimeUnitMilliseconds;
        byte maxIdleUnit = MaxIdle is null ? Constants.TimeUnitDefault : Constants.TimeUnitMilliseconds;
        HotRodCodec.WriteByte(writer, (byte)((lifespanUnit << 4) | maxIdleUnit));

        if (Lifespan is TimeSpan l)
            HotRodCodec.WriteVLong(writer, (long)l.TotalMilliseconds);
        if (MaxIdle is TimeSpan m)
            HotRodCodec.WriteVLong(writer, (long)m.TotalMilliseconds);
    }
}
