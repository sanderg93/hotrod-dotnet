using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// The entry point for Ickle remote queries against one cache, modelled on Java's <c>QueryFactory</c>.
/// Obtain it from <see cref="HotRodClient.Query"/>, then <see cref="Create"/> a query from an Ickle
/// string. The target cache should use ProtoStream encoding and the server must have the matching
/// protobuf schema registered, since results come back as ProtoStream-wrapped values.
/// </summary>
public sealed class QueryFactory
{
    private readonly RemoteCache _cache;

    internal QueryFactory(RemoteCache cache) => _cache = cache;

    /// <summary>
    /// Builds a query from an Ickle string such as <c>"FROM tutorial.Person WHERE age &gt; 30"</c> or a
    /// projection like <c>"SELECT p.name, p.age FROM tutorial.Person p"</c>. The returned query is a
    /// builder — set paging and named parameters on it, then call <see cref="RemoteQuery.ExecuteAsync"/>.
    /// </summary>
    public RemoteQuery Create(string queryString)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryString);
        return new RemoteQuery(_cache, queryString);
    }
}

/// <summary>
/// A built Ickle query: an immutable-per-setter builder that carries the query string, optional paging
/// window, named parameters, and hit-count accuracy, and runs the query with
/// <see cref="ExecuteAsync"/>. Setters return the same instance so they chain. Modelled on Java's
/// <c>Query</c>: <c>StartOffset</c>/<c>SetMaxResults</c> page the results and named parameters bind
/// <c>:name</c> placeholders in the query string.
/// </summary>
public sealed class RemoteQuery
{
    private readonly RemoteCache _cache;
    private readonly string _queryString;
    private Dictionary<string, object>? _namedParameters;
    private long _startOffset;
    private int _maxResults = -1;       // -1 => unset; the server applies its default page size
    private int _hitCountAccuracy = -1; // -1 => unset; the server applies its default accuracy

    internal RemoteQuery(RemoteCache cache, string queryString)
    {
        _cache = cache;
        _queryString = queryString;
    }

    /// <summary>The Ickle query string this query runs.</summary>
    public string QueryString => _queryString;

    /// <summary>Skips the first <paramref name="offset"/> matches (paging). Zero (the default) starts at the first match.</summary>
    public RemoteQuery StartOffset(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _startOffset = offset;
        return this;
    }

    /// <summary>Caps the page at <paramref name="maxResults"/> rows. Left unset the server applies its default page size.</summary>
    public RemoteQuery SetMaxResults(int maxResults)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxResults);
        _maxResults = maxResults;
        return this;
    }

    /// <summary>
    /// Requests a hit count accurate to at least <paramref name="accuracy"/> matches; a larger value trades
    /// server work for a more exact <see cref="QueryResult.HitCount"/>. Left unset the server's default applies.
    /// </summary>
    public RemoteQuery SetHitCountAccuracy(int accuracy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(accuracy);
        _hitCountAccuracy = accuracy;
        return this;
    }

    /// <summary>Binds a <c>:name</c> placeholder in the query string to <paramref name="value"/> (a scalar: number, bool, or string).</summary>
    public RemoteQuery SetParameter(string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        (_namedParameters ??= new Dictionary<string, object>())[name] = value;
        return this;
    }

    /// <summary>Runs the query and returns the current page of rows together with the total hit count.</summary>
    public async ValueTask<QueryResult> ExecuteAsync(CancellationToken ct = default)
    {
        byte[] request = QueryCodec.EncodeRequest(_queryString, _startOffset, _maxResults, _namedParameters, _hitCountAccuracy);
        byte[] response = await _cache.ExecuteQueryAsync(request, ct);
        return QueryCodec.DecodeResponse(response);
    }
}
