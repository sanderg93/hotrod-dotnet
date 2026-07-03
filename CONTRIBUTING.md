# Contributing to HotRod.Client

Thanks for considering a contribution. This document covers how to build the project, run its
tests, and the conventions changes are expected to follow.

## Building

```bash
dotnet build HotRod.slnx
```

This builds the library (`src/HotRod.Client`), the console demo (`src/HotRod.Demo`), and the test
project (`test/HotRod.Client.Tests`).

## Running the tests

```bash
dotnet test
```

The unit test suite (`test/HotRod.Client.Tests`) runs against mocked I/O and needs nothing external.

Some tests are integration tests that need a running Infinispan server reachable over HotRod 4.1
(Infinispan 16.x). Bring one up with Docker before running those, for example:

```bash
docker run -d --name ispn-dev -p 11222:11222 \
  -v "$PWD/test/infinispan-noauth.xml:/user-config/infinispan.xml:ro" \
  infinispan/server:latest -c /user-config/infinispan.xml
```

The `test/` directory has additional server configs (`infinispan-auth.xml`, `infinispan-scram.xml`,
`infinispan-tls.xml`, `infinispan-cluster.xml`) for authentication, TLS, and cluster-topology
scenarios — see the README for how each is used.

## Coding conventions

- **Nullable reference types are enabled** across the library and tests; keep new code
  nullable-clean rather than suppressing warnings.
- **Comments and doc comments describe behavior**, not the state of the project — avoid words
  like "MVP", "POC", "phase", "initial", or "for now". A comment should read the same whether it
  was written on day one or after years of use.
- Tests use **xUnit** and **NSubstitute**. Do not add or recommend **FluentAssertions** in this
  repository (licensing) — use xUnit's own `Assert` API.
- Before building the wire-level behavior of an operation, check the Infinispan Java client/server
  source for the actual request/response layout rather than guessing — the protocol notes in the
  README exist because this was learned the hard way.

## Dependencies

Packages resolve from the official **nuget.org** feed only — see the repository's `NuGet.config`.
Do not add alternate or private package sources.

## Submitting changes

Open a pull request against `main` with a clear description of the change and, where relevant, the
tests that cover it. Small, focused pull requests are easier to review than large ones.
