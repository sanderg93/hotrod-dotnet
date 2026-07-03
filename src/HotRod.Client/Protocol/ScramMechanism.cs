using System.Security.Cryptography;
using System.Text;

namespace HotRod.Client.Protocol;

/// <summary>
/// SASL SCRAM (Salted Challenge Response Authentication Mechanism), RFC 5802 / RFC 7677.
/// The password is never sent; instead both sides prove knowledge of a salted hash of it,
/// and the client verifies the server's signature in return. Parameterised by hash
/// algorithm so the same logic serves SCRAM-SHA-256 and SCRAM-SHA-512.
/// </summary>
/// <remarks>
/// Channel binding is not used ("n,," / "c=biws"). Usernames and passwords are taken as
/// UTF-8 without SASLprep (RFC 4013) normalisation, which only matters for non-ASCII input.
/// </remarks>
internal sealed class ScramMechanism : ISaslMechanism
{
    private const string Gs2Header = "n,,"; // no channel binding, no authorization id

    private readonly string _username;
    private readonly string _password;
    private readonly HashAlgorithmName _hash;
    private readonly int _hashSizeBytes;

    private string _clientNonce = string.Empty;
    private string _clientFirstBare = string.Empty;
    private byte[] _serverSignature = [];
    private bool _serverVerified;

    public ScramMechanism(string name, string username, string password, HashAlgorithmName hash, int hashSizeBytes)
    {
        Name = name;
        _username = username;
        _password = password;
        _hash = hash;
        _hashSizeBytes = hashSizeBytes;
    }

    public string Name { get; }

    public bool IsComplete => _serverVerified;

    /// <summary>Builds client-first-message: <c>n,,n=user,r=nonce</c>.</summary>
    public byte[] InitialResponse()
    {
        _clientNonce = GenerateNonce();
        _clientFirstBare = $"n={Escape(_username)},r={_clientNonce}";
        return Encoding.UTF8.GetBytes(Gs2Header + _clientFirstBare);
    }

    /// <summary>
    /// Processes a server challenge. server-first (<c>r=…,s=…,i=…</c>) yields client-final
    /// (<c>c=biws,r=…,p=proof</c>); server-final (<c>v=signature</c>) is verified and answered
    /// with an empty response so the server can complete the exchange.
    /// </summary>
    public byte[] Evaluate(byte[] challenge)
    {
        string message = Encoding.UTF8.GetString(challenge);
        var attrs = ParseAttributes(message);

        // server-final (mutual authentication) can arrive as its own round rather than
        // alongside the completion flag; verify it and reply with an empty response.
        if (!attrs.ContainsKey('r'))
        {
            VerifyServer(attrs, message);
            return [];
        }

        string serverNonce = attrs['r'];
        if (!serverNonce.StartsWith(_clientNonce, StringComparison.Ordinal))
            throw new HotRodException("SCRAM server nonce does not start with the client nonce");

        byte[] salt = Convert.FromBase64String(attrs['s']);
        int iterations = int.Parse(attrs['i']);

        // c=biws is base64("n,,"); the channel-binding input is just the gs2 header here.
        string clientFinalNoProof = $"c={Convert.ToBase64String(Encoding.UTF8.GetBytes(Gs2Header))},r={serverNonce}";
        byte[] authMessage = Encoding.UTF8.GetBytes($"{_clientFirstBare},{message},{clientFinalNoProof}");

        byte[] saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(_password), salt, iterations, _hash, _hashSizeBytes);

        byte[] clientKey = Hmac(saltedPassword, "Client Key");
        byte[] storedKey = Hash(clientKey);
        byte[] clientSignature = Hmac(storedKey, authMessage);
        byte[] clientProof = Xor(clientKey, clientSignature);

        byte[] serverKey = Hmac(saltedPassword, "Server Key");
        _serverSignature = Hmac(serverKey, authMessage);

        string clientFinal = $"{clientFinalNoProof},p={Convert.ToBase64String(clientProof)}";
        return Encoding.UTF8.GetBytes(clientFinal);
    }

    /// <summary>Verifies server-final if it arrives together with the completion flag.</summary>
    public void ValidateCompletion(byte[] finalChallenge)
    {
        if (finalChallenge.Length == 0)
        {
            if (!_serverVerified)
                throw new HotRodException("SCRAM completed without a server signature to verify");
            return;
        }

        string message = Encoding.UTF8.GetString(finalChallenge);
        VerifyServer(ParseAttributes(message), message);
    }

    /// <summary>Checks the server signature (<c>v=…</c>) so a forged server is rejected.</summary>
    private void VerifyServer(Dictionary<char, string> attrs, string message)
    {
        if (attrs.TryGetValue('e', out string? error))
            throw new HotRodException($"SCRAM authentication failed: {error}");
        if (!attrs.TryGetValue('v', out string? verifier))
            throw new HotRodException($"SCRAM server message has no verifier: {message}");

        byte[] received = Convert.FromBase64String(verifier);
        if (!CryptographicOperations.FixedTimeEquals(received, _serverSignature))
            throw new HotRodException("SCRAM server signature verification failed");

        _serverVerified = true;
    }

    private byte[] Hmac(byte[] key, string text) => Hmac(key, Encoding.UTF8.GetBytes(text));

    private byte[] Hmac(byte[] key, byte[] data)
    {
        using var hmac = IncrementalHash.CreateHMAC(_hash, key);
        hmac.AppendData(data);
        return hmac.GetHashAndReset();
    }

    private byte[] Hash(byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(_hash);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }

    /// <summary>A client nonce: random bytes as base64, whose alphabet excludes the ',' separator.</summary>
    private static string GenerateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    /// <summary>SCRAM reserves ',' and '=' in the username, escaped as =2C and =3D.</summary>
    private static string Escape(string username) =>
        username.Replace("=", "=3D").Replace(",", "=2C");

    /// <summary>Parses a comma-separated SCRAM message into single-letter attributes.</summary>
    private static Dictionary<char, string> ParseAttributes(string message)
    {
        var attrs = new Dictionary<char, string>();
        foreach (string part in message.Split(','))
        {
            if (part.Length >= 2 && part[1] == '=')
                attrs[part[0]] = part[2..];
        }
        return attrs;
    }
}
