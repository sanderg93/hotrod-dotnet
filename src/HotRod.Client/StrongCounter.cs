namespace HotRod.Client;

/// <summary>
/// A handle to a strong (strongly consistent) clustered counter. Every read reflects all completed
/// updates, and a bounded counter refuses any update that would cross its bounds (surfaced as a
/// <see cref="CounterOutOfBoundsException"/>). Supports atomic compare-and-swap. Obtain one from
/// <see cref="CounterManager.GetOrCreateStrongAsync"/> or <see cref="CounterManager.GetStrongCounterAsync"/>.
/// A handle is a lightweight reference to the named counter; it holds no server resources.
/// </summary>
public sealed class StrongCounter
{
    private readonly HotRodClient _client;

    internal StrongCounter(HotRodClient client, string name, CounterConfiguration configuration)
    {
        _client = client;
        Name = name;
        Configuration = configuration;
    }

    /// <summary>The counter's name.</summary>
    public string Name { get; }

    /// <summary>The counter's configuration as defined on the server.</summary>
    public CounterConfiguration Configuration { get; }

    /// <summary>Returns the counter's current value.</summary>
    public ValueTask<long> GetValueAsync(CancellationToken ct = default) =>
        CounterOps.GetValueAsync(_client, Name, ct);

    /// <summary>
    /// Atomically adds <paramref name="delta"/> to the value and returns the result. Throws
    /// <see cref="CounterOutOfBoundsException"/> if the counter is bounded and the update would cross a bound.
    /// </summary>
    public ValueTask<long> AddAndGetAsync(long delta, CancellationToken ct = default) =>
        CounterOps.AddAndGetAsync(_client, Name, delta, ct);

    /// <summary>Atomically increments the value by one and returns the result.</summary>
    public ValueTask<long> IncrementAsync(CancellationToken ct = default) =>
        CounterOps.AddAndGetAsync(_client, Name, 1, ct);

    /// <summary>Atomically decrements the value by one and returns the result.</summary>
    public ValueTask<long> DecrementAsync(CancellationToken ct = default) =>
        CounterOps.AddAndGetAsync(_client, Name, -1, ct);

    /// <summary>
    /// Atomically sets the value to <paramref name="update"/> if it currently equals <paramref name="expect"/>,
    /// and returns the value the counter held before the call (the compare-and-swap "witness"). If the returned
    /// value equals <paramref name="expect"/> the swap took effect.
    /// </summary>
    public ValueTask<long> CompareAndSwapAsync(long expect, long update, CancellationToken ct = default) =>
        CounterOps.CompareAndSwapAsync(_client, Name, expect, update, ct);

    /// <summary>
    /// Atomically sets the value to <paramref name="update"/> if it currently equals <paramref name="expect"/>;
    /// returns true if the swap took effect.
    /// </summary>
    public async ValueTask<bool> CompareAndSetAsync(long expect, long update, CancellationToken ct = default) =>
        await CounterOps.CompareAndSwapAsync(_client, Name, expect, update, ct) == expect;

    /// <summary>Resets the counter to its initial value.</summary>
    public ValueTask ResetAsync(CancellationToken ct = default) =>
        CounterOps.ResetAsync(_client, Name, ct);

    /// <summary>Removes this counter cluster-wide.</summary>
    public ValueTask RemoveAsync(CancellationToken ct = default) =>
        CounterOps.RemoveAsync(_client, Name, ct);
}
