namespace HotRod.Client.Protocol;

/// <summary>
/// HotRod 4.1 wire constants.
/// Reference: https://infinispan.org/docs/stable/titles/hotrod_protocol/hotrod_protocol.html
/// </summary>
internal static class Constants
{
    public const byte RequestMagic = 0xA0;
    public const byte ResponseMagic = 0xA1;

    /// <summary>
    /// Version byte = major * 10 + minor, encoded as a single byte. 4.1 => 41 => 0x29.
    /// </summary>
    public const byte Version41 = 0x29;

    // Client intelligence. With Basic the server never pushes topology updates back,
    // so the client needs no consistent-hash routing or topology bookkeeping.
    public const byte IntelligenceBasic = 0x01;
    public const byte IntelligenceTopologyAware = 0x02;
    public const byte IntelligenceHashAware = 0x03;

    // Key/value MediaType descriptor (HotRod 2.8+). 0x00 = "no type set":
    // the server falls back to the cache's configured encoding.
    public const byte MediaTypeNone = 0x00;

    // Request opcodes (response opcode is always request + 1).
    public const byte PutRequest = 0x01;
    public const byte GetRequest = 0x03;
    public const byte PutIfAbsentRequest = 0x05;
    public const byte ReplaceRequest = 0x07;
    public const byte ReplaceIfUnmodifiedRequest = 0x09;
    public const byte RemoveRequest = 0x0B;
    public const byte RemoveIfUnmodifiedRequest = 0x0D;
    public const byte ContainsKeyRequest = 0x0F;
    public const byte GetWithVersionRequest = 0x11;
    public const byte ClearRequest = 0x13;
    public const byte StatsRequest = 0x15;
    public const byte PingRequest = 0x17;
    public const byte GetWithMetadataRequest = 0x1B;
    public const byte BulkGetKeysRequest = 0x1D;
    public const byte SizeRequest = 0x29;
    public const byte PutAllRequest = 0x2D;
    public const byte GetAllRequest = 0x2F;
    public const byte IterationStartRequest = 0x31;
    public const byte IterationNextRequest = 0x33;
    public const byte IterationEndRequest = 0x35;

    // Remote query (Ickle). The request body is a single protobuf-encoded QueryRequest and the response
    // body a single protobuf-encoded QueryResponse. Cache-scoped like a normal cache operation, so the
    // cache name travels on the header and the response opcode is request + 1 (0x20).
    public const byte QueryRequest = 0x1F;

    // Transaction opcodes. A transactional cache buffers a transaction's writes on the client and drives
    // the two-phase protocol at commit: PREPARE_TX stages the whole modification set under an XID and
    // validates it, then COMMIT_TX or ROLLBACK_TX completes it and FORGET_TX discards the server-side
    // record. PREPARE_TX is cache-scoped (its body is a modification list for one cache); COMMIT_TX,
    // ROLLBACK_TX, and FORGET_TX are global to the XID and carry an empty cache name. The response opcode
    // is request + 1. These match Infinispan's HotRodConstants (PREPARE_TX_2 = 0x7D, COMMIT = 0x3D,
    // ROLLBACK = 0x3F, FORGET_TX = 0x79); PREPARE_TX_2 is the current prepare that streams the whole
    // modification set, superseding the original PREPARE_TX opcode.
    public const byte PrepareTransactionRequest = 0x7D;  // stage + validate a transaction's modifications
    public const byte CommitTransactionRequest = 0x3D;   // commit a prepared transaction by XID
    public const byte RollbackTransactionRequest = 0x3F; // roll a prepared transaction back by XID
    public const byte ForgetTransactionRequest = 0x79;   // discard the server-side record of an XID

    // Clustered-counter opcodes. Counter operations are not cache-scoped: they carry an empty cache
    // name in the header and the counter's name as the first field of the body. The response opcode is
    // request + 1, as for cache operations.
    public const byte CounterCreateRequest = 0x4B;           // define a counter from a configuration
    public const byte CounterGetConfigurationRequest = 0x4D; // read a counter's configuration
    public const byte CounterIsDefinedRequest = 0x4F;        // test whether a counter exists
    public const byte CounterAddAndGetRequest = 0x52;        // add a delta and return the new value
    public const byte CounterResetRequest = 0x54;            // reset a counter to its initial value
    public const byte CounterGetRequest = 0x56;              // read a counter's current value
    public const byte CounterCasRequest = 0x58;              // compare-and-swap (strong counters)
    public const byte CounterRemoveRequest = 0x5E;           // delete a counter cluster-wide
    public const byte CounterGetNamesRequest = 0x64;         // list every defined counter's name

    // Server-side task execution. A single opcode carries both ad-hoc script execution
    // (RemoteCache.execute) and cache administration: admin operations are server tasks named
    // "@@cache@create", "@@cache@remove", etc., invoked through EXEC with string parameters. The
    // response opcode is request + 1, as for cache operations.
    public const byte ExecRequest = 0x2B;

    // Authentication opcodes. AUTH_MECH_LIST asks the server which SASL mechanisms
    // its endpoint offers; AUTH carries the SASL exchange itself.
    public const byte AuthMechListRequest = 0x21;
    public const byte AuthRequest = 0x23;

    // Client-listener opcodes. Adding a listener registers it (by a client-generated id) and turns
    // the connection into an event stream; removing it tears the registration down again.
    public const byte AddClientListenerRequest = 0x25;
    public const byte RemoveClientListenerRequest = 0x27;

    // Unsolicited messages the server pushes on a listener connection. Each carries its own opcode in
    // the header (in place of a request's echoed opcode) so the read loop can tell events from acks.
    public const byte ErrorResponse = 0x50;
    public const byte CacheEntryCreatedEvent = 0x60;
    public const byte CacheEntryModifiedEvent = 0x61;
    public const byte CacheEntryRemovedEvent = 0x62;
    public const byte CacheEntryExpiredEvent = 0x63;
    public const byte CounterEvent = 0x66;

    // Entry-metadata flags in a metadata/force-return response: when set, that dimension never
    // expires and its (timestamp, duration) pair is omitted from the body.
    public const byte InfiniteLifespan = 0x01;
    public const byte InfiniteMaxIdle = 0x02;

    // Expiration time-unit nibbles (the byte packs lifespan in the high nibble, maxIdle in the low).
    public const byte TimeUnitSeconds = 0x00;
    public const byte TimeUnitMilliseconds = 0x01;
    public const byte TimeUnitDefault = 0x07;  // use the cache's configured expiration; no vLong follows
    public const byte TimeUnitInfinite = 0x08; // never expire; no vLong follows

    /// <summary>0x77 = DEFAULT for both lifespan and maxIdle, with no following vLongs.</summary>
    public const byte TimeUnitDefaultBoth = (TimeUnitDefault << 4) | TimeUnitDefault;

    // Response status codes.
    public const byte StatusSuccess = 0x00;                  // operation completed
    public const byte StatusNotExecuted = 0x01;              // conditional op did not run
    public const byte StatusKeyDoesNotExist = 0x02;          // key absent
    public const byte StatusSuccessWithPrevious = 0x03;      // success, previous value in the body
    public const byte StatusNotExecutedWithPrevious = 0x04;  // not executed, current value in the body
    // Object-storage caches return a parallel set of success codes.
    public const byte StatusSuccessObj = 0x06;
    public const byte StatusSuccessWithPreviousObj = 0x07;
    public const byte StatusNotExecutedWithPreviousObj = 0x08;

    // Statuses >= 0x80 are server-side errors; the body carries a message string.
    public const byte StatusErrorThreshold = 0x80;
    public const byte StatusInvalidMagicOrMessageId = 0x81;
    public const byte StatusUnknownCommand = 0x82;
    public const byte StatusUnknownVersion = 0x83;
    public const byte StatusRequestParsingError = 0x84;
    public const byte StatusServerError = 0x85;
    public const byte StatusCommandTimeout = 0x86;
    public const byte StatusNodeSuspected = 0x87;
    public const byte StatusIllegalLifecycleState = 0x88;
}
