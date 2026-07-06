using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// Runtime administration of the server's caches over HotRod: create a cache from a named template or
/// an inline configuration, create-or-get one idempotently, remove one, and enumerate or test for
/// caches by name. Each operation is a server administration task (for example "@@cache@create")
/// invoked through the EXEC opcode with string parameters; the operations are not cache-scoped and
/// reuse the client's connection pool and topology handling. The manager is stateless and cheap;
/// obtain it from <see cref="HotRodClient.Administration"/>.
/// </summary>
public sealed class AdminManager
{
    private const string CreateCacheTask = "@@cache@create";
    private const string GetOrCreateCacheTask = "@@cache@getorcreate";
    private const string RemoveCacheTask = "@@cache@remove";
    private const string CacheNamesTask = "@@cache@names";

    private const string NameParameter = "name";
    private const string TemplateParameter = "template";
    private const string ConfigurationParameter = "configuration";
    private const string FlagsParameter = "flags";

    private readonly HotRodClient _client;
    private readonly AdminFlags _flags;

    internal AdminManager(HotRodClient client) : this(client, AdminFlags.None) { }

    private AdminManager(HotRodClient client, AdminFlags flags)
    {
        _client = client;
        _flags = flags;
    }

    /// <summary>
    /// Returns a manager that applies <paramref name="flags"/> to every administration operation it
    /// issues. Flags accumulate with any already set on this manager.
    /// </summary>
    public AdminManager WithFlags(AdminFlags flags) => new(_client, _flags | flags);

    /// <summary>
    /// Creates a cache named <paramref name="name"/> from the server-side template
    /// <paramref name="template"/> (for example one of the <see cref="DefaultTemplate"/> names). Throws
    /// if a cache already exists under that name.
    /// </summary>
    public ValueTask CreateCacheFromTemplateAsync(string name, string template, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(template);
        return RunCacheTaskAsync(CreateCacheTask, name, TemplateParameter, template, ct);
    }

    /// <summary>
    /// Creates a cache named <paramref name="name"/> from an inline <paramref name="configuration"/>
    /// (an XML, JSON, or YAML cache definition). Throws if a cache already exists under that name.
    /// </summary>
    public ValueTask CreateCacheFromConfigurationAsync(string name, string configuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        return RunCacheTaskAsync(CreateCacheTask, name, ConfigurationParameter, configuration, ct);
    }

    /// <summary>
    /// Returns the cache named <paramref name="name"/>, creating it from the server-side template
    /// <paramref name="template"/> if it does not yet exist. Existing caches are left unchanged.
    /// </summary>
    public ValueTask GetOrCreateCacheFromTemplateAsync(string name, string template, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(template);
        return RunCacheTaskAsync(GetOrCreateCacheTask, name, TemplateParameter, template, ct);
    }

    /// <summary>
    /// Returns the cache named <paramref name="name"/>, creating it from an inline
    /// <paramref name="configuration"/> if it does not yet exist. Existing caches are left unchanged.
    /// </summary>
    public ValueTask GetOrCreateCacheFromConfigurationAsync(string name, string configuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        return RunCacheTaskAsync(GetOrCreateCacheTask, name, ConfigurationParameter, configuration, ct);
    }

    /// <summary>Removes the cache named <paramref name="name"/> cluster-wide; a no-op if it does not exist.</summary>
    public ValueTask RemoveCacheAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return RunCacheTaskAsync(RemoveCacheTask, name, definitionKey: null, definitionValue: null, ct);
    }

    /// <summary>Returns the names of every cache defined on the server.</summary>
    public async ValueTask<IReadOnlyCollection<string>> GetCacheNamesAsync(CancellationToken ct = default)
    {
        byte[] result = await ScriptExecutor.ExecuteAsync(
            _client, string.Empty, CacheNamesTask, EmptyParameters, routingKey: null, ct);
        return AdminCodec.ParseCacheNames(result);
    }

    /// <summary>Returns true if a cache named <paramref name="name"/> is defined on the server.</summary>
    public async ValueTask<bool> CacheExistsAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        IReadOnlyCollection<string> names = await GetCacheNamesAsync(ct);
        return names.Contains(name);
    }

    private async ValueTask RunCacheTaskAsync(string taskName, string cacheName, string? definitionKey, string? definitionValue, CancellationToken ct)
    {
        var parameters = new Dictionary<string, byte[]>(3)
        {
            [NameParameter] = AdminCodec.StringParameter(cacheName),
        };
        if (definitionKey is not null && definitionValue is not null)
            parameters[definitionKey] = AdminCodec.StringParameter(definitionValue);
        if (AdminCodec.EncodeFlags(_flags) is { } encodedFlags)
            parameters[FlagsParameter] = AdminCodec.StringParameter(encodedFlags);

        await ScriptExecutor.ExecuteAsync(_client, string.Empty, taskName, parameters, routingKey: null, ct);
    }

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyParameters = new Dictionary<string, byte[]>();
}

/// <summary>
/// Flags that modify a cache administration operation. Combine with a bitwise OR. None (the default)
/// creates persistent configurations that survive a server restart.
/// </summary>
[Flags]
public enum AdminFlags
{
    /// <summary>No flags: the operation uses the server's default behavior.</summary>
    None = 0,

    /// <summary>The configuration change is not persisted to the server's global state (lost on restart).</summary>
    Volatile = 1,

    /// <summary>If a compatible configuration already exists, update it rather than failing.</summary>
    Update = 2,
}

/// <summary>
/// Inline JSON configurations for Infinispan's built-in default cache types. Pass one to
/// <see cref="AdminManager.CreateCacheFromConfigurationAsync"/> or
/// <see cref="AdminManager.GetOrCreateCacheFromConfigurationAsync"/> to create a cache of that type
/// without a server-side template. These match the configurations the Java client's
/// <c>DefaultTemplate</c> enum carries; a named template (as opposed to these inline configurations)
/// must instead be defined on the server and referenced through the template-based methods.
/// </summary>
public static class DefaultTemplate
{
    /// <summary>A non-clustered, single-node cache.</summary>
    public const string Local = "{\"local-cache\":{\"statistics\":\"true\"}}";

    /// <summary>A replicated cache with synchronous writes.</summary>
    public const string ReplicatedSync = "{\"replicated-cache\":{\"mode\":\"SYNC\",\"statistics\":\"true\"}}";

    /// <summary>A replicated cache with asynchronous writes.</summary>
    public const string ReplicatedAsync = "{\"replicated-cache\":{\"mode\":\"ASYNC\",\"statistics\":\"true\"}}";

    /// <summary>A distributed cache with synchronous writes.</summary>
    public const string DistributedSync = "{\"distributed-cache\":{\"mode\":\"SYNC\",\"statistics\":\"true\"}}";

    /// <summary>A distributed cache with asynchronous writes.</summary>
    public const string DistributedAsync = "{\"distributed-cache\":{\"mode\":\"ASYNC\",\"statistics\":\"true\"}}";

    /// <summary>An invalidation cache with synchronous writes.</summary>
    public const string InvalidationSync = "{\"invalidation-cache\":{\"mode\":\"SYNC\",\"statistics\":\"true\"}}";

    /// <summary>An invalidation cache with asynchronous writes.</summary>
    public const string InvalidationAsync = "{\"invalidation-cache\":{\"mode\":\"ASYNC\",\"statistics\":\"true\"}}";
}
