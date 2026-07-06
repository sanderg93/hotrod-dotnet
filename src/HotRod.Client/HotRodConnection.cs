using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HotRod.Client.Logging;
using HotRod.Client.Protocol;
using Microsoft.Extensions.Logging;

namespace HotRod.Client;

/// <summary>
/// A single asynchronous connection to one Infinispan node speaking HotRod 4.1 at the configured
/// <see cref="ClientIntelligence"/> level. Built on <see cref="System.IO.Pipelines"/>: requests are
/// written into a <see cref="PipeWriter"/> and flushed, responses are pulled from a
/// <see cref="PipeReader"/>. Requests are serialised behind a semaphore — one in flight at a time.
/// Pooled per node and handed out one caller at a time by <see cref="HotRodClient"/>.
/// </summary>
internal sealed class HotRodConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _transport;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly ITopologyCoordinator _coordinator;
    private readonly ClientIntelligence _intelligence;
    private readonly ServerAddress _server;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _messageId;
    private volatile bool _faulted;

    private HotRodConnection(
        TcpClient tcp, Stream transport, ITopologyCoordinator coordinator, ClientIntelligence intelligence,
        ServerAddress server, ILogger logger)
    {
        _tcp = tcp;
        _transport = transport;
        _coordinator = coordinator;
        _intelligence = intelligence;
        _server = server;
        _logger = logger;
        _reader = PipeReader.Create(transport, new StreamPipeReaderOptions(leaveOpen: true));
        _writer = PipeWriter.Create(transport, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>True once an interrupted exchange has desynced the stream; the connection must be discarded.</summary>
    public bool IsFaulted => _faulted;

    /// <summary>
    /// Opens a connection to <paramref name="server"/>: TLS first when configured, then SASL
    /// authentication when credentials are present, so the returned connection is ready to use.
    /// </summary>
    public static async ValueTask<HotRodConnection> ConnectAsync(
        HotRodClientOptions options, ServerAddress server, ITopologyCoordinator coordinator, CancellationToken ct)
    {
        ILogger logger = options.LoggerFactory.CreateLogger("HotRod.Client.Connection");
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(server.Host, server.Port, ct);
            Stream transport = await NegotiateAsync(tcp.GetStream(), server.Host, options.Tls, ct);

            var connection = new HotRodConnection(tcp, transport, coordinator, options.Intelligence, server, logger);
            // EXTERNAL takes its identity from the TLS client certificate, so it authenticates
            // even without a username; OAUTHBEARER authenticates off a token instead of a
            // username/password pair; the others need supplied credentials.
            if (options.Username is not null || options.Mechanism == SaslMechanism.External || options.Token is not null)
            {
                string user = options.Username ?? "(external)";
                try
                {
                    await connection.AuthenticateAsync(
                        CreateMechanism(options.Mechanism, options.Username, options.Password ?? string.Empty, options.Token), ct);
                    Log.AuthenticationSucceeded(logger, user, options.Mechanism);
                }
                catch (Exception ex)
                {
                    Log.AuthenticationFailed(logger, user, options.Mechanism, ex);
                    throw;
                }
            }
            Log.ConnectionOpened(logger, server.Host, server.Port);
            return connection;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Wraps the raw socket stream in TLS when requested; otherwise returns it unchanged.</summary>
    private static async ValueTask<Stream> NegotiateAsync(NetworkStream network, string host, TlsOptions? tls, CancellationToken ct)
    {
        if (tls is null)
            return network;

        var ssl = new SslStream(network, leaveInnerStreamOpen: false,
            userCertificateValidationCallback: tls.AllowUntrusted ? (_, _, _, _) => true : null);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = tls.TargetHost ?? host,
                ClientCertificates = tls.BuildClientCertificates(),
            }, ct);
        return ssl;
    }

    /// <summary>
    /// Returns the SASL mechanism names the server offers on this endpoint (AUTH_MECH_LIST),
    /// e.g. "PLAIN", "SCRAM-SHA-256". An empty list means the endpoint is unauthenticated.
    /// </summary>
    public ValueTask<IReadOnlyList<string>> ListAuthMechanismsAsync(CancellationToken ct) =>
        ExecuteAsync(string.Empty, Constants.AuthMechListRequest, flags: 0, DataFormat.None,
            writeBody: _ => { },
            readBody: async (_, reader, c) =>
            {
                int count = await HotRodCodec.ReadVIntAsync(reader, c);
                var mechs = new string[count];
                for (int i = 0; i < count; i++)
                    mechs[i] = await HotRodCodec.ReadStringAsync(reader, c);
                return (IReadOnlyList<string>)mechs;
            },
            ct);

    /// <summary>Round-trips a PING to check the connection is alive; throws if it is not.</summary>
    public async ValueTask PingAsync(CancellationToken ct) =>
        await ExecuteAsync(string.Empty, Constants.PingRequest, flags: 0, DataFormat.None,
            writeBody: _ => { },
            readBody: async (_, reader, c) =>
            {
                // PING body: key + value MediaType, server version (1 byte), then the supported
                // op codes (vInt count, each a 2-byte short). Consume it all to keep the stream in sync.
                await SkipMediaTypeAsync(reader, c);
                await SkipMediaTypeAsync(reader, c);
                await HotRodCodec.ReadByteAsync(reader, c);
                int opCount = await HotRodCodec.ReadVIntAsync(reader, c);
                for (int i = 0; i < opCount; i++)
                    await HotRodCodec.ReadUShortAsync(reader, c);
                return true;
            },
            ct);

    /// <summary>Consumes a MediaType (marker 0 none / 1 predefined id / 2 custom name, plus its parameters).</summary>
    private static async ValueTask SkipMediaTypeAsync(PipeReader reader, CancellationToken ct)
    {
        byte marker = await HotRodCodec.ReadByteAsync(reader, ct);
        if (marker == 0)
            return;

        if (marker == 1)
            await HotRodCodec.ReadVIntAsync(reader, ct);  // predefined id
        else
            await HotRodCodec.ReadArrayAsync(reader, ct); // custom name

        int paramCount = await HotRodCodec.ReadVIntAsync(reader, ct);
        for (int i = 0; i < paramCount; i++)
        {
            await HotRodCodec.ReadArrayAsync(reader, ct); // param name
            await HotRodCodec.ReadArrayAsync(reader, ct); // param value
        }
    }

    private static ISaslMechanism CreateMechanism(SaslMechanism mechanism, string? username, string password, string? token) =>
        mechanism switch
        {
            SaslMechanism.Plain => new PlainMechanism(username!, password),
            SaslMechanism.ScramSha256 => new ScramMechanism("SCRAM-SHA-256", username!, password, HashAlgorithmName.SHA256, 32),
            SaslMechanism.ScramSha512 => new ScramMechanism("SCRAM-SHA-512", username!, password, HashAlgorithmName.SHA512, 64),
            // EXTERNAL carries no password; a username, if given, is the requested authorization identity.
            SaslMechanism.External => new ExternalMechanism(username),
            SaslMechanism.OAuthBearer => new OAuthBearerMechanism(
                token ?? throw new HotRodException("OAUTHBEARER authentication requires a token; set HotRodClientOptions.Token")),
            _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "Unsupported SASL mechanism"),
        };

    /// <summary>
    /// Drives the SASL exchange: send the initial response, then process each server
    /// challenge until the exchange completes. The mechanism name is sent on every AUTH
    /// request; the server instantiates its SASL server on the first and reuses it.
    /// </summary>
    private async ValueTask AuthenticateAsync(ISaslMechanism mechanism, CancellationToken ct)
    {
        byte[] response = mechanism.InitialResponse();
        while (true)
        {
            (bool completed, byte[] challenge) = await AuthAsync(mechanism.Name, response, ct);
            if (completed)
            {
                mechanism.ValidateCompletion(challenge);
                return;
            }

            response = mechanism.Evaluate(challenge);

            // A mutual-auth mechanism may finish (server signature verified) before the
            // server flips its completion flag; once it has nothing more to send, stop.
            if (mechanism.IsComplete)
                return;
        }
    }

    /// <summary>Performs one AUTH round trip, returning the server's completion flag and challenge bytes.</summary>
    private ValueTask<(bool Completed, byte[] Challenge)> AuthAsync(string mechanismName, byte[] response, CancellationToken ct) =>
        ExecuteAsync(string.Empty, Constants.AuthRequest, flags: 0, DataFormat.None,
            writeBody: w =>
            {
                HotRodCodec.WriteArray(w, Encoding.UTF8.GetBytes(mechanismName));
                HotRodCodec.WriteArray(w, response);
            },
            readBody: async (_, reader, c) =>
            {
                bool completed = await HotRodCodec.ReadByteAsync(reader, c) != 0;
                byte[] challenge = await HotRodCodec.ReadArrayAsync(reader, c);
                return (completed, challenge);
            },
            ct);

    /// <summary>
    /// Writes a request header + body, flushes, reads the response header (applying any topology
    /// update), then hands the (status, reader) to <paramref name="readBody"/> to consume the body.
    /// The whole exchange is performed under a semaphore so the connection stays in sync.
    /// </summary>
    /// <remarks>
    /// Once bytes are on the wire, an interrupted exchange (cancellation, I/O failure or a
    /// protocol desync) leaves the stream at an unknown position, so the connection is marked
    /// faulted and refuses further use — the pool discards it. A clean server-status error is
    /// fully framed and leaves the stream in sync, so it does not fault. Cancellation while
    /// still waiting for the semaphore sends nothing and is always safe.
    /// </remarks>
    public async ValueTask<T> ExecuteAsync<T>(
        string cacheName,
        byte opcode,
        int flags,
        DataFormat dataFormat,
        Action<IBufferWriter<byte>> writeBody,
        Func<byte, PipeReader, CancellationToken, ValueTask<T>> readBody,
        CancellationToken ct)
    {
        ThrowIfFaulted();
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfFaulted();
            try
            {
                WriteHeader(cacheName, opcode, flags, dataFormat);
                writeBody(_writer);
                await _writer.FlushAsync(ct);

                byte status = await ReadResponseHeaderAsync(cacheName, ct);
                return await readBody(status, _reader, ct);
            }
            catch (Exception ex) when (LeavesStreamDesynced(ex))
            {
                _faulted = true;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Registers a client listener on this (now dedicated) connection and turns it into an event
    /// stream. Writes the addClientListener request, then loops reading messages: the first
    /// addClientListener ack completes <paramref name="ackReady"/> (or faults it on a server error),
    /// and every cache event after that is dispatched to <paramref name="onEvent"/>. The loop runs
    /// until <paramref name="ct"/> is cancelled (the listener is removed), at which point the
    /// connection is left mid-stream and marked faulted so the pool discards it on return.
    /// </summary>
    /// <remarks>
    /// The connection is held exclusively for the listener's lifetime, so the request lock is taken
    /// once and kept for the whole loop. A throwing event handler is contained — it must not desync
    /// the stream — but a protocol or I/O failure ends the loop and surfaces through the returned task.
    /// </remarks>
    public async Task ListenAsync(
        string cacheName,
        DataFormat dataFormat,
        Action<IBufferWriter<byte>> writeAddBody,
        Func<Protocol.RawClientEvent, ValueTask> onEvent,
        TaskCompletionSource ackReady,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            WriteHeader(cacheName, Constants.AddClientListenerRequest, flags: 0, dataFormat);
            writeAddBody(_writer);
            await _writer.FlushAsync(ct);

            while (true)
            {
                byte magic = await HotRodCodec.ReadByteAsync(_reader, ct);
                if (magic != Constants.ResponseMagic)
                    throw new HotRodException($"Unexpected response magic 0x{magic:X2} on a listener connection.");

                await HotRodCodec.ReadVLongAsync(_reader, ct); // message id (echoed / event correlation)
                byte opcode = await HotRodCodec.ReadByteAsync(_reader, ct);

                if (IsCacheEvent(opcode))
                {
                    Protocol.RawClientEvent ev = await HotRodCodec.ReadClientEventAsync(_reader, opcode, ct);
                    try { await onEvent(ev); }
                    catch { /* a throwing handler must not desync the stream; the next event is still read */ }
                }
                else if (opcode == Constants.AddClientListenerRequest + 1) // ADD_CLIENT_LISTENER_RESPONSE
                {
                    await ConsumeListenerAckAsync(cacheName, ackReady, ct);
                }
                else if (opcode == Constants.ErrorResponse)
                {
                    await FaultFromErrorAsync(cacheName, ackReady, ct);
                }
                else
                {
                    throw new HotRodException($"Unexpected opcode 0x{opcode:X2} on a listener connection.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The listener was removed: the read was abandoned mid-stream, so the connection is spent.
        }
        catch (Exception ex)
        {
            ackReady.TrySetException(ex); // pre-ack failures surface to AddListenerAsync; post-ack it is a no-op
            throw;
        }
        finally
        {
            _faulted = true;
            _lock.Release();
        }
    }

    /// <summary>
    /// Registers a counter listener on this (now dedicated) connection and turns it into an event
    /// stream. Writes the counterAddListener request (counter name, then listener id), then loops
    /// reading messages: the first ack completes <paramref name="ackReady"/> (or faults it on a server
    /// error), and every counter event after that is dispatched to <paramref name="onEvent"/>. The loop
    /// runs until <paramref name="ct"/> is cancelled (the listener is removed), at which point the
    /// connection is left mid-stream and marked faulted so the pool discards it on return.
    /// </summary>
    /// <remarks>
    /// Counter operations carry an empty cache name on the header (see <see cref="CounterManager"/>), so
    /// this shares the ack/error/topology plumbing with <see cref="ListenAsync"/> by passing an empty
    /// cache name through. Additive alongside <see cref="ListenAsync"/> rather than folded into it, since
    /// a counter event's body (counter name plus old/new value and state) does not fit the cache entry
    /// event shape read by <see cref="HotRodCodec.ReadClientEventAsync"/>.
    /// </remarks>
    public async Task ListenCounterAsync(
        string counterName,
        byte[] listenerId,
        Func<Protocol.RawCounterEvent, ValueTask> onEvent,
        TaskCompletionSource ackReady,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            WriteHeader(string.Empty, Constants.CounterAddListenerRequest, flags: 0, DataFormat.None);
            CounterCodec.WriteCounterName(_writer, counterName);
            HotRodCodec.WriteArray(_writer, listenerId);
            await _writer.FlushAsync(ct);

            while (true)
            {
                byte magic = await HotRodCodec.ReadByteAsync(_reader, ct);
                if (magic != Constants.ResponseMagic)
                    throw new HotRodException($"Unexpected response magic 0x{magic:X2} on a counter listener connection.");

                await HotRodCodec.ReadVLongAsync(_reader, ct); // message id (echoed / event correlation)
                byte opcode = await HotRodCodec.ReadByteAsync(_reader, ct);

                if (opcode == Constants.CounterEvent)
                {
                    Protocol.RawCounterEvent ev = await CounterCodec.ReadCounterEventAsync(_reader, ct);
                    try { await onEvent(ev); }
                    catch { /* a throwing handler must not desync the stream; the next event is still read */ }
                }
                else if (opcode == Constants.CounterAddListenerRequest + 1) // COUNTER_ADD_LISTENER_RESPONSE
                {
                    await ConsumeListenerAckAsync(string.Empty, ackReady, ct);
                }
                else if (opcode == Constants.ErrorResponse)
                {
                    await FaultFromErrorAsync(string.Empty, ackReady, ct);
                }
                else
                {
                    throw new HotRodException($"Unexpected opcode 0x{opcode:X2} on a counter listener connection.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The listener was removed: the read was abandoned mid-stream, so the connection is spent.
        }
        catch (Exception ex)
        {
            ackReady.TrySetException(ex); // pre-ack failures surface to AddListenerAsync; post-ack it is a no-op
            throw;
        }
        finally
        {
            _faulted = true;
            _lock.Release();
        }
    }

    /// <summary>Reads the addClientListener ack header and completes the registration, or faults it on a server error.</summary>
    private async ValueTask ConsumeListenerAckAsync(string cacheName, TaskCompletionSource ackReady, CancellationToken ct)
    {
        byte status = await HotRodCodec.ReadByteAsync(_reader, ct);
        byte topologyChanged = await HotRodCodec.ReadByteAsync(_reader, ct);
        if (topologyChanged != 0)
            await ReadTopologyUpdateAsync(cacheName, ct);

        if (ResponseStatus.IsError(status))
        {
            string message = await HotRodCodec.ReadStringAsync(_reader, ct);
            ackReady.TrySetException(HotRodServerException.Create(status, message));
            return;
        }
        ackReady.TrySetResult();
    }

    /// <summary>Reads an error message pushed on the listener connection and throws it, faulting a pending ack first.</summary>
    private async ValueTask FaultFromErrorAsync(string cacheName, TaskCompletionSource ackReady, CancellationToken ct)
    {
        byte status = await HotRodCodec.ReadByteAsync(_reader, ct);
        byte topologyChanged = await HotRodCodec.ReadByteAsync(_reader, ct);
        if (topologyChanged != 0)
            await ReadTopologyUpdateAsync(cacheName, ct);

        string message = await HotRodCodec.ReadStringAsync(_reader, ct);
        HotRodException error = HotRodServerException.Create(status, message);
        ackReady.TrySetException(error);
        throw error;
    }

    private static bool IsCacheEvent(byte opcode) =>
        opcode is Constants.CacheEntryCreatedEvent or Constants.CacheEntryModifiedEvent
                or Constants.CacheEntryRemovedEvent or Constants.CacheEntryExpiredEvent;

    private void ThrowIfFaulted()
    {
        if (_faulted)
            throw new HotRodException("The connection is faulted after an interrupted request and was discarded.");
    }

    /// <summary>
    /// True for failures that leave the read position unknown. A server-status
    /// <see cref="HotRodException"/> is a fully-read, well-framed response and is excluded;
    /// everything else (cancellation, I/O, protocol framing errors) desyncs the stream.
    /// </summary>
    private static bool LeavesStreamDesynced(Exception ex) =>
        ex is not HotRodException { Status: not null };

    private void WriteHeader(string cacheName, byte opcode, int flags, DataFormat dataFormat)
    {
        IBufferWriter<byte> w = _writer;
        HotRodCodec.WriteByte(w, Constants.RequestMagic);
        HotRodCodec.WriteVLong(w, ++_messageId);
        HotRodCodec.WriteByte(w, Constants.Version41);
        HotRodCodec.WriteByte(w, opcode);
        HotRodCodec.WriteArray(w, Encoding.UTF8.GetBytes(cacheName));
        HotRodCodec.WriteVInt(w, flags);
        HotRodCodec.WriteByte(w, (byte)_intelligence);
        HotRodCodec.WriteVInt(w, _coordinator.GetTopologyId(cacheName)); // last seen id; server resends only on change
        dataFormat.Key.WriteTo(w);   // key media type
        dataFormat.Value.WriteTo(w); // value media type
        HotRodCodec.WriteVInt(w, 0); // other params count (HotRod 4.0+)
    }

    /// <summary>Reads the response header and returns its status byte, throwing on protocol/server errors.</summary>
    private async ValueTask<byte> ReadResponseHeaderAsync(string cacheName, CancellationToken ct)
    {
        byte magic = await HotRodCodec.ReadByteAsync(_reader, ct);
        if (magic != Constants.ResponseMagic)
            throw new HotRodException($"Unexpected response magic 0x{magic:X2}");

        await HotRodCodec.ReadVLongAsync(_reader, ct); // message id (echoed)
        await HotRodCodec.ReadByteAsync(_reader, ct);  // response opcode
        byte status = await HotRodCodec.ReadByteAsync(_reader, ct);

        byte topologyChanged = await HotRodCodec.ReadByteAsync(_reader, ct);
        if (topologyChanged != 0)
            await ReadTopologyUpdateAsync(cacheName, ct);

        if (ResponseStatus.IsError(status))
        {
            string message = await HotRodCodec.ReadStringAsync(_reader, ct);
            throw HotRodServerException.Create(status, message);
        }

        return status;
    }

    /// <summary>
    /// Reads a topology update — id and node list — and reports it to the coordinator. Under
    /// <see cref="ClientIntelligence.HashAware"/> the consistent-hash segment→owner table follows the
    /// node list and is read too; under <see cref="ClientIntelligence.TopologyAware"/> the server
    /// sends no hash section, so none is read.
    /// </summary>
    private async ValueTask ReadTopologyUpdateAsync(string cacheName, CancellationToken ct)
    {
        int topologyId = await HotRodCodec.ReadVIntAsync(_reader, ct);
        int serverCount = await HotRodCodec.ReadVIntAsync(_reader, ct);

        var servers = new ServerAddress[serverCount];
        for (int i = 0; i < serverCount; i++)
        {
            string host = await HotRodCodec.ReadStringAsync(_reader, ct);
            int port = await HotRodCodec.ReadUShortAsync(_reader, ct);
            servers[i] = new ServerAddress(host, port);
        }

        ConsistentHash? hash = _intelligence == ClientIntelligence.HashAware
            ? await ReadHashAsync(servers, ct)
            : null;
        _coordinator.ReportTopology(cacheName, topologyId, servers, hash);
    }

    /// <summary>Reads the segment→owner table that follows the node list under Hash-Aware intelligence.</summary>
    private async ValueTask<ConsistentHash?> ReadHashAsync(ServerAddress[] servers, CancellationToken ct)
    {
        byte hashFunctionVersion = await HotRodCodec.ReadByteAsync(_reader, ct);
        int numSegments = await HotRodCodec.ReadVIntAsync(_reader, ct);
        if (hashFunctionVersion == 0)
            return null; // not distribution-aware; no per-segment owners follow

        var primaryBySegment = new ServerAddress?[numSegments];
        for (int segment = 0; segment < numSegments; segment++)
        {
            int ownerCount = await HotRodCodec.ReadByteAsync(_reader, ct);
            for (int owner = 0; owner < ownerCount; owner++)
            {
                int serverIndex = await HotRodCodec.ReadVIntAsync(_reader, ct);
                if (owner == 0) // the first owner is the primary
                    primaryBySegment[segment] = servers[serverIndex];
            }
        }

        return new ConsistentHash(primaryBySegment);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.CompleteAsync();
        await _reader.CompleteAsync();
        await _transport.DisposeAsync();
        _tcp.Dispose();
        _lock.Dispose();
        Log.ConnectionClosed(_logger, _server.Host, _server.Port);
    }
}
