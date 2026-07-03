namespace HotRod.Client;

/// <summary>
/// A handle to a weak (eventually consistent) clustered counter. A weak counter shards its value for
/// write throughput and is unbounded, so it supports only additions and a value read that may lag the
/// most recent updates; it has no compare-and-swap. Obtain one from
/// <see cref="CounterManager.GetOrCreateWeakAsync"/> or <see cref="CounterManager.GetWeakCounterAsync"/>.
/// A handle is a lightweight reference to the named counter; it holds no server resources.
/// </summary>
public sealed class WeakCounter
{
    private readonly HotRodClient _client;

    internal WeakCounter(HotRodClient client, string name, CounterConfiguration configuration)
    {
        _client = client;
        Name = name;
        Configuration = configuration;
    }

    /// <summary>The counter's name.</summary>
    public string Name { get; }

    /// <summary>The counter's configuration as defined on the server.</summary>
    public CounterConfiguration Configuration { get; }

    /// <summary>Returns the counter's current value, which may not yet reflect the most recent additions.</summary>
    public ValueTask<long> GetValueAsync(CancellationToken ct = default) =>
        CounterOps.GetValueAsync(_client, Name, ct);

    /// <summary>Adds <paramref name="delta"/> to the value. A weak counter's add returns no value.</summary>
    public async ValueTask AddAsync(long delta, CancellationToken ct = default) =>
        await CounterOps.AddAndGetAsync(_client, Name, delta, ct);

    /// <summary>Increments the value by one.</summary>
    public ValueTask IncrementAsync(CancellationToken ct = default) => AddAsync(1, ct);

    /// <summary>Decrements the value by one.</summary>
    public ValueTask DecrementAsync(CancellationToken ct = default) => AddAsync(-1, ct);

    /// <summary>Resets the counter to its initial value.</summary>
    public ValueTask ResetAsync(CancellationToken ct = default) =>
        CounterOps.ResetAsync(_client, Name, ct);

    /// <summary>Removes this counter cluster-wide.</summary>
    public ValueTask RemoveAsync(CancellationToken ct = default) =>
        CounterOps.RemoveAsync(_client, Name, ct);
}
