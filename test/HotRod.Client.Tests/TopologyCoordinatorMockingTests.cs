using HotRod.Client;

namespace HotRod.Client.Tests;

/// <summary>
/// Exercises NSubstitute against the library's <em>internal</em> interfaces (made mockable via
/// <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c>). It pins the <see cref="ITopologyCoordinator"/>
/// contract a connection relies on — read the last topology id, report a parsed update — and proves
/// the mocking toolchain works for the collaborator-heavy phases (iteration, listeners) to come.
/// </summary>
public class TopologyCoordinatorMockingTests
{
    [Fact]
    public void A_substitute_returns_the_stubbed_topology_id()
    {
        var coordinator = Substitute.For<ITopologyCoordinator>();
        coordinator.GetTopologyId("orders").Returns(7);

        Assert.Equal(7, coordinator.GetTopologyId("orders"));
    }

    [Fact]
    public void Reported_topology_is_observed_with_its_parsed_servers()
    {
        var coordinator = Substitute.For<ITopologyCoordinator>();
        var servers = new[] { new ServerAddress("10.0.0.1", 11222), new ServerAddress("10.0.0.2", 11222) };

        coordinator.ReportTopology("orders", topologyId: 12, servers, hash: null);

        coordinator.Received(1).ReportTopology(
            "orders",
            12,
            Arg.Is<IReadOnlyList<ServerAddress>>(s => s.Count == 2 && s[0].Host == "10.0.0.1"),
            null);
    }
}
