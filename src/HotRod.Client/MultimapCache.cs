using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using HotRod.Client.Marshalling;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// A handle to a remote multimap cache: a cache where each key holds a collection of values instead of
/// one (Infinispan's <c>RemoteMultimapCache</c>). The byte-array overloads exchange raw bytes; the
/// string overloads go through the cache's <see cref="CacheEncoding"/>, as <see cref="RemoteCache"/>
/// does. All operations are asynchronous.
/// <para>
/// <see cref="SupportsDuplicates"/> selects the multimap's duplicate semantics: true stores every
/// <see cref="PutAsync(byte[], byte[], Expiration, CancellationToken)"/> as a distinct value (list
/// semantics), false (the default) collapses a value already present under the key (set semantics). It
/// must match how the multimap cache is configured server-side — it is not negotiated, only asserted —
/// and travels with every request, mirroring the Java client's per-handle flag of the same name.
/// </para>
/// </summary>
public sealed class MultimapCache
{
    private readonly HotRodClient _client;
    private readonly string _name;
    private readonly CacheMarshaller _marshaller;
    private readonly bool _supportsDuplicates;

    internal MultimapCache(HotRodClient client, string name, CacheEncoding encoding, bool supportsDuplicates)
    {
        _client = client;
        _name = name;
        _marshaller = CacheMarshaller.For(encoding);
        _supportsDuplicates = supportsDuplicates;
    }

    /// <summary>
    /// Whether this handle stores duplicate values under a key as distinct entries (list semantics)
    /// rather than collapsing them (set semantics) — set when the handle was obtained and sent with
    /// every request; it does not change the server's actual configuration.
    /// </summary>
    public bool SupportsDuplicates => _supportsDuplicates;

    // -- Basic operations -----------------------------------------------------

    /// <summary>Adds a value under key, optionally overriding the cache's default expiration.</summary>
    public async ValueTask PutAsync(byte[] key, byte[] value, Expiration expiration = default, CancellationToken ct = default) =>
        await Execute(Constants.MultimapPutRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                expiration.WriteTo(w);
                HotRodCodec.WriteArray(w, value);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            (_, _, _) => ValueTask.FromResult(true), ct);

    /// <summary>Returns every value stored under key, or an empty collection if the key is absent.</summary>
    public ValueTask<IReadOnlyList<byte[]>> GetAsync(byte[] key, CancellationToken ct = default) =>
        Execute(Constants.MultimapGetRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            async (status, reader, c) =>
                ResponseStatus.KeyDoesNotExist(status)
                    ? (IReadOnlyList<byte[]>)[]
                    : await MultimapCodec.ReadValueCollectionAsync(reader, c), ct);

    /// <summary>Removes every value stored under key; returns true if the key held any values.</summary>
    public ValueTask<bool> RemoveAsync(byte[] key, CancellationToken ct = default) =>
        Execute(Constants.MultimapRemoveRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            MultimapCodec.ReadBoolResponseAsync, ct);

    /// <summary>Removes one occurrence of value under key; returns true if a value was removed.</summary>
    public ValueTask<bool> RemoveEntryAsync(byte[] key, byte[] value, CancellationToken ct = default) =>
        Execute(Constants.MultimapRemoveEntryRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                MultimapCodec.WriteInfiniteExpiration(w);
                HotRodCodec.WriteArray(w, value);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            MultimapCodec.ReadBoolResponseAsync, ct);

    /// <summary>Returns true if key holds any values.</summary>
    public ValueTask<bool> ContainsKeyAsync(byte[] key, CancellationToken ct = default) =>
        Execute(Constants.MultimapContainsKeyRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            MultimapCodec.ReadBoolResponseAsync, ct);

    /// <summary>Returns true if value is one of the values stored under key.</summary>
    public ValueTask<bool> ContainsEntryAsync(byte[] key, byte[] value, CancellationToken ct = default) =>
        Execute(Constants.MultimapContainsEntryRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                MultimapCodec.WriteInfiniteExpiration(w);
                HotRodCodec.WriteArray(w, value);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            MultimapCodec.ReadBoolResponseAsync, ct);

    /// <summary>Returns true if value is stored under any key in the multimap.</summary>
    public ValueTask<bool> ContainsValueAsync(byte[] value, CancellationToken ct = default) =>
        Execute(Constants.MultimapContainsValueRequest, routingKey: null,
            w =>
            {
                MultimapCodec.WriteInfiniteExpiration(w);
                HotRodCodec.WriteArray(w, value);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            MultimapCodec.ReadBoolResponseAsync, ct);

    /// <summary>Returns the number of key/value pairs across the whole multimap (not the number of keys).</summary>
    public ValueTask<long> SizeAsync(CancellationToken ct = default) =>
        Execute(Constants.MultimapSizeRequest, routingKey: null,
            w => MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates),
            (_, reader, c) => HotRodCodec.ReadVLongAsync(reader, c), ct);

    // -- Metadata -------------------------------------------------------------

    /// <summary>Returns every value under key with the entry's server metadata (version, timestamps, expiration), or null if absent.</summary>
    public ValueTask<MultimapMetadataValue<byte[]>?> GetWithMetadataAsync(byte[] key, CancellationToken ct = default) =>
        Execute(Constants.MultimapGetWithMetadataRequest, key,
            w =>
            {
                HotRodCodec.WriteArray(w, key);
                MultimapCodec.WriteSupportsDuplicates(w, _supportsDuplicates);
            },
            async (status, reader, c) =>
            {
                if (ResponseStatus.KeyDoesNotExist(status))
                    return null;
                EntryMetadata meta = await HotRodCodec.ReadMetadataAsync(reader, c);
                IReadOnlyList<byte[]> values = await MultimapCodec.ReadValueCollectionAsync(reader, c);
                return MultimapMetadataValues.From(meta, values);
            }, ct);

    // -- String convenience overloads (via the cache's encoding) --------------

    /// <summary>String-keyed overload of <see cref="PutAsync(byte[], byte[], Expiration, CancellationToken)"/>.</summary>
    public ValueTask PutAsync(string key, string value, Expiration expiration = default, CancellationToken ct = default) =>
        PutAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), expiration, ct);

    /// <summary>String-keyed overload of <see cref="GetAsync(byte[], CancellationToken)"/>.</summary>
    public async ValueTask<IReadOnlyList<string>> GetAsync(string key, CancellationToken ct = default)
    {
        IReadOnlyList<byte[]> values = await GetAsync(_marshaller.Marshal(key), ct);
        var result = new List<string>(values.Count);
        foreach (byte[] value in values)
            result.Add(_marshaller.Unmarshal(value));
        return result;
    }

    /// <summary>String-keyed overload of <see cref="RemoveAsync(byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default) =>
        RemoveAsync(_marshaller.Marshal(key), ct);

    /// <summary>String-keyed overload of <see cref="RemoveEntryAsync(byte[], byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> RemoveEntryAsync(string key, string value, CancellationToken ct = default) =>
        RemoveEntryAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), ct);

    /// <summary>String-keyed overload of <see cref="ContainsKeyAsync(byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> ContainsKeyAsync(string key, CancellationToken ct = default) =>
        ContainsKeyAsync(_marshaller.Marshal(key), ct);

    /// <summary>String-keyed overload of <see cref="ContainsEntryAsync(byte[], byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> ContainsEntryAsync(string key, string value, CancellationToken ct = default) =>
        ContainsEntryAsync(_marshaller.Marshal(key), _marshaller.Marshal(value), ct);

    /// <summary>String-keyed overload of <see cref="ContainsValueAsync(byte[], CancellationToken)"/>.</summary>
    public ValueTask<bool> ContainsValueAsync(string value, CancellationToken ct = default) =>
        ContainsValueAsync(_marshaller.Marshal(value), ct);

    /// <summary>String-keyed overload of <see cref="GetWithMetadataAsync(byte[], CancellationToken)"/>.</summary>
    public async ValueTask<MultimapMetadataValue<string>?> GetWithMetadataAsync(string key, CancellationToken ct = default)
    {
        MultimapMetadataValue<byte[]>? meta = await GetWithMetadataAsync(_marshaller.Marshal(key), ct);
        if (meta is null)
            return null;
        var values = new List<string>(meta.Values.Count);
        foreach (byte[] value in meta.Values)
            values.Add(_marshaller.Unmarshal(value));
        return new MultimapMetadataValue<string>(values, meta.Version, meta.Created, meta.Lifespan, meta.LastUsed, meta.MaxIdle);
    }

    // -- Plumbing ---------------------------------------------------------------

    private ValueTask<T> Execute<T>(
        byte opcode,
        byte[]? routingKey,
        Action<IBufferWriter<byte>> writeBody,
        Func<byte, PipeReader, CancellationToken, ValueTask<T>> readBody,
        CancellationToken ct) =>
        _client.ExecuteAsync(_name, opcode, flags: 0, routingKey, _marshaller.DataFormat, writeBody, readBody, ct);
}
