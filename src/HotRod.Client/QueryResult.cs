namespace HotRod.Client;

/// <summary>
/// The outcome of an Ickle query: the decoded result rows plus the server's hit count. The hit count is
/// the total number of matches the query has (ignoring paging), and <see cref="HitCountExact"/> states
/// whether that total is exact or a lower-bound estimate the server stopped counting at. Rows reflect
/// only the current page (after <c>startOffset</c>/<c>maxResults</c>).
/// </summary>
public sealed class QueryResult
{
    internal QueryResult(int hitCount, bool hitCountExact, int projectionSize, IReadOnlyList<QueryResultRow> rows)
    {
        HitCount = hitCount;
        HitCountExact = hitCountExact;
        ProjectionSize = projectionSize;
        Rows = rows;
    }

    /// <summary>The total number of matches for the query, ignoring paging. See <see cref="HitCountExact"/>.</summary>
    public int HitCount { get; }

    /// <summary>True when <see cref="HitCount"/> is exact; false when it is a lower bound the server capped counting at.</summary>
    public bool HitCountExact { get; }

    /// <summary>
    /// The number of columns a projection query selects (<c>SELECT a, b, …</c>). Zero for an entity query
    /// (<c>FROM …</c> with no projection), whose rows each carry a single entity column.
    /// </summary>
    public int ProjectionSize { get; }

    /// <summary>The rows on the current page, in server order.</summary>
    public IReadOnlyList<QueryResultRow> Rows { get; }
}

/// <summary>
/// One query result row: an ordered list of column values. A projection query yields one column per
/// selected expression; an entity query yields a single column holding the matched entity. Each value is
/// a decoded scalar (number, bool, string, or byte[]) when the server wrapped a primitive, the raw
/// marshalled message bytes (<see cref="T:byte[]"/>) for an entity or nested message that a richer
/// decoder must interpret, or null for an absent value.
/// </summary>
public sealed class QueryResultRow
{
    internal QueryResultRow(IReadOnlyList<object?> columns) => Columns = columns;

    /// <summary>The column values in selection order.</summary>
    public IReadOnlyList<object?> Columns { get; }

    /// <summary>The value of column <paramref name="index"/>.</summary>
    public object? this[int index] => Columns[index];

    /// <summary>The single column of an entity query's row; throws when the row is not single-valued.</summary>
    public object? Value =>
        Columns.Count == 1
            ? Columns[0]
            : throw new InvalidOperationException($"This row has {Columns.Count} columns; use {nameof(Columns)} to read a projection.");
}
