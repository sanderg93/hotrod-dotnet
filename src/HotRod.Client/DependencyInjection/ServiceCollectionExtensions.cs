using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HotRod.Client.DependencyInjection;

/// <summary>
/// Registers a <see cref="HotRodClient"/> in a <see cref="IServiceCollection"/>. The client is a
/// singleton — one pooled, topology-aware connection set shared across the application.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="HotRodClient"/> configured through <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="HotRodClient.ConnectAsync(HotRodClientOptions, CancellationToken)"/> is asynchronous, but a
    /// DI singleton factory is synchronous, so the connection is established once, on first resolution, by
    /// blocking on that call. A generic host has no synchronization context, so this does not deadlock;
    /// resolve the client during startup to surface connection errors early rather than on first use. The
    /// application's <see cref="ILoggerFactory"/> is pulled from the container and flowed into the options so
    /// the client's logs join the application's logging pipeline.
    /// </remarks>
    public static IServiceCollection AddHotRodClient(this IServiceCollection services, Action<HotRodClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new HotRodClientBuilder();
        configure(builder);

        services.AddSingleton(serviceProvider =>
        {
            ILoggerFactory loggerFactory = serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            HotRodClientOptions options = builder.Build(loggerFactory);
            return HotRodClient.ConnectAsync(options).AsTask().GetAwaiter().GetResult();
        });

        return services;
    }
}
