# Security Policy

## Supported Versions

HotRod.Client is pre-1.0; the latest published release on
[NuGet](https://www.nuget.org/packages/HotRod.Client) is the only version that receives security
fixes. There is no long-term-support branch yet — upgrading to the latest release is the supported
path for any fix, security or otherwise.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for a suspected security vulnerability.

Instead, report it privately through GitHub's
[private vulnerability reporting](https://github.com/sanderg93/hotrod-dotnet/security/advisories/new)
— the **Report a vulnerability** button on the repository's **Security** tab — including:

- A description of the vulnerability and its potential impact.
- Steps to reproduce it, including any relevant code, configuration, or server setup.
- The affected version(s) of HotRod.Client.

You should expect an initial response within a few days. Once a fix is available it will be
released and the report will be credited (unless you prefer to remain anonymous) in the release
notes.

## Scope

This client implements a binary wire protocol (HotRod) against an Infinispan server. Reports of
particular interest include:

- Memory-safety or parsing issues when handling server responses (the codec, protocol framing).
- Authentication or TLS handling that could weaken or bypass SASL/TLS guarantees.
- Any case where the client could be induced to trust or misinterpret data from an untrusted server.

Vulnerabilities in Infinispan itself should be reported to the
[Infinispan project](https://infinispan.org/security/) instead.
