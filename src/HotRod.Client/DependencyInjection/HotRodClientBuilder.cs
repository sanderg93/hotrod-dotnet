using Microsoft.Extensions.Logging;

namespace HotRod.Client.DependencyInjection;

/// <summary>
/// Fluent configuration for the <see cref="HotRodClient"/> registered by
/// <see cref="ServiceCollectionExtensions.AddHotRodClient"/>. The common settings have dedicated
/// <c>Use…</c> methods; <see cref="Configure"/> is an escape hatch for the full option set. Either way
/// the container's <see cref="ILoggerFactory"/> is flowed into the options at build time, so the
/// client's logs join the application's logging pipeline without the caller wiring it up.
/// </summary>
public sealed class HotRodClientBuilder
{
    private Func<ILoggerFactory, HotRodClientOptions>? _configure;

    private string? _host;
    private int? _port;
    private string? _username;
    private string? _password;
    private SaslMechanism? _mechanism;
    private TlsOptions? _tls;
    private ClientIntelligence? _intelligence;
    private int? _maxRetries;
    private int? _minConnections;
    private int? _maxConnections;

    /// <summary>Sets the seed server the client connects to.</summary>
    public HotRodClientBuilder UseServer(string host, int port = 11222)
    {
        _host = host;
        _port = port;
        return this;
    }

    /// <summary>Sets the SASL credentials and mechanism used to authenticate.</summary>
    public HotRodClientBuilder UseCredentials(string username, string password, SaslMechanism mechanism = SaslMechanism.ScramSha256)
    {
        _username = username;
        _password = password;
        _mechanism = mechanism;
        return this;
    }

    /// <summary>Enables TLS with the given settings.</summary>
    public HotRodClientBuilder UseTls(TlsOptions tls)
    {
        _tls = tls;
        return this;
    }

    /// <summary>Sets how much cluster topology the client tracks.</summary>
    public HotRodClientBuilder UseIntelligence(ClientIntelligence intelligence)
    {
        _intelligence = intelligence;
        return this;
    }

    /// <summary>Sets the per-node connection-pool bounds.</summary>
    public HotRodClientBuilder UsePoolSize(int minConnections, int maxConnections)
    {
        _minConnections = minConnections;
        _maxConnections = maxConnections;
        return this;
    }

    /// <summary>Sets how many additional failover attempts an operation makes after a retriable failure.</summary>
    public HotRodClientBuilder UseMaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Supplies the complete options given the container's <see cref="ILoggerFactory"/>, overriding the
    /// <c>Use…</c> methods. Set <see cref="HotRodClientOptions.LoggerFactory"/> to the provided factory to
    /// keep the client's logs in the application's pipeline.
    /// </summary>
    public HotRodClientBuilder Configure(Func<ILoggerFactory, HotRodClientOptions> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>Builds the options, flowing <paramref name="loggerFactory"/> in and applying defaults for unset values.</summary>
    internal HotRodClientOptions Build(ILoggerFactory loggerFactory)
    {
        if (_configure is not null)
            return _configure(loggerFactory);

        var defaults = new HotRodClientOptions();
        return new HotRodClientOptions
        {
            Host = _host ?? defaults.Host,
            Port = _port ?? defaults.Port,
            Username = _username,
            Password = _password,
            Mechanism = _mechanism ?? defaults.Mechanism,
            Tls = _tls,
            Intelligence = _intelligence ?? defaults.Intelligence,
            MaxRetries = _maxRetries ?? defaults.MaxRetries,
            MinConnections = _minConnections ?? defaults.MinConnections,
            MaxConnections = _maxConnections ?? defaults.MaxConnections,
            LoggerFactory = loggerFactory,
        };
    }
}
