using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// SASL EXTERNAL (RFC 4422 App. A) as an <see cref="ISaslMechanism"/>: the identity is taken
/// from an out-of-band channel — here the client certificate validated during the TLS
/// handshake — so no credentials travel in the SASL exchange. The initial response is the
/// optional authorization identity (UTF-8) or empty to act as the certificate's own identity,
/// and the server completes in one round, so there is never a challenge to process.
/// </summary>
internal sealed class ExternalMechanism : ISaslMechanism
{
    private readonly string? _authorizationId;

    public ExternalMechanism(string? authorizationId = null)
    {
        _authorizationId = authorizationId;
    }

    public string Name => "EXTERNAL";

    // EXTERNAL finishes when the server reports completion, not on the client side.
    public bool IsComplete => false;

    public byte[] InitialResponse() =>
        string.IsNullOrEmpty(_authorizationId) ? [] : Encoding.UTF8.GetBytes(_authorizationId);

    public byte[] Evaluate(byte[] challenge) =>
        throw new HotRodException("EXTERNAL authentication does not expect a server challenge");

    public void ValidateCompletion(byte[] finalChallenge) { }
}
