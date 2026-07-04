using System.Security.Cryptography.X509Certificates;

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

    /// <summary>
    /// A client certificate presented during the TLS handshake so the server can authenticate the
    /// client (mutual TLS). Its private key must be available. Supply this together with
    /// <see cref="SaslMechanism.External"/> to have the server map the validated certificate to a
    /// user; leave it null for server-only TLS.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// Additional client certificates to offer during the handshake, letting the server pick one it
    /// trusts and letting a cert be sent with its issuing chain. Combined with
    /// <see cref="ClientCertificate"/> when both are set. Leave null when a single certificate suffices.
    /// </summary>
    public X509CertificateCollection? ClientCertificates { get; init; }

    /// <summary>
    /// Builds the certificate collection to hand to the TLS client-authentication path, merging
    /// <see cref="ClientCertificate"/> and <see cref="ClientCertificates"/>. Returns null when neither
    /// is set, so no client certificate is offered.
    /// </summary>
    internal X509CertificateCollection? BuildClientCertificates()
    {
        if (ClientCertificate is null && ClientCertificates is null)
            return null;

        var collection = new X509CertificateCollection();
        if (ClientCertificate is not null)
            collection.Add(ClientCertificate);
        if (ClientCertificates is not null)
            collection.AddRange(ClientCertificates);
        return collection;
    }
}
