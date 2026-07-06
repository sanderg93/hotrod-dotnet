using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// The value returned by <see cref="RemoteCache.GetStreamAsync(byte[], int, System.Threading.CancellationToken)"/>:
/// a server-side cursor (GetStreamStart/Next/End) that fetches the value one batch-sized chunk at a time
/// as it is read, so a large entry can be processed (e.g. copied to a file) without holding it all in
/// memory at once — mirroring how <see cref="RemoteCache.IterateAsync"/> holds one connection for a
/// server-side cursor across several exchanges. Forward-only and read-only. The underlying connection is
/// reserved for this stream's whole lifetime; dispose it (whether or not it was read to the end) to send
/// GetStreamEnd and release the connection back to its pool.
/// </summary>
public sealed class HotRodValueStream : Stream
{
    private readonly HotRodConnection _connection;
    private readonly Cluster.ConnectionLease _lease;
    private readonly string _cacheName;
    private readonly DataFormat _dataFormat;
    private readonly int _streamId;
    private byte[] _chunk;
    private int _chunkOffset;
    private bool _complete;
    private bool _ended;

    internal HotRodValueStream(
        HotRodConnection connection, Cluster.ConnectionLease lease, string cacheName, DataFormat dataFormat,
        int streamId, byte[] firstChunk, bool complete, long version)
    {
        _connection = connection;
        _lease = lease;
        _cacheName = cacheName;
        _dataFormat = dataFormat;
        _streamId = streamId;
        _chunk = firstChunk;
        _complete = complete;
        Version = version;
    }

    /// <summary>The entry's version at the moment this stream was started (as from <see cref="RemoteCache.GetWithVersionAsync(byte[], CancellationToken)"/>).</summary>
    public long Version { get; }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <summary>Not supported: the total length is never sent up front, only chunk by chunk.</summary>
    public override long Length => throw new NotSupportedException("HotRodValueStream does not know its total length up front.");

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException("HotRodValueStream is forward-only.");
        set => throw new NotSupportedException("HotRodValueStream is forward-only.");
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("HotRodValueStream is forward-only.");

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException("HotRodValueStream is read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("HotRodValueStream is read-only.");

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken);

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_ended, this);
        if (buffer.Length == 0)
            return 0;

        if (_chunkOffset == _chunk.Length)
        {
            if (_complete)
            {
                await EndAsync(cancellationToken);
                return 0;
            }

            GetStreamChunk next = await _connection.ExecuteAsync(_cacheName, Constants.GetStreamNextRequest, flags: 0, _dataFormat,
                w => StreamCodec.WriteStreamId(w, _streamId),
                (status, reader, c) => StreamCodec.ReadGetStreamNextAsync(status, reader, c), cancellationToken);

            _chunk = next.Chunk;
            _chunkOffset = 0;
            _complete = next.Complete;

            if (_chunk.Length == 0)
            {
                if (_complete)
                {
                    await EndAsync(cancellationToken);
                    return 0;
                }
                // An empty, non-final chunk is legal (if unusual) — try again for real data.
                return await ReadAsync(buffer, cancellationToken);
            }
        }

        int n = Math.Min(buffer.Length, _chunk.Length - _chunkOffset);
        _chunk.AsSpan(_chunkOffset, n).CopyTo(buffer.Span);
        _chunkOffset += n;
        return n;
    }

    /// <summary>Ends the cursor (sending GetStreamEnd if not already done), then returns the connection to the pool.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            EndAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync() => await EndAsync(CancellationToken.None);

    private async ValueTask EndAsync(CancellationToken ct)
    {
        if (_ended)
            return;
        _ended = true;

        try
        {
            await _connection.ExecuteAsync(_cacheName, Constants.GetStreamEndRequest, flags: 0, _dataFormat,
                w => StreamCodec.WriteStreamId(w, _streamId),
                (_, _, _) => ValueTask.FromResult(true), ct);
        }
        catch
        {
            // Best-effort: the cursor may already be gone (expired) or the connection faulted; either
            // way there is nothing more useful to do server-side, and the lease below discards a faulted
            // connection rather than returning it to the pool.
        }
        finally
        {
            await _lease.DisposeAsync();
        }
    }
}
