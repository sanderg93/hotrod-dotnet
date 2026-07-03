using HotRod.Client;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// The public <see cref="ClientIntelligence"/> enum is sent on the wire as its byte value, so each
/// member must equal the corresponding protocol constant exactly.
/// </summary>
public class ClientIntelligenceTests
{
    [Fact]
    public void Enum_values_match_the_protocol_bytes()
    {
        Assert.Equal(Constants.IntelligenceBasic, (byte)ClientIntelligence.Basic);
        Assert.Equal(Constants.IntelligenceTopologyAware, (byte)ClientIntelligence.TopologyAware);
        Assert.Equal(Constants.IntelligenceHashAware, (byte)ClientIntelligence.HashAware);
    }

    [Fact]
    public void HashAware_is_the_default_intelligence()
    {
        var options = new HotRodClientOptions();

        Assert.Equal(ClientIntelligence.HashAware, options.Intelligence);
    }
}
