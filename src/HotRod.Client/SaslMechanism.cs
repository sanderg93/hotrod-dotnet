namespace HotRod.Client;

/// <summary>
/// The SASL mechanism used to authenticate a <see cref="HotRodClient"/> connection.
/// The server advertises which mechanisms it accepts during the handshake; the chosen
/// one must be in that list.
/// </summary>
public enum SaslMechanism
{
    /// <summary>
    /// Sends username and password in cleartext (RFC 4616). Only offered by realms that
    /// store retrievable passwords, and only safe over TLS.
    /// </summary>
    Plain,

    /// <summary>
    /// Salted challenge-response over SHA-256 (RFC 5802 / RFC 7677). The password never
    /// leaves the client; the server is authenticated in return.
    /// </summary>
    ScramSha256,

    /// <summary>Salted challenge-response over SHA-512.</summary>
    ScramSha512,
}
