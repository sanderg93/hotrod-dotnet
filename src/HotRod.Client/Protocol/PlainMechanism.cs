namespace HotRod.Client.Protocol;

/// <summary>
/// SASL PLAIN as an <see cref="ISaslMechanism"/>: the credentials are the initial
/// response and the server completes in one round, so there is never a challenge to
/// process and no server signature to verify.
/// </summary>
internal sealed class PlainMechanism : ISaslMechanism
{
    private readonly string _username;
    private readonly string _password;

    public PlainMechanism(string username, string password)
    {
        _username = username;
        _password = password;
    }

    public string Name => "PLAIN";

    // PLAIN finishes when the server reports completion, not on the client side.
    public bool IsComplete => false;

    public byte[] InitialResponse() => SaslPlain.BuildResponse(_username, _password);

    public byte[] Evaluate(byte[] challenge) =>
        throw new HotRodException("PLAIN authentication does not expect a server challenge");

    public void ValidateCompletion(byte[] finalChallenge) { }
}
