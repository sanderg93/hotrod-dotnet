using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// SASL OAUTHBEARER (RFC 7628): the client presents an OAuth 2.0 bearer token instead of a
/// username and password. The initial client response carries the token, and the server either
/// completes the exchange (success) or returns a JSON error challenge. On the error path the
/// mechanism replies with a single kvsep byte, as the RFC requires, so the server can finish the
/// exchange and report the failure.
/// </summary>
/// <remarks>
/// The initial response is <c>gs2-header</c> + kvsep + <c>auth=Bearer &lt;token&gt;</c> + kvsep +
/// kvsep, where the gs2 header is <c>n,,</c> (no channel binding, no authorization id) and kvsep is
/// 0x01. The host/port key-value pairs the RFC allows are optional and are not sent over this
/// transport. Like PLAIN, completion is server-driven: the server reports it on the AUTH response.
/// </remarks>
internal sealed class OAuthBearerMechanism : ISaslMechanism
{
    private const byte KvSep = 0x01; // control-A separator between the gs2 header and each key-value pair
    private const string Gs2Header = "n,,"; // no channel binding, no authorization id

    private readonly string _token;
    private bool _failureAcknowledged;

    public OAuthBearerMechanism(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new HotRodException("OAUTHBEARER authentication requires a bearer token");
        _token = token;
    }

    public string Name => "OAUTHBEARER";

    // Completion is reported by the server on the AUTH response, not decided on the client side.
    public bool IsComplete => false;

    /// <summary>Builds <c>n,,</c> 0x01 <c>auth=Bearer &lt;token&gt;</c> 0x01 0x01.</summary>
    public byte[] InitialResponse()
    {
        byte[] header = Encoding.UTF8.GetBytes(Gs2Header);
        byte[] auth = Encoding.UTF8.GetBytes($"auth=Bearer {_token}");

        var response = new byte[header.Length + 1 + auth.Length + 1 + 1];
        int i = 0;
        Array.Copy(header, 0, response, i, header.Length);
        i += header.Length;
        response[i++] = KvSep;
        Array.Copy(auth, 0, response, i, auth.Length);
        i += auth.Length;
        response[i++] = KvSep;
        response[i] = KvSep;
        return response;
    }

    /// <summary>
    /// Handles the server's error challenge: the server sends a JSON error object when the token is
    /// rejected, and the RFC requires the client to answer with a single kvsep byte so the server can
    /// finish the exchange (which it does by failing the authentication). A second challenge means the
    /// server did not finish as expected, so the recorded error surfaces rather than looping.
    /// </summary>
    public byte[] Evaluate(byte[] challenge)
    {
        if (_failureAcknowledged)
            throw new HotRodException($"OAUTHBEARER authentication failed: {DescribeError(challenge)}");

        _failureAcknowledged = true;
        return [KvSep];
    }

    public void ValidateCompletion(byte[] finalChallenge) { }

    /// <summary>Renders the server's error challenge as text for an exception message; empty when absent.</summary>
    private static string DescribeError(byte[] challenge) =>
        challenge.Length == 0 ? "no server error detail" : Encoding.UTF8.GetString(challenge);
}
