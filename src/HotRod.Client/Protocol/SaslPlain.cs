using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// Builds the SASL PLAIN client response defined by RFC 4616:
/// <c>authzid NUL authcid NUL passwd</c>, each part UTF-8 encoded and separated by a
/// NUL (0x00) byte. PLAIN is a single-message mechanism — the client sends these
/// credentials in one shot and the server replies that authentication is complete,
/// so there is no server challenge to process. The credentials travel in cleartext,
/// so PLAIN is only safe over an encrypted transport.
/// </summary>
internal static class SaslPlain
{
    public static byte[] BuildResponse(string username, string password, string authorizationId = "")
    {
        byte[] authzid = Encoding.UTF8.GetBytes(authorizationId);
        byte[] authcid = Encoding.UTF8.GetBytes(username);
        byte[] passwd = Encoding.UTF8.GetBytes(password);

        var response = new byte[authzid.Length + 1 + authcid.Length + 1 + passwd.Length];
        int i = 0;
        Array.Copy(authzid, 0, response, i, authzid.Length);
        i += authzid.Length;
        response[i++] = 0x00;
        Array.Copy(authcid, 0, response, i, authcid.Length);
        i += authcid.Length;
        response[i++] = 0x00;
        Array.Copy(passwd, 0, response, i, passwd.Length);
        return response;
    }
}
