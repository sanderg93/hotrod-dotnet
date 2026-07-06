using HotRod.Client.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the structured log events the client emits at its key lifecycle points: each source-generated
/// <see cref="Log"/> method emits a single entry carrying the right <see cref="HotRodEventIds"/> id,
/// severity, rendered message and (for failures) exception. Also confirms logging is off by default.
/// </summary>
public class LoggingTests
{
    private static RecordingLogger.Entry Single(Action<RecordingLogger> emit)
    {
        var logger = new RecordingLogger();
        emit(logger);
        return Assert.Single(logger.Entries);
    }

    [Fact]
    public void Logging_is_disabled_by_default()
    {
        var options = new HotRodClientOptions();
        Assert.Same(NullLoggerFactory.Instance, options.LoggerFactory);
    }

    [Fact]
    public void ConnectionOpened_logs_the_endpoint_at_debug()
    {
        RecordingLogger.Entry entry = Single(l => Log.ConnectionOpened(l, "node-a", 11222));

        Assert.Equal(HotRodEventIds.ConnectionOpened, entry.EventId.Id);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("node-a:11222", entry.Message);
    }

    [Fact]
    public void ConnectionClosed_logs_the_endpoint_at_debug()
    {
        RecordingLogger.Entry entry = Single(l => Log.ConnectionClosed(l, "node-a", 11222));

        Assert.Equal(HotRodEventIds.ConnectionClosed, entry.EventId.Id);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("node-a:11222", entry.Message);
    }

    [Fact]
    public void AuthenticationSucceeded_logs_user_and_mechanism_at_debug()
    {
        RecordingLogger.Entry entry = Single(l => Log.AuthenticationSucceeded(l, "alice", SaslMechanism.ScramSha256));

        Assert.Equal(HotRodEventIds.AuthenticationSucceeded, entry.EventId.Id);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("alice", entry.Message);
        Assert.Contains("ScramSha256", entry.Message);
    }

    [Fact]
    public void AuthenticationFailed_logs_the_exception_at_error()
    {
        var cause = new HotRodException("bad credentials");
        RecordingLogger.Entry entry = Single(l => Log.AuthenticationFailed(l, "alice", SaslMechanism.Plain, cause));

        Assert.Equal(HotRodEventIds.AuthenticationFailed, entry.EventId.Id);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("alice", entry.Message);
        Assert.Same(cause, entry.Exception);
    }

    [Fact]
    public void TopologyChanged_logs_cache_id_and_node_count_at_information()
    {
        RecordingLogger.Entry entry = Single(l => Log.TopologyChanged(l, "orders", topologyId: 7, nodeCount: 3));

        Assert.Equal(HotRodEventIds.TopologyChanged, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("orders", entry.Message);
        Assert.Contains("7", entry.Message);
        Assert.Contains("3", entry.Message);
    }

    [Fact]
    public void NodeFailover_logs_the_target_node_and_attempt_at_warning()
    {
        RecordingLogger.Entry entry = Single(l => Log.NodeFailover(l, "node-b", 11222, attempt: 2));

        Assert.Equal(HotRodEventIds.NodeFailover, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("node-b:11222", entry.Message);
        Assert.Contains("2", entry.Message);
    }

    [Fact]
    public void PoolExhausted_logs_the_endpoint_and_timeout_at_warning()
    {
        RecordingLogger.Entry entry = Single(l => Log.PoolExhausted(l, "node-c", 11222, TimeSpan.FromSeconds(30)));

        Assert.Equal(HotRodEventIds.PoolExhausted, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("node-c:11222", entry.Message);
    }
}
