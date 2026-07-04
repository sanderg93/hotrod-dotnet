using System.Buffers;
using System.IO.Pipelines;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// The entry point to Infinispan's clustered counters over HotRod. A counter is a cluster-wide named
/// 64-bit value: strong counters are strongly consistent and may be bounded and compared-and-swapped;
/// weak counters trade consistency for write throughput. Define (or look up) a counter with the
/// <c>GetOrCreate</c>/<c>Get</c> methods, then operate on the returned <see cref="StrongCounter"/> or
/// <see cref="WeakCounter"/>. The manager is stateless and cheap; obtain it from
/// <see cref="HotRodClient.Counters"/>.
/// <para>
/// Counter operations are not cache-scoped — they travel on the standard HotRod header with an empty
/// cache name, carrying the counter's name in the request body — so they reuse the client's existing
/// connection pool and topology handling.
/// </para>
/// </summary>
public sealed class CounterManager
{
    private readonly HotRodClient _client;

    internal CounterManager(HotRodClient client) => _client = client;

    /// <summary>
    /// Defines a strong counter with <paramref name="configuration"/> if it does not exist, then returns a
    /// handle bound to the counter's actual (server-side) configuration. If a counter already exists under
    /// <paramref name="name"/> its configuration is left unchanged; this throws if it exists as a weak counter.
    /// </summary>
    public async ValueTask<StrongCounter> GetOrCreateStrongAsync(string name, CounterConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsStrong)
            throw new ArgumentException("A strong counter requires a strong (bounded or unbounded) configuration.", nameof(configuration));

        await CounterOps.DefineAsync(_client, name, configuration, ct);
        return await GetStrongCounterAsync(name, ct);
    }

    /// <summary>
    /// Defines a weak counter with <paramref name="configuration"/> if it does not exist, then returns a
    /// handle bound to the counter's actual (server-side) configuration. If a counter already exists under
    /// <paramref name="name"/> its configuration is left unchanged; this throws if it exists as a strong counter.
    /// </summary>
    public async ValueTask<WeakCounter> GetOrCreateWeakAsync(string name, CounterConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Type != CounterType.Weak)
            throw new ArgumentException("A weak counter requires a weak configuration.", nameof(configuration));

        await CounterOps.DefineAsync(_client, name, configuration, ct);
        return await GetWeakCounterAsync(name, ct);
    }

    /// <summary>Returns a handle to an existing strong counter; throws if it is undefined or is a weak counter.</summary>
    public async ValueTask<StrongCounter> GetStrongCounterAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        CounterConfiguration configuration = await CounterOps.GetConfigurationAsync(_client, name, ct)
            ?? throw new UndefinedCounterException(name);
        if (!configuration.IsStrong)
            throw new InvalidOperationException($"Counter '{name}' is a weak counter, not a strong counter.");
        return new StrongCounter(_client, name, configuration);
    }

    /// <summary>Returns a handle to an existing weak counter; throws if it is undefined or is a strong counter.</summary>
    public async ValueTask<WeakCounter> GetWeakCounterAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        CounterConfiguration configuration = await CounterOps.GetConfigurationAsync(_client, name, ct)
            ?? throw new UndefinedCounterException(name);
        if (configuration.Type != CounterType.Weak)
            throw new InvalidOperationException($"Counter '{name}' is a strong counter, not a weak counter.");
        return new WeakCounter(_client, name, configuration);
    }

    /// <summary>
    /// Defines a counter cluster-wide from <paramref name="configuration"/>. Returns true if it was newly
    /// created, or false if a counter already existed under <paramref name="name"/> (whose configuration is
    /// then left untouched).
    /// </summary>
    public ValueTask<bool> DefineAsync(string name, CounterConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configuration);
        return CounterOps.DefineAsync(_client, name, configuration, ct);
    }

    /// <summary>Returns true if a counter is defined under <paramref name="name"/>.</summary>
    public ValueTask<bool> IsDefinedAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return CounterOps.IsDefinedAsync(_client, name, ct);
    }

    /// <summary>Returns the configuration of the named counter, or null if it is not defined.</summary>
    public ValueTask<CounterConfiguration?> GetConfigurationAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return CounterOps.GetConfigurationAsync(_client, name, ct);
    }

    /// <summary>Removes the named counter cluster-wide.</summary>
    public ValueTask RemoveAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return CounterOps.RemoveAsync(_client, name, ct);
    }

    /// <summary>Returns the names of every counter defined in the cluster.</summary>
    public ValueTask<IReadOnlyCollection<string>> GetCounterNamesAsync(CancellationToken ct = default) =>
        CounterOps.GetCounterNamesAsync(_client, ct);
}

/// <summary>
/// The wire operations shared by <see cref="CounterManager"/>, <see cref="StrongCounter"/>, and
/// <see cref="WeakCounter"/>. Each frames a counter request on an empty-cache-name header (the counter's
/// name is the first body field) and interprets the response status: a key-does-not-exist status means
/// the counter is undefined, and a not-executed-with-previous status means a bounded counter refused the
/// update because it would cross a bound.
/// </summary>
internal static class CounterOps
{
    /// <summary>Runs one counter request/response exchange with an empty cache name and no single-hop routing.</summary>
    private static ValueTask<T> ExecuteAsync<T>(
        HotRodClient client,
        byte opcode,
        Action<IBufferWriter<byte>> writeBody,
        Func<byte, PipeReader, CancellationToken, ValueTask<T>> readBody,
        CancellationToken ct) =>
        client.ExecuteAsync(string.Empty, opcode, flags: 0, routingKey: null, DataFormat.None, writeBody, readBody, ct);

    public static ValueTask<bool> DefineAsync(HotRodClient client, string name, CounterConfiguration configuration, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterCreateRequest,
            w =>
            {
                CounterCodec.WriteCounterName(w, name);
                CounterCodec.WriteConfiguration(w, configuration);
            },
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);

    public static ValueTask<bool> IsDefinedAsync(HotRodClient client, string name, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterIsDefinedRequest,
            w => CounterCodec.WriteCounterName(w, name),
            (status, _, _) => ValueTask.FromResult(ResponseStatus.IsSuccess(status)), ct);

    public static ValueTask<CounterConfiguration?> GetConfigurationAsync(HotRodClient client, string name, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterGetConfigurationRequest,
            w => CounterCodec.WriteCounterName(w, name),
            async (status, reader, c) =>
                ResponseStatus.KeyDoesNotExist(status) ? null : await CounterCodec.ReadConfigurationAsync(reader, c), ct);

    public static ValueTask<long> GetValueAsync(HotRodClient client, string name, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterGetRequest,
            w => CounterCodec.WriteCounterName(w, name),
            async (status, reader, c) =>
            {
                ThrowIfUndefined(status, name);
                return await HotRodCodec.ReadLongAsync(reader, c);
            }, ct);

    public static ValueTask<long> AddAndGetAsync(HotRodClient client, string name, long delta, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterAddAndGetRequest,
            w =>
            {
                CounterCodec.WriteCounterName(w, name);
                HotRodCodec.WriteLong(w, delta);
            },
            async (status, reader, c) =>
            {
                ThrowIfUndefined(status, name);
                ThrowIfOutOfBounds(status, name);
                return await HotRodCodec.ReadLongAsync(reader, c);
            }, ct);

    public static ValueTask<long> CompareAndSwapAsync(HotRodClient client, string name, long expect, long update, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterCasRequest,
            w =>
            {
                CounterCodec.WriteCounterName(w, name);
                HotRodCodec.WriteLong(w, expect);
                HotRodCodec.WriteLong(w, update);
            },
            async (status, reader, c) =>
            {
                ThrowIfUndefined(status, name);
                ThrowIfOutOfBounds(status, name);
                return await HotRodCodec.ReadLongAsync(reader, c);
            }, ct);

    public static async ValueTask ResetAsync(HotRodClient client, string name, CancellationToken ct) =>
        await ExecuteAsync(client, Constants.CounterResetRequest,
            w => CounterCodec.WriteCounterName(w, name),
            (status, _, _) => { ThrowIfUndefined(status, name); return ValueTask.FromResult(true); }, ct);

    public static async ValueTask RemoveAsync(HotRodClient client, string name, CancellationToken ct) =>
        await ExecuteAsync(client, Constants.CounterRemoveRequest,
            w => CounterCodec.WriteCounterName(w, name),
            (_, _, _) => ValueTask.FromResult(true), ct);

    public static ValueTask<IReadOnlyCollection<string>> GetCounterNamesAsync(HotRodClient client, CancellationToken ct) =>
        ExecuteAsync(client, Constants.CounterGetNamesRequest,
            _ => { }, // the get-names request carries no counter name; the body is empty
            async (_, reader, c) =>
            {
                int count = await HotRodCodec.ReadVIntAsync(reader, c);
                var names = new List<string>(count);
                for (int i = 0; i < count; i++)
                    names.Add(await HotRodCodec.ReadStringAsync(reader, c));
                return (IReadOnlyCollection<string>)names;
            }, ct);

    private static void ThrowIfUndefined(byte status, string name)
    {
        if (ResponseStatus.KeyDoesNotExist(status))
            throw new UndefinedCounterException(name);
    }

    private static void ThrowIfOutOfBounds(byte status, string name)
    {
        if (status == Constants.StatusNotExecutedWithPrevious)
            throw new CounterOutOfBoundsException(name);
    }
}

/// <summary>The base type for errors specific to counter operations.</summary>
public class CounterException : HotRodException
{
    /// <summary>Creates the exception with a message and no associated server status.</summary>
    public CounterException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the server status it originated from.</summary>
    public CounterException(string message, byte status) : base(message, status) { }
}

/// <summary>Raised when an operation targets a counter that is not defined (server status key-does-not-exist).</summary>
public sealed class UndefinedCounterException : CounterException
{
    /// <summary>Creates the exception for the named counter.</summary>
    public UndefinedCounterException(string name)
        : base($"Counter '{name}' is not defined.", Protocol.Constants.StatusKeyDoesNotExist) => CounterName = name;

    /// <summary>The name of the undefined counter.</summary>
    public string CounterName { get; }
}

/// <summary>
/// Raised when an update to a bounded strong counter is refused because it would cross the counter's
/// lower or upper bound (server status not-executed-with-previous). The counter's value is left unchanged.
/// </summary>
public sealed class CounterOutOfBoundsException : CounterException
{
    /// <summary>Creates the exception for the named counter.</summary>
    public CounterOutOfBoundsException(string name)
        : base($"Counter '{name}' would exceed its configured bounds; the update was not applied.",
            Protocol.Constants.StatusNotExecutedWithPrevious) => CounterName = name;

    /// <summary>The name of the counter whose bound was hit.</summary>
    public string CounterName { get; }
}
