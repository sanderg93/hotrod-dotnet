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

    /// <summary>
    /// Derives the client identity from an out-of-band channel rather than a password (RFC 4422
    /// App. A). Used with mutual TLS: the client presents a certificate during the handshake and
    /// the server maps that validated certificate to a user, so the SASL exchange carries no
    /// credentials. Requires a client certificate on <see cref="TlsOptions"/>.
    /// </summary>
    External,

    /// <summary>
    /// Presents an OAuth 2.0 bearer token instead of a username and password (RFC 7628). The token
    /// is supplied via <see cref="HotRodClientOptions.Token"/>; only safe over TLS.
    /// </summary>
    OAuthBearer,
}
