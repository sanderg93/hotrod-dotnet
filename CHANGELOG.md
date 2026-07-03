# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once a first stable release
is published.

## [Unreleased]

### Added

- Key/value operations: `Get`, `Put`, `Remove`, `ContainsKey`, `Clear`, `Size`, each in a `string`
  and a `byte[]` overload.
- Conditional operations: `PutIfAbsent`, `Replace`.
- Versioned operations for optimistic concurrency: `GetWithVersion`, `ReplaceWithVersion`,
  `RemoveWithVersion`.
- Previous-value writes (`FORCE_RETURN_VALUE`): `PutAndReturnPrevious`,
  `RemoveAndReturnPrevious`, `ReplaceAndReturnPrevious`, `PutIfAbsentAndReturnPrevious`.
- Bulk operations: `PutAll`, `GetAll`.
- `bulkGetKeys` support: `GetKeysAsync` / `GetKeyStringsAsync` retrieve every key in a cache.
- Per-entry expiration (lifespan / max-idle) on every write.
- Entry metadata (`GetWithMetadata`) and cache statistics (`Stats`).
- Cache iteration (`IterateAsync` / `IterateStringsAsync`) over a server-side cursor, streamed as
  an `IAsyncEnumerable` in batches.
- Client listeners: subscribe to server-pushed cache entry events (created / modified / removed /
  expired) over a dedicated connection.
- An invalidated near cache: a local, size-bounded copy of read entries, kept consistent via the
  client-listener event stream.
- Connection pooling per cluster node, with idle/lifetime eviction and optional validate-on-borrow.
- Cluster topology discovery with hash-aware (single-hop) routing: the client learns every node and
  routes each key directly to its primary owner using Infinispan's own consistent hash.
- Authentication via SASL PLAIN and SASL SCRAM-SHA-256 / SCRAM-SHA-512.
- TLS support for encrypting the connection to the server.
- ProtoStream marshalling for string keys and values, interoperable with the Java client and the
  Infinispan console.
- A typed exception hierarchy (`HotRodException` / `HotRodServerException` /
  `HotRodTimeoutException`) mapping every server status code.

[Unreleased]: https://github.com/sanderg93/hotrod-dotnet/commits/main
