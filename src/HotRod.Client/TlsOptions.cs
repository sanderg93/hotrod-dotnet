namespace HotRod.Client;

/// <summary>
/// TLS settings for a <see cref="HotRodClient"/> connection. Pass an instance to enable
/// encryption; leave it null to connect in plaintext. Use TLS whenever credentials cross
/// an untrusted network — in particular it is what makes SASL PLAIN safe to use.
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// The host name to validate the server certificate against and to send as SNI.
    /// Defaults to the host passed to the connection when null.
    /// </summary>
    public string? TargetHost { get; init; }

    /// <summary>
    /// Skips certificate-chain and name validation. Convenient for self-signed test
    /// servers; never enable it against production, as it removes the guarantee that you
    /// are talking to the real server.
    /// </summary>
    public bool AllowUntrusted { get; init; }
}
