namespace HotRod.Client.Protocol;

/// <summary>
/// A SASL client mechanism driven by the HotRod AUTH exchange. The client sends an
/// initial response, then processes each server challenge in turn until the server
/// reports the authentication complete. Single-step mechanisms (PLAIN) finish on the
/// first reply; challenge-response ones (SCRAM) take several rounds.
/// </summary>
internal interface ISaslMechanism
{
    /// <summary>SASL mechanism name as advertised by the server, e.g. "SCRAM-SHA-256".</summary>
    string Name { get; }

    /// <summary>
    /// True once the mechanism itself considers authentication finished and has nothing
    /// more to send. SCRAM sets this after verifying the server signature, which is the
    /// real terminator — the server's own completion flag can lag a round behind.
    /// </summary>
    bool IsComplete { get; }

    /// <summary>The first response to send with the initial AUTH request (may be empty).</summary>
    byte[] InitialResponse();

    /// <summary>Processes a server challenge and returns the next client response.</summary>
    byte[] Evaluate(byte[] challenge);

    /// <summary>
    /// Called once the server reports completion, with the final challenge bytes.
    /// Mechanisms that authenticate the server (SCRAM) verify the server signature here;
    /// others ignore it.
    /// </summary>
    void ValidateCompletion(byte[] finalChallenge);
}
