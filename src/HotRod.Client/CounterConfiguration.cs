namespace HotRod.Client;

/// <summary>
/// The kind of a clustered counter. A strong counter keeps a single consistent value updated under
/// consensus (so it can enforce bounds and support compare-and-swap); a weak counter trades that
/// consistency for throughput by sharding its value across entries, exposing only an eventually
/// consistent read and no bounds. A strong counter is unbounded unless a lower/upper bound is set.
/// </summary>
public enum CounterType
{
    /// <summary>A strong counter with no bounds; its value may range over the whole 64-bit space.</summary>
    UnboundedStrong,

    /// <summary>A strong counter whose value is confined to <c>[lowerBound, upperBound]</c>.</summary>
    BoundedStrong,

    /// <summary>A weak, eventually consistent counter sharded for write throughput.</summary>
    Weak,
}

/// <summary>Whether a counter's value survives a cluster restart.</summary>
public enum CounterStorage
{
    /// <summary>The value is kept in memory only and is lost on a full cluster restart.</summary>
    Volatile,

    /// <summary>The value is persisted and restored when the cluster restarts.</summary>
    Persistent,
}

/// <summary>
/// The definition of a clustered counter: its type, initial value, storage mode, and the parameters
/// specific to its type (bounds for a bounded strong counter, a concurrency level for a weak counter).
/// Build one with <see cref="Strong"/>, <see cref="BoundedStrong"/>, or <see cref="Weak"/> and pass it
/// to <see cref="CounterManager.GetOrCreateStrongAsync"/> / <see cref="CounterManager.GetOrCreateWeakAsync"/>
/// to define the counter cluster-wide. A counter's configuration is fixed once it is created.
/// </summary>
public sealed class CounterConfiguration
{
    internal CounterConfiguration(
        CounterType type, long initialValue, long lowerBound, long upperBound, int concurrencyLevel, CounterStorage storage)
    {
        Type = type;
        InitialValue = initialValue;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        ConcurrencyLevel = concurrencyLevel;
        Storage = storage;
    }

    /// <summary>The counter's type.</summary>
    public CounterType Type { get; }

    /// <summary>The value the counter starts at and is restored to by a reset.</summary>
    public long InitialValue { get; }

    /// <summary>The inclusive lower bound; meaningful only for <see cref="CounterType.BoundedStrong"/>.</summary>
    public long LowerBound { get; }

    /// <summary>The inclusive upper bound; meaningful only for <see cref="CounterType.BoundedStrong"/>.</summary>
    public long UpperBound { get; }

    /// <summary>
    /// The number of internal shards a weak counter spreads its value across; meaningful only for
    /// <see cref="CounterType.Weak"/>. A higher level favours concurrent writes over read cost.
    /// </summary>
    public int ConcurrencyLevel { get; }

    /// <summary>Whether the value survives a cluster restart.</summary>
    public CounterStorage Storage { get; }

    /// <summary>Whether the counter is a strong counter (bounded or unbounded).</summary>
    public bool IsStrong => Type is CounterType.UnboundedStrong or CounterType.BoundedStrong;

    /// <summary>An unbounded strong counter.</summary>
    public static CounterConfiguration Strong(long initialValue = 0, CounterStorage storage = CounterStorage.Volatile) =>
        new(CounterType.UnboundedStrong, initialValue, long.MinValue, long.MaxValue, concurrencyLevel: 0, storage);

    /// <summary>
    /// A strong counter confined to <c>[lowerBound, upperBound]</c> (both inclusive). An update that would
    /// cross a bound is rejected and the counter's value is left unchanged.
    /// </summary>
    public static CounterConfiguration BoundedStrong(
        long lowerBound, long upperBound, long initialValue = 0, CounterStorage storage = CounterStorage.Volatile)
    {
        if (lowerBound > upperBound)
            throw new ArgumentException($"lowerBound ({lowerBound}) must not exceed upperBound ({upperBound}).", nameof(lowerBound));
        if (initialValue < lowerBound || initialValue > upperBound)
            throw new ArgumentOutOfRangeException(nameof(initialValue),
                $"initialValue ({initialValue}) must lie within [{lowerBound}, {upperBound}].");
        return new CounterConfiguration(CounterType.BoundedStrong, initialValue, lowerBound, upperBound, concurrencyLevel: 0, storage);
    }

    /// <summary>A weak, eventually consistent counter sharded across <paramref name="concurrencyLevel"/> entries.</summary>
    public static CounterConfiguration Weak(
        long initialValue = 0, int concurrencyLevel = 16, CounterStorage storage = CounterStorage.Volatile)
    {
        if (concurrencyLevel < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrencyLevel), concurrencyLevel, "concurrencyLevel must be at least 1.");
        return new CounterConfiguration(CounterType.Weak, initialValue, long.MinValue, long.MaxValue, concurrencyLevel, storage);
    }

    /// <summary>Returns a human-readable summary of the configuration's fields.</summary>
    public override string ToString() =>
        $"CounterConfiguration{{type={Type}, initialValue={InitialValue}, lowerBound={LowerBound}, " +
        $"upperBound={UpperBound}, concurrencyLevel={ConcurrencyLevel}, storage={Storage}}}";
}
