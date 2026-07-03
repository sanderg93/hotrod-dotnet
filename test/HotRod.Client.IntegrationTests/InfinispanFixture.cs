using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HotRod.Client;

namespace HotRod.Client.IntegrationTests;

/// <summary>
/// Starts a single real Infinispan server in a throwaway container for the whole test class and
/// hands out connected clients against it. The server runs the SCRAM security realm from
/// <c>containerconfig/</c> (admin/secret, the <c>default</c> cache), so tests exercise the client's
/// default SASL SCRAM-SHA-256 authentication end-to-end. Testcontainers assigns an ephemeral host
/// port and removes the container on dispose.
/// </summary>
public sealed class InfinispanFixture : IAsyncLifetime
{
    // Pinned to the 16.x line the client targets (HotRod 4.1, the config schema in containerconfig/).
    private const string Image = "quay.io/infinispan/server:16.0";
    private const int HotRodPort = 11222;

    /// <summary>The user provisioned in the SCRAM realm; matches scram-users.properties.</summary>
    public const string Username = "admin";
    public const string Password = "secret";

    /// <summary>A cache declared in the mounted server configuration.</summary>
    public const string CacheName = "default";

    private IContainer _container = null!;

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(HotRodPort);

    public async Task InitializeAsync()
    {
        string configDir = Path.Combine(AppContext.BaseDirectory, "containerconfig");

        _container = new ContainerBuilder(Image)
            .WithResourceMapping(new FileInfo(Path.Combine(configDir, "infinispan-scram.xml")), "/user-config/")
            .WithResourceMapping(new FileInfo(Path.Combine(configDir, "scram-users.properties")), "/user-config/")
            .WithResourceMapping(new FileInfo(Path.Combine(configDir, "scram-groups.properties")), "/user-config/")
            .WithCommand("-c", "/user-config/infinispan-scram.xml")
            .WithPortBinding(HotRodPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged(new Regex("ISPN080001"), o => o.WithTimeout(TimeSpan.FromMinutes(3))))
            .Build();

        await _container.StartAsync();
    }

    /// <summary>Connects a fresh client to the running server with SCRAM-SHA-256 authentication.</summary>
    public ValueTask<HotRodClient> ConnectAsync() =>
        HotRodClient.ConnectAsync(Host, Port, Username, Password, SaslMechanism.ScramSha256);

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
