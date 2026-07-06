using System.Text;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the SASL EXTERNAL response bytes: the identity comes from the TLS certificate, so the
/// initial response is empty (act as the certificate's own identity) or the UTF-8 of an
/// explicit authorization identity, and the exchange is a single round with no challenge.
/// </summary>
public class ExternalMechanismTests
{
    [Fact]
    public void Name_is_EXTERNAL()
    {
        Assert.Equal("EXTERNAL", new ExternalMechanism().Name);
    }

    [Fact]
    public void Initial_response_is_empty_without_an_authorization_id()
    {
        Assert.Empty(new ExternalMechanism().InitialResponse());
    }

    [Fact]
    public void Initial_response_is_empty_for_an_empty_authorization_id()
    {
        Assert.Empty(new ExternalMechanism(string.Empty).InitialResponse());
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("CN=client,OU=dev")]
    [InlineData("üser")]
    public void Initial_response_is_the_utf8_authorization_id(string authorizationId)
    {
        byte[] response = new ExternalMechanism(authorizationId).InitialResponse();

        Assert.Equal(Encoding.UTF8.GetBytes(authorizationId), response);
    }

    [Fact]
    public void Server_completes_the_exchange_so_the_client_does_not_report_completion()
    {
        // Matches PLAIN: the server flips the completion flag; the mechanism never claims it first.
        Assert.False(new ExternalMechanism().IsComplete);
    }

    [Fact]
    public void Evaluate_rejects_an_unexpected_challenge()
    {
        var mechanism = new ExternalMechanism();

        Assert.Throws<HotRodException>(() => mechanism.Evaluate([1, 2, 3]));
    }

    [Fact]
    public void ValidateCompletion_ignores_the_final_challenge()
    {
        // EXTERNAL does not authenticate the server, so there is nothing to verify.
        new ExternalMechanism().ValidateCompletion([9, 9, 9]);
    }
}
