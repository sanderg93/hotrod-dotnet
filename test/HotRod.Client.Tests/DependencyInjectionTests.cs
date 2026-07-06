using HotRod.Client.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HotRod.Client.Tests;

/// <summary>
/// Covers the DI surface: <see cref="ServiceCollectionExtensions.AddHotRodClient"/> registers the client
/// as a singleton, and <see cref="HotRodClientBuilder"/> turns fluent/explicit configuration into options
/// while flowing the container's logger factory in. Registration is asserted without resolving the client,
/// since resolving establishes a live connection.
/// </summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddHotRodClient_registers_the_client_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddHotRodClient(b => b.UseServer("localhost"));

        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(HotRodClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddHotRodClient_guards_its_arguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddHotRodClient(null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddHotRodClient(_ => { }));
    }

    [Fact]
    public void Builder_applies_fluent_settings_and_flows_the_logger_factory()
    {
        var factory = new RecordingLoggerFactory();

        HotRodClientOptions options = new HotRodClientBuilder()
            .UseServer("node-a", 12345)
            .UseCredentials("alice", "secret", SaslMechanism.Plain)
            .UseMaxRetries(3)
            .UsePoolSize(2, 8)
            .Build(factory);

        Assert.Equal("node-a", options.Host);
        Assert.Equal(12345, options.Port);
        Assert.Equal("alice", options.Username);
        Assert.Equal("secret", options.Password);
        Assert.Equal(SaslMechanism.Plain, options.Mechanism);
        Assert.Equal(3, options.MaxRetries);
        Assert.Equal(2, options.MinConnections);
        Assert.Equal(8, options.MaxConnections);
        Assert.Same(factory, options.LoggerFactory);
    }

    [Fact]
    public void Builder_keeps_defaults_for_unset_settings()
    {
        var defaults = new HotRodClientOptions();

        HotRodClientOptions options = new HotRodClientBuilder()
            .UseServer("node-a")
            .Build(NullLoggerFactory.Instance);

        Assert.Equal(defaults.Port, options.Port);
        Assert.Equal(defaults.MaxConnections, options.MaxConnections);
        Assert.Equal(defaults.MaxRetries, options.MaxRetries);
        Assert.Equal(defaults.Intelligence, options.Intelligence);
        Assert.Null(options.Username);
    }

    [Fact]
    public void Builder_Configure_escape_hatch_takes_over_and_receives_the_logger_factory()
    {
        var factory = new RecordingLoggerFactory();

        HotRodClientOptions options = new HotRodClientBuilder()
            .UseServer("ignored", 1) // overridden by Configure
            .Configure(lf => new HotRodClientOptions { Host = "explicit", Port = 999, LoggerFactory = lf })
            .Build(factory);

        Assert.Equal("explicit", options.Host);
        Assert.Equal(999, options.Port);
        Assert.Same(factory, options.LoggerFactory);
    }
}
