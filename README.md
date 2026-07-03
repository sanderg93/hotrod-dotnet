# HotRod.Client (.NET)

A dependency-free .NET client for [Infinispan](https://infinispan.org) speaking the **HotRod 4.1**
binary wire protocol.

[![Build](https://img.shields.io/github/actions/workflow/status/sanderg93/hotrod-dotnet/ci.yml?branch=main&label=build)](https://github.com/sanderg93/hotrod-dotnet/actions)
[![NuGet](https://img.shields.io/nuget/v/HotRod.Client.svg)](https://www.nuget.org/packages/HotRod.Client)
[![License](https://img.shields.io/github/license/sanderg93/hotrod-dotnet.svg)](LICENSE)

## Getting started

```bash
dotnet add package HotRod.Client
```

```csharp
using HotRod.Client;

// Connects to a single seed server; the client then discovers the rest of the cluster.
await using HotRodClient client = await HotRodClient.ConnectAsync("127.0.0.1", 11222);

RemoteCache cache = client.GetCache("default");

await cache.PutAsync("Stad", "Amsterdam");
string? city = await cache.GetAsync("Stad"); // "Amsterdam"
```

See [Using the client](#using-the-client) below for pool sizing, authentication (SASL PLAIN/SCRAM)
and TLS options, and the [Operations](#operations) section for the full API — conditional and
versioned writes, bulk operations, iteration, client listeners, and the near cache.

---

# HotRod.Client (.NET) — minimal HotRod 4.1 client

A small, dependency-free .NET client for [Infinispan](https://infinispan.org) over the
**HotRod 4.1** binary wire protocol. It implements the core key/value operations against
a single server, verified end-to-end against Infinispan 16.0.

This exists because there is no maintained .NET HotRod client that speaks protocol 4.x
(the official `infinispan/dotnet-client` is effectively dead and `Infinispan.Hotrod.Core`
stops at protocol 3.0). For most caching use cases the RESP endpoint + StackExchange.Redis
is the pragmatic alternative; this client is for when you specifically need HotRod.

## Scope

Implemented: the basic `Put`, `Get`, `Remove`, `ContainsKey`, `Clear`, `Size`; the conditional
`PutIfAbsent` and `Replace`; the versioned `GetWithVersion`, `ReplaceWithVersion`,
`RemoveWithVersion` (optimistic concurrency); the bulk `PutAll`, `GetAll`; and per-entry
**expiration** (lifespan / max-idle) on every write (string and `byte[]` overloads). Operations are
fully asynchronous over a **pool of connections per cluster node** with **Hash-Distribution-Aware
client intelligence** (single-hop routing to a key's owner), optionally encrypted with **TLS** and
authenticated with **SASL PLAIN, SCRAM-SHA-256 or SCRAM-SHA-512**. Every server status code is mapped
to a typed exception. Strings are exchanged as **ProtoStream** by default, so they interoperate with
the Java client and the console; a **raw** encoding is available per cache.

Deliberately not implemented (yet):

| Not implemented | Consequence |
|-----------------|-------------|
| SASL mechanisms beyond PLAIN / SCRAM (DIGEST, GSSAPI, OAUTHBEARER) | Cover the common username/password realms; Kerberos and token auth are out |
| Client certificate (mutual TLS) auth | TLS authenticates the server only; clients still authenticate via SASL |
| Pipelining | Each connection handles one request at a time; concurrency comes from the pool |
| Transactions, query | Key/value plus clustered counters, client listeners and an invalidated near cache; the rest is out |
| ProtoStream for custom types | Strings and primitive numbers are ProtoStream-wrapped; custom objects would need a registered `.proto` schema |

The client learns every cluster node, keeps a pool per node, and routes each key straight to its
primary owner using the same consistent hash the server does — so reads and writes are single-hop.

## Project layout

```
src/HotRod.Client/
  Protocol/Constants.cs      wire constants (magic, opcodes, version, time units, status codes, flags)
  Protocol/ResponseStatus.cs reads a status byte: success / not-executed / has-previous / error
  Protocol/EntryMetadata.cs  per-entry metadata (lifespan/maxIdle/created/lastUsed/version) in a response
  Protocol/IterationBatch.cs one iterationNext batch: finished-segments bitset + this batch's entries
  Protocol/HotRodCodec.cs    byte-level primitives: varints, length-prefixed arrays/strings, longs
  Protocol/ISaslMechanism.cs the SASL client contract (initial response, challenge loop)
  Protocol/SaslPlain.cs      builds the SASL PLAIN response (RFC 4616)
  Protocol/PlainMechanism.cs PLAIN as an ISaslMechanism
  Protocol/ScramMechanism.cs SCRAM-SHA-256/512 state machine (RFC 5802 / 7677)
  Protocol/MurmurHash3.cs    Infinispan's MurmurHash3 variant (key → hash), ported exactly
  Protocol/MediaType.cs      MediaType + DataFormat: how the request header declares the encoding
  Protocol/CacheMarshaller.cs Raw (UTF-8) and ProtoStream (WrappedMessage) value marshalling
  Protocol/RawClientEvent.cs a decoded server event as it comes off the wire (before mapping)
  CacheEncoding.cs           public enum: Raw or ProtoStream, per cache
  CacheFlag.cs               public [Flags] enum: per-operation flags (FORCE_RETURN_VALUE, SKIP_*, ...)
  ClientIntelligence.cs      public enum: Basic / TopologyAware / HashAware cluster awareness
  Expiration.cs              public lifespan / max-idle for a write; encodes the time-units byte
  Versioned.cs               public value + its server version (for optimistic concurrency)
  MetadataValue.cs           public value + server metadata (version, timestamps, expiration)
  ClientEventType.cs         public enum: which change an event reports (Created/Modified/Removed/Expired)
  ClientCacheEntryEvent.cs   public event handed to a listener: type, raw key, version, retried-flag
  ClientListenerOptions.cs   public listener tuning: interest bitmask + include-current-state
  ClientListener.cs          public listener handle: owns the dedicated connection; dispose to unsubscribe
  NearCache.cs               thread-safe local store: LRU size-cap, placeholder guard, hit/miss/size stats
  NearCacheOptions.cs        public near-cache config (max entries) + the stats snapshot record
  SaslMechanism.cs           public enum selecting the mechanism
  TlsOptions.cs              public TLS settings (target host, allow-untrusted)
  HotRodClientOptions.cs     public config: endpoint, security, pool sizing (with defaults)
  HotRodClient.cs            public facade over the cluster; one operation = one routed exchange
  Cluster.cs                 a pool per node; parses topology, routes by key owner / round-robin
  ConsistentHash.cs          key → segment → primary-owner node (the routing table)
  Topology.cs                ServerAddress + the connection↔cluster topology contract
  ConnectionPool.cs          bounded pool for one node: borrow/return, reuse, retire, discard faulted
  HotRodConnection.cs        one connection (Pipelines I/O, TLS, SASL, topology parse, fault tracking)
  RemoteCache.cs             the operations (basic/conditional/versioned/bulk) as request/response bodies
  HotRodException.cs         exception hierarchy: client errors, server-status errors, timeouts
src/HotRod.Demo/             console smoke test exercising every operation
test/infinispan-noauth.xml   server config with endpoint auth disabled (local testing)
test/infinispan-auth.xml     server config with a SASL PLAIN realm (clear-text users)
test/infinispan-scram.xml    server config with SCRAM/DIGEST mechs (encrypted realm)
test/infinispan-tls.xml      server config adding a TLS server identity to the SCRAM realm
test/infinispan-cluster.xml  two-node clustered config (distributed cache) for topology testing
```

The codec knows the *alphabet* of the protocol (how to read/write a number, a string, a byte
array). `HotRodConnection` knows the *grammar* (header layout, the request→response exchange);
`Cluster` + `ConnectionPool` manage *which nodes exist and how many* connections to each, and hand
them out. `RemoteCache` knows only the *meaning* of each operation.

## Quickstart

Start a local Infinispan with authentication disabled (HotRod 4.1 is offered by 16.x):

```bash
docker run -d --name ispn-mvp -p 11222:11222 \
  -v "$PWD/test/infinispan-noauth.xml:/user-config/infinispan.xml:ro" \
  infinispan/server:latest -c /user-config/infinispan.xml
```

Run the demo against the named `default` cache:

```bash
dotnet run --project src/HotRod.Demo -c Release 127.0.0.1 11222 default
```

Expected output:

```
Stored Stad=Amsterdam, Land=Nederland
Get Stad         -> Amsterdam
Get Onbekend     -> (null)
ContainsKey Land -> True
Size             -> 2
Remove Stad      -> True
ContainsKey Stad -> False
After clear size -> 0
Cluster servers  -> 127.0.0.1:11222
```

> Use a **named** cache (e.g. `default`). An empty cache name maps to HotRod's "default cache",
> which the server reports as *"Default cache requested but not configured"* unless explicitly set up.

## Using the client

The API is fully asynchronous. `HotRodClient` is a pooled, thread-safe entry point — create one,
share it across the application, and dispose it on shutdown:

```csharp
await using HotRodClient client = await HotRodClient.ConnectAsync(new HotRodClientOptions
{
    Host = "infinispan",
    Username = "admin",
    Password = "secret",
    Mechanism = SaslMechanism.ScramSha256,
    Tls = new TlsOptions(),

    // pool sizing (defaults shown)
    MinConnections = 1,                          // opened eagerly; validates credentials at startup
    MaxConnections = 10,                         // hard cap; further callers wait
    AcquireTimeout = TimeSpan.FromSeconds(30),   // how long to wait for a free connection
    MaxIdleTime = TimeSpan.FromMinutes(2),       // idle connections retired after this
    MaxLifetime = TimeSpan.FromMinutes(30),      // connections rotated after this
    MaintenanceInterval = TimeSpan.FromSeconds(30), // background sweep of idle/expired connections
    ValidateOnBorrow = false,                    // PING a reused connection before handing it out
});

RemoteCache cache = client.GetCache("default");
await cache.PutAsync("Stad", "Amsterdam");
string? city = await cache.GetAsync("Stad");
```

A shorter overload covers the common case with default pool sizing:

```csharp
await using HotRodClient client = await HotRodClient.ConnectAsync(
    "infinispan", 11222, "admin", "secret", SaslMechanism.ScramSha256, new TlsOptions());
```

Each operation borrows a connection from a node's pool, runs on it, and returns it, so calls can run
concurrently up to `MaxConnections` per node. Every operation accepts a `CancellationToken`. Each
connection still handles one request at a time; if a request is interrupted mid-wire (cancellation,
I/O error, protocol desync) that connection is faulted and the pool discards it, opening a fresh one.

A pool keeps idle connections fresh in two ways. A **background sweep** (`MaintenanceInterval`) runs
periodically and closes connections that have passed `MaxIdleTime` or `MaxLifetime`, so the pool does
not hold stale connections between bursts of traffic. With **`ValidateOnBorrow`** enabled, a reused
connection is first checked with a lightweight `PING`; if the peer closed it while idle the PING fails
and the pool retires it and opens a fresh one, so a dead connection is never handed to a caller — at
the cost of one round-trip per borrow. Both can be turned off (`MaintenanceInterval = TimeSpan.Zero`,
`ValidateOnBorrow = false`) to rely solely on eviction at borrow time.

## Operations

Beyond the basic `Get`/`Put`/`Remove`/`ContainsKey`/`Clear`/`Size`, the cache exposes the conditional,
versioned and bulk operations, each in a `string` and a `byte[]` overload.

**Expiration** — every write takes an optional `Expiration`; omit it to use the cache's configured
default. A `Lifespan` caps absolute age; a `MaxIdle` caps time since last access:

```csharp
await cache.PutAsync("session", token, new Expiration(lifespan: TimeSpan.FromMinutes(30)));
await cache.PutAsync("draft", body, new Expiration(maxIdle: TimeSpan.FromMinutes(5)));
```

**Conditional** — write only when the key's presence matches, so check-then-set is atomic on the
server rather than racy across two calls:

```csharp
bool stored   = await cache.PutIfAbsentAsync("lock", owner);  // only if the key is absent
bool replaced = await cache.ReplaceAsync("config", updated);  // only if the key exists
```

**Versioned (optimistic concurrency)** — read a value together with its server version, then write
only if that version still holds. A concurrent change bumps the version, so the conditional write
returns `false` instead of clobbering the other writer — no locks held:

```csharp
Versioned<string>? current = await cache.GetWithVersionAsync("counter");
if (current is { } v)
{
    bool ok = await cache.ReplaceWithVersionAsync("counter", Next(v.Value), v.Version);
    // ok == false → someone else wrote first; re-read and retry
}
bool removed = await cache.RemoveWithVersionAsync("counter", v.Version);
```

**Previous value** — the `…AndReturnPrevious` variants set the `FORCE_RETURN_VALUE` flag so a write
returns the value it replaced, removed, or was blocked by, in one round-trip instead of a separate read:

```csharp
string? replaced = await cache.PutAndReturnPreviousAsync("k", "v2");        // value k held before, or null
string? removed  = await cache.RemoveAndReturnPreviousAsync("k");           // value removed, or null if absent
string? blocking = await cache.PutIfAbsentAndReturnPreviousAsync("k", "v"); // existing value that blocked it, or null if stored
```

Only the "with previous" responses carry a body — entry metadata followed by the prior value — so an
operation that did not apply (a replace on an absent key) simply returns null. Set other flags
yourself with `CacheFlag` where an operation exposes them.

**Metadata** — `GetWithMetadataAsync` returns the value together with its server metadata: version,
created/last-used timestamps, and remaining lifespan/max-idle (a null timestamp or duration means that
dimension never expires). `StatsAsync` returns the cache's server statistics as a name→value map:

```csharp
MetadataValue<string>? m = await cache.GetWithMetadataAsync("k");
if (m is not null)
    Console.WriteLine($"{m.Value} v{m.Version}, lifespan {m.Lifespan?.ToString() ?? "infinite"}");

IReadOnlyDictionary<string, string> stats = await cache.StatsAsync();
```

**Bulk** — move many entries in one round-trip instead of one call per key; the server fans the
request out across the owning nodes:

```csharp
await cache.PutAllAsync(new Dictionary<byte[], byte[]> { [k1] = v1, [k2] = v2 });
IReadOnlyList<KeyValuePair<byte[], byte[]>> found = await cache.GetAllAsync(new[] { k1, k2, k3 });
// absent keys are simply omitted from the result
```

Bulk operations are keyless from the client's point of view, so they go out round-robin and the
server forwards each key to its owner.

**Iteration** — `IterateAsync` streams every entry as an `IAsyncEnumerable` in server-driven batches,
without loading the whole cache into memory:

```csharp
await foreach (KeyValuePair<string, string> entry in cache.IterateStringsAsync(batchSize: 100))
    Console.WriteLine($"{entry.Key} = {entry.Value}");
```

The server-side cursor is bound to one connection, so an iteration holds a single leased connection
from `iterationStart` through `iterationNext` to `iterationEnd`. Disposing the enumerator early (a
`break`) still ends the cursor cleanly and returns the connection to its pool.

**All keys** — `GetKeysAsync` fetches every key in the cache in one request (the keyless call goes out
round-robin and the server spans each owner); `GetKeyStringsAsync` decodes them through the cache's
encoding. Unlike `IterateAsync` it returns keys only, and materializes them all, so reach for iteration
when the key set may be large:

```csharp
IReadOnlyList<byte[]> raw  = await cache.GetKeysAsync();
IReadOnlyList<string> keys = await cache.GetKeyStringsAsync();
```

## Client listeners (events)

`AddListenerAsync` registers a listener and returns a `ClientListener` handle; the server then pushes
an event to your callback whenever a matching entry is created, modified, removed or expired. Dispose
the handle to unsubscribe:

```csharp
await using ClientListener listener = await cache.AddListenerAsync(async e =>
{
    string key = cache.DecodeKey(e.Key);        // event keys are raw storage bytes — decode them
    Console.WriteLine($"{e.Type} {key} v{e.Version}");
});

// ... events arrive on the callback while the listener is alive ...
// leaving the `await using` scope removes the listener on the server and frees its connection
```

Each event is a `ClientCacheEntryEvent`: the `Type` (`Created` / `Modified` / `Removed` / `Expired`),
the `Key`, a `Version` (the entry's new version for created/modified, `null` for removed/expired), and
`CommandRetried` (true when the server re-sent the event after a topology change, so an exactly-once
handler can deduplicate). The `Key` is the raw key in the cache's storage format — under the default
ProtoStream encoding a string key is a `WrappedMessage`, not plain UTF-8 — so decode it with
`cache.DecodeKey(key)` (the same encoding the cache reads and writes with), never `Encoding.UTF8`.

Tune the registration with `ClientListenerOptions`:

```csharp
await using ClientListener listener = await cache.AddListenerAsync(OnEvent, new ClientListenerOptions
{
    Interests = ClientListenerInterest.Modified | ClientListenerInterest.Removed, // subset; default is All
    IncludeCurrentState = true,   // first replay a Created event per existing entry, then stream live
});
```

`Interests` is the bitmask the server filters on, so it only pushes the event types you ask for.
`IncludeCurrentState` replays the cache's current contents as `Created` events before live changes
begin — a listener that must build a complete local view starts from a snapshot instead of missing
whatever was already there.

Unlike every other operation, a listener does not fit the borrow-run-return model: the server pushes
events unsolicited, at any time. So a listener **holds a dedicated leased connection** for its whole
lifetime with a background loop reading the event stream, rather than sharing the pool. `AddListenerAsync`
waits for the server's registration ack before returning, so a failed registration throws instead of
silently dropping events. `DisposeAsync` removes the listener clusterwide (best effort), stops the loop,
and hands the now mid-stream connection back to be discarded; it is idempotent.

> Custom filter/converter factories and continuous queries are not implemented — a listener reports the
> raw key of each change (`useRawData`), not a server-side-transformed payload. Counter events are out of
> scope. A near cache (below) builds on this event stream.

## Near cache

A near cache keeps a local copy of the entries a cache has read, so a repeated read of the same key is
served from memory instead of a server round-trip. It runs in **invalidated** mode: under the hood it
registers a client listener (the mechanism above) that drops the local copy the instant the entry is
modified, removed or expired on the server, and the cache's own writes invalidate their key too — so a
local hit never returns a value the server has already changed. Enable it per cache via the async
`GetCacheAsync` overload:

```csharp
await using RemoteCache cache = await client.GetCacheAsync("orders",
    nearCache: new NearCacheOptions { MaxEntries = 1000 });   // 0 = unbounded; >0 evicts least-recently-used

await cache.GetAsync("k");                       // miss → fetched from the server, copied locally
await cache.GetAsync("k");                        // hit → served locally, no round-trip

NearCacheStatistics s = cache.NearCacheStats!.Value;
Console.WriteLine($"hits {s.Hits}, misses {s.Misses}, size {s.Size}");
```

The listener is registered **before** `GetCacheAsync` returns, so no read is served locally until
invalidation can arrive. Reads (`GetAsync`, `GetWithVersionAsync`, `GetWithMetadataAsync`, and their
string overloads) populate the store; every write, remove and clear on the handle invalidates eagerly,
and server-side changes invalidate through the listener. `MaxEntries` bounds the store with a simple
LRU size-cap. A near-cache handle **owns its listener**, so dispose it (`await using`) to unsubscribe;
disposal is idempotent, and `NearCacheStats` is null on a cache without a near cache.

A miss reserves the key with a placeholder before fetching and only caches the fetched value if that
placeholder is still there — a concurrent write or invalidation that clears it makes the value suspect,
so it is dropped rather than cached stale. `GetCache` (the synchronous overload) is unchanged and has no
near cache. Eager mode and the bloom-filter bounded variant are out of scope.

> Invalidation is driven by the same client-listener stream, so a near cache inherits its guarantees: it
> is eventually consistent, not transactional. A read in the small window between a server-side change
> and its event can still be served locally; the event then clears it for the next read.

## Error handling

Every response carries a one-byte status. Success and "expected" non-success (a missing key, a
conditional write that did not apply) are turned into ordinary return values — `GetAsync` returns
`null`, `ReplaceAsync` returns `false` — never an exception. Only a genuine server error status
(`≥ 0x80`) or a protocol/transport failure throws:

```
HotRodException                 base: protocol errors (bad magic, truncated stream), pool timeouts
└─ HotRodServerException        a server-reported error status (carries the Status byte + message)
   └─ HotRodTimeoutException    the server timed out executing the operation (status 0x86)
```

`HotRodServerException.Status` exposes the raw status byte, and the message includes a decoded
description (e.g. *"request parsing error"*, *"node suspected"*). The dedicated
`HotRodTimeoutException` lets a caller single out the one server error that is typically worth a retry:

```csharp
try
{
    await cache.PutAsync("k", "v");
}
catch (HotRodTimeoutException)
{
    // server-side command timeout — safe to retry
}
catch (HotRodServerException ex)
{
    log.Warn("HotRod server error 0x{Status:X2}: {Message}", ex.Status, ex.Message);
}
```

## Encoding (interop with the Java client and console)

A cache stores entries in a **storage MediaType**. Each request header declares the MediaType of the
bytes the client sends (a marker byte: `0` none, `1` predefined id, `2` custom name); the server
transcodes between that and the cache's storage encoding. So you pick a client-side encoding once and
it works against caches regardless of their storage format — no per-cache discovery needed.

`GetCache` uses the client's `DefaultEncoding` (ProtoStream) unless overridden:

```csharp
RemoteCache orders = client.GetCache("orders");                    // ProtoStream (default)
RemoteCache blobs  = client.GetCache("blobs", CacheEncoding.Raw);  // raw bytes
```

| Encoding | String on the wire | Declares | Use for |
|----------|--------------------|----------|---------|
| `ProtoStream` (default) | a ProtoStream `WrappedMessage` (`wrappedString`, field 9) | `application/x-protostream` | interop with the Java client; readable (as JSON) in the console; queryable |
| `Raw` | UTF-8, byte arrays as-is | nothing (none) | binary blobs or a private byte store; fastest, but shown as raw bytes |

With ProtoStream a value written by this client reads back through REST as
`{"_type":"string","_value":"Amsterdam"}`, and a value written by the Java client or REST reads back
here as the plain string — verified both directions. Strings and primitive numbers (`int`, `long`,
`float`, `double`, `bool`) are wrapped through the matching `WrappedMessage` fields; custom objects
would still need a registered `.proto` schema.

## Cluster topology

The client connects to one **seed** server but discovers the whole cluster. By default it sends
*Hash-Aware* intelligence (`0x03`) in every request header along with the last topology id it has
seen. When that id is stale, the server prepends a **topology update** to the response — the new id,
every node's host/port, and the consistent-hash segment table — which the client parses out of the
response header (before the operation body):

```
topologyChanged (1 byte) → if set:
  topologyId   (vInt)
  serverCount  (vInt),  then [host (string), port (u16)] * serverCount
  hashVersion  (1 byte)
  segmentCount (vInt),  then [ownerCount (1 byte), [serverIndex (vInt)] * ownerCount] * segmentCount
```

The client keeps a **connection pool per node** and disposes a departed node's pool. The discovered
nodes are visible via `HotRodClient.Servers`.

### Intelligence level

The level is configurable via `HotRodClientOptions.Intelligence`, though `HashAware` is almost always
what you want and is the default. Lower it only for a deliberate reason — the levels are supersets, so
a lower one never does anything *more* correctly, only less:

| `ClientIntelligence` | Byte | Server pushes | Routing |
|----------------------|------|---------------|---------|
| `Basic`         | `0x01` | nothing — the client stays on its seed node | none |
| `TopologyAware` | `0x02` | the node list on change | round-robin across nodes |
| `HashAware` (default) | `0x03` | the node list **and** the segment table | single-hop to each key's owner |

The client reads exactly what the chosen level makes the server send: under `TopologyAware` it parses
the node list but no segment table, under `Basic` no update arrives at all. Either way every operation
still works — a non-routed call simply lands on some node, which forwards it to the key's owner.

### Single-hop routing

The segment table maps each of the (e.g. 256) segments to its owner nodes — the first is the
**primary owner**. To route a key the client computes, exactly as the server does:

```
hash    = MurmurHash3(keyBytes)              // Infinispan's own variant, ported byte-for-byte
segment = (hash & 0x7FFFFFFF) / segmentSize  // segmentSize = ceil(2^31 / segmentCount)
node    = primaryOwner(segment)              // from the table above
```

and borrows from that node's pool — so a `Get`/`Put` lands on the owning node directly instead of
being forwarded. `HotRodClient.GetPrimaryOwner(cache, key)` exposes this decision. Keyless operations
(`Size`, `Clear`) and any key whose owner is not yet known fall back to round-robin. Getting the hash
right matters only for *which* node is contacted: a wrong guess still works because the contacted node
forwards — which is also why the implementation was validated against the server's own
`?action=distribution` view (40/40 keys agreed).

> Only **clustered** caches (distributed/replicated) advertise a topology; a `local-cache` reports
> none, so the client just keeps the seed and round-robins. The update is keyed per cache name, so the
> server only resends when that cache's topology actually changes. Single-hop ownership matches only
> when client and server hash the *same* key bytes — configure the cache with a raw encoding
> (`application/octet-stream`) when you rely on it from this raw-bytes client.

To see it against a real cluster, bring up the bundled two-node config and run the demo **inside the
same Docker network** (the advertised addresses are the nodes' network IPs):

```bash
docker network create ispn-net
for n in node1 node2; do
  docker run -d --name $n --hostname $n --network ispn-net \
    -v "$PWD/test:/user-config:ro" \
    infinispan/server:latest -c /user-config/infinispan-cluster.xml
done

dotnet publish src/HotRod.Demo -c Release -o out
docker run --rm --network ispn-net -v "$PWD/out:/app:ro" \
  mcr.microsoft.com/dotnet/runtime:10.0 \
  dotnet /app/HotRod.Demo.dll node1 11222 default
# → Cluster servers  -> 172.x.0.2:11222, 172.x.0.3:11222
```

## The wire format (as implemented)

HotRod is length-prefixed: every variable-length field is preceded by its length, and the
server knows a message is complete by **counting**, not by any terminator byte.

### Request header (HotRod 4.1)

| Field | Encoding | Value used here |
|-------|----------|-----------------|
| Magic | 1 byte | `0xA0` |
| Message ID | vLong | incrementing |
| Version | 1 byte | `0x29` (= 41 = 4.1) |
| Opcode | 1 byte | per operation |
| Cache name | vInt length + UTF-8 | e.g. `default` |
| Flags | vInt | `0` |
| Client intelligence | 1 byte | `0x03` (Hash-Aware) by default; see [Intelligence level](#intelligence-level) |
| Topology ID | vInt | `0` |
| Key MediaType | 1 byte | `0x00` (none) |
| Value MediaType | 1 byte | `0x00` (none) |
| **Other params count** | **vInt** | **`0`** ← added in protocol 4.0 |

### PUT body

`key` (vInt length + bytes) · `time-units` (1 byte) · `lifespan`/`maxIdle` (vLong, *only if > 0*) ·
`value` (vInt length + bytes).

The time-units byte packs the lifespan unit (high nibble) and maxIdle unit (low nibble);
`DEFAULT` = `0x7`, so "no expiration" = `0x77` with no following vLongs.

## Authentication (SASL)

A secured endpoint rejects every operation (`ISPN006017: Operation 'PUT' requires
authentication`) until the connection has authenticated. Pass credentials to the constructor
and the client runs the SASL exchange before returning. The mechanism defaults to
SCRAM-SHA-256:

```csharp
await using HotRodClient client = await HotRodClient.ConnectAsync("infinispan", 11222,
    username: "admin", password: "secret", mechanism: SaslMechanism.ScramSha256);
```

Two opcodes are involved, each a normal request/response with the standard header:

| Operation | Request opcode | Request body | Response body |
|-----------|---------------|--------------|---------------|
| AUTH_MECH_LIST | `0x21` | *(empty)* | vInt count + that many mechanism-name strings |
| AUTH | `0x23` | mech name (array) + SASL response (array) | `completed` (1 byte) + challenge (array) |

The client sends the mechanism name on every AUTH request; the server creates its SASL server
on the first and reuses it. The same AUTH loop drives both mechanisms — they differ only in how
many rounds they take and who decides the exchange is finished.

### PLAIN

PLAIN (RFC 4616) is a single round-trip: the SASL response is `authzid 0x00 authcid 0x00
password` (UTF-8, `authzid` empty here) and the server replies `completed = 1` with an empty
challenge. The password travels in cleartext, so PLAIN is only safe behind TLS, and it is only
*offered* by a realm that can read the password back to compare it (`ALGORITHM=clear`). A realm
with hashed passwords advertises SCRAM/DIGEST instead and answers AUTH with `Invalid mech 'PLAIN'`.

### SCRAM-SHA-256 / SCRAM-SHA-512

SCRAM (RFC 5802 / 7677) never sends the password and authenticates the server in return. The
rounds:

1. **client-first** `n,,n=<user>,r=<clientNonce>` →
2. **server-first** `r=<clientNonce+serverNonce>,s=<salt>,i=<iterations>` →
3. **client-final** `c=biws,r=<nonce>,p=<proof>`, where
   `SaltedPassword = PBKDF2(password, salt, i)`, `ClientKey = HMAC(SaltedPassword, "Client Key")`,
   `ClientProof = ClientKey XOR HMAC(SHA(ClientKey), AuthMessage)` →
4. **server-final** `v=<serverSignature>`, which the client verifies against
   `HMAC(HMAC(SaltedPassword, "Server Key"), AuthMessage)`.

The subtlety that cost time: Infinispan sends server-final (`v=…`) with `completed = 0`, expecting
one more empty client message before it flips the flag. So the **client** decides the exchange is
over once it has verified the server signature (`ISaslMechanism.IsComplete`) — keying off the
server's `completed` byte alone loops forever. SCRAM works against the default hashed (encrypted)
realm, which stores the per-mechanism salted hashes.

Run a server offering the production mechanism set (`admin` / `secret`) and exercise it:

```bash
docker run -d --name ispn-scram -p 11222:11222 \
  -v "$PWD/test:/user-config:ro" \
  infinispan/server:latest -c /user-config/infinispan-scram.xml

dotnet run --project src/HotRod.Demo -c Release 127.0.0.1 11222 default admin secret ScramSha256
```

## TLS

Pass `TlsOptions` to wrap the connection in TLS before any HotRod bytes are sent. Use it whenever
credentials cross an untrusted network — it is what makes PLAIN safe and protects cache values that
SASL `qop=auth` leaves in cleartext:

```csharp
await using HotRodClient client = await HotRodClient.ConnectAsync("infinispan", 11222,
    username: "admin", password: "secret",
    mechanism: SaslMechanism.ScramSha256,
    tls: new TlsOptions());                       // validates the server certificate
```

`TlsOptions.AllowUntrusted = true` skips certificate validation for self-signed test servers — never
in production, as it removes the guarantee you are talking to the real server. `TargetHost` overrides
the name validated and sent as SNI.

Infinispan serves TLS when its security realm has an `<ssl>` server identity; the same single port
then speaks HTTPS/TLS for both REST and HotRod. To try it with a self-signed keystore:

```bash
# one-off: generate a self-signed keystore the server config points at
docker run --rm --user root --entrypoint /usr/lib/jvm/default-java/bin/keytool \
  -v "$PWD/test:/user-config" infinispan/server:latest \
  -genkeypair -alias server -keyalg RSA -keysize 2048 -validity 365 \
  -dname "CN=localhost" -ext "SAN=dns:localhost,ip:127.0.0.1" \
  -keystore /user-config/server.pfx -storetype PKCS12 -storepass password -keypass password

docker run -d --name ispn-tls -p 11222:11222 \
  -v "$PWD/test:/user-config:ro" \
  infinispan/server:latest -c /user-config/infinispan-tls.xml

dotnet run --project src/HotRod.Demo -c Release 127.0.0.1 11222 default admin secret ScramSha256 tls-insecure
```

## Gotcha that cost the most time: the `otherParams` header field

Protocol **4.0** added a field to the end of the request header: a length-prefixed map of
named parameters (`Map<String, byte[]>`), written as a **vInt count** followed by that many
`(string key, byte[] value)` pairs. An empty map is a single `0` byte.

In the Infinispan client this lives in `Codec40.writeHeader`, which calls the 3.x header
writer and then appends `writeOtherParams(...)`. Because protocol codecs are layered
(`Codec30 → Codec31 → Codec40 → Codec41`), each version only adds its delta — and this one
byte is the delta between 3.1 and 4.0.

Omitting it shifts the entire body by one byte: the server reads the wrong key length, so
`GET` silently looks up the empty key (always "not found") and `PUT` blocks waiting for bytes
that never arrive. The fix is one line in `HotRodConnection.WriteHeader`:

```csharp
HotRodCodec.WriteVInt(s, 0); // other params count (HotRod 4.0+)
```

### What is `otherParams` actually for?

It is a **generic extension point in the header**. Instead of bumping the protocol version
or changing the fixed header layout every time a new piece of per-operation metadata is
needed, HotRod 4.0 reserved a bag of named key/value parameters that a client *may* fill in.
Properties:

- **Forward compatibility** — new optional metadata can be added without a wire-format change.
  A client that knows nothing about a given parameter simply doesn't send it.
- **Always a fixed shape** — the server unconditionally reads the count first, so the header
  stays parseable even when the bag is empty (`0`). That predictable structure is exactly why
  the missing byte broke everything: the field is mandatory, its *contents* are optional.
- **Per-operation, not per-connection** — it rides on each request header, so different calls
  on the same connection can carry different metadata.

The headline real-world consumer is **distributed-tracing context propagation**: a client can
attach the current trace/span context as named parameters so server-side spans link up with
client-side ones, without the cache operations themselves needing new fields. The mechanism is
intentionally open-ended, which is the whole point — that is why a *generic* named-parameter
bag was chosen over a fixed "tracing header" field.

## Verifying against a real server

Wire correctness must ultimately be confirmed against a running server. Useful tools:

- Enable HotRod TRACE at runtime via REST (no auth):
  `curl -XPUT 'http://localhost:11222/rest/v2/logging/loggers/org.infinispan.server.hotrod?level=TRACE'`
  then read `docker exec ispn-mvp cat /opt/infinispan/server/log/server.log`.
  The server logs the parsed header (`HotRodHeader{op=PUT, cacheName='default', otherParams={}}`),
  which is how the byte layout above was validated.
- Cross-check stored values over REST: `curl http://localhost:11222/rest/v2/caches/default/Stad`.
- Validate consistent-hash ownership against the server: `GET /rest/v2/caches/{cache}/{key}?action=distribution`
  returns the key's owners (with the primary flagged). Computing `GetPrimaryOwner` for the same keys and
  comparing is how the MurmurHash3 port was confirmed (40/40 keys agreed on a two-node cluster).

## Roadmap

1. ~~SASL PLAIN authentication~~ — done; see [Authentication](#authentication-sasl).
2. ~~SCRAM-SHA-256 / SCRAM-SHA-512~~ — done.
3. ~~TLS~~ — done; see [TLS](#tls).
4. ~~Async API over `System.IO.Pipelines`~~ — done.
5. ~~Connection pooling~~ — done; see [Using the client](#using-the-client).
6. ~~Topology awareness (a pool per node, round-robin)~~ — done; see [Cluster topology](#cluster-topology).
7. ~~Hash awareness (single-hop routing to a key's primary owner)~~ — done; see [Single-hop routing](#single-hop-routing).
8. ~~ProtoStream marshalling for Java-client interop~~ — done (strings); see [Encoding](#encoding-interop-with-the-java-client-and-console).
9. ~~Conditional, versioned, bulk operations + per-entry expiration~~ — done; see [Operations](#operations).
10. ~~Full status-code handling and a typed exception hierarchy~~ — done; see [Error handling](#error-handling).
11. ~~Pool robustness: background reaper + test-on-borrow (`PING`)~~ — done; see [Using the client](#using-the-client).
12. ~~Per-operation flags + previous-value writes (`FORCE_RETURN_VALUE`)~~ — done; see [Operations](#operations).
13. ~~Entry metadata (`GetWithMetadata`) and cache `Stats`~~ — done; see [Operations](#operations).
14. ~~Iteration (`iterationStart`/`next`/`end`) over a leased connection~~ — done; see [Operations](#operations).
15. ~~Client listeners/events over a dedicated connection~~ — done; see [Client listeners](#client-listeners-events).
16. ~~Invalidated near cache built on the listener stream~~ — done; see [Near cache](#near-cache).
17. ~~`bulkGetKeys` (fetch every key)~~ — done; see [Operations](#operations).
18. ~~Clustered strong/weak counters~~ — done.
19. ~~Transparent retry/failover across cluster nodes~~ — done.
20. ~~ProtoStream marshalling for primitive numbers (`int`/`long`/`float`/`double`/`bool`)~~ — done; see [Encoding](#encoding-interop-with-the-java-client-and-console).

Working towards full HotRod 4.1 coverage (test-driven): server-side `exec`/admin, and query.
Further out: ProtoStream for custom types via a registered `.proto` schema.

## How this was built

This project was built with substantial help from AI (Anthropic's Claude). The wire-protocol
reverse-engineering, implementation, tests, and documentation were developed in close
collaboration with an AI assistant, and much of the code and prose here is AI-generated. Every
feature was reviewed by a human and verified end-to-end against a real Infinispan server before
being included.
