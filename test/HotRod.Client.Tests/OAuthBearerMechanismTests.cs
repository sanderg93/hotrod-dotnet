using System.Text;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the OAUTHBEARER (RFC 7628) client behaviour: the exact initial-response byte layout, the
/// mechanism name and completion semantics, and the success/failure round-trip where a rejected
/// token draws an error challenge that the client answers with a single kvsep byte.
/// </summary>
public class OAuthBearerMechanismTests
{
    private const byte KvSep = 0x01;

    [Fact]
    public void Name_is_the_advertised_mechanism()
    {
        var mechanism = new OAuthBearerMechanism("token");
        Assert.Equal("OAUTHBEARER", mechanism.Name);
    }

    [Fact]
    public void IsComplete_is_server_driven_and_starts_false()
    {
        var mechanism = new OAuthBearerMechanism("token");
        Assert.False(mechanism.IsComplete);
    }

    [Fact]
    public void InitialResponse_matches_the_rfc7628_layout()
    {
        var mechanism = new OAuthBearerMechanism("t0ken-abc");

        byte[] actual = mechanism.InitialResponse();

        // gs2-header "n,," + 0x01 + "auth=Bearer t0ken-abc" + 0x01 + 0x01
        var expected = new List<byte>();
        expected.AddRange(Encoding.UTF8.GetBytes("n,,"));
        expected.Add(KvSep);
        expected.AddRange(Encoding.UTF8.GetBytes("auth=Bearer t0ken-abc"));
        expected.Add(KvSep);
        expected.Add(KvSep);

        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void InitialResponse_ends_with_two_kvsep_bytes()
    {
        byte[] response = new OAuthBearerMechanism("token").InitialResponse();

        Assert.Equal(KvSep, response[^1]);
        Assert.Equal(KvSep, response[^2]);
        // Exactly three kvsep bytes total: after the header, after the auth pair, and the terminator.
        Assert.Equal(3, response.Count(b => b == KvSep));
    }

    [Fact]
    public void InitialResponse_carries_the_bearer_token_verbatim()
    {
        byte[] response = new OAuthBearerMechanism("xyz.123-TOKEN").InitialResponse();

        string decoded = Encoding.UTF8.GetString(response);
        Assert.Contains("auth=Bearer xyz.123-TOKEN", decoded);
        Assert.StartsWith("n,,", decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Construction_rejects_a_missing_token(string? token)
    {
        var ex = Assert.Throws<HotRodException>(() => new OAuthBearerMechanism(token!));
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_answers_an_error_challenge_with_a_single_kvsep()
    {
        var mechanism = new OAuthBearerMechanism("token");
        byte[] errorJson = Encoding.UTF8.GetBytes("{\"status\":\"invalid_token\"}");

        byte[] response = mechanism.Evaluate(errorJson);

        // RFC 7628: the client sends a lone %x01 so the server can finish and fail the exchange.
        Assert.Equal(new byte[] { KvSep }, response);
    }

    [Fact]
    public void Evaluate_surfaces_the_server_error_if_the_exchange_does_not_finish()
    {
        var mechanism = new OAuthBearerMechanism("token");
        byte[] errorJson = Encoding.UTF8.GetBytes("{\"status\":\"invalid_token\"}");

        mechanism.Evaluate(errorJson); // first challenge: acknowledged with a kvsep

        var ex = Assert.Throws<HotRodException>(() => mechanism.Evaluate(errorJson));
        Assert.Contains("invalid_token", ex.Message);
    }

    [Fact]
    public void ValidateCompletion_accepts_the_success_path()
    {
        var mechanism = new OAuthBearerMechanism("token");
        // A successful single round-trip completes on the server side with no verification to do.
        mechanism.ValidateCompletion([]);
    }
}
