using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// Runs one server-task (EXEC) request/response exchange and returns its raw result. Both ad-hoc
/// script execution and cache administration are server tasks issued through the single EXEC opcode,
/// so this shared runner backs <see cref="HotRodClient.ExecuteAsync"/> and <see cref="AdminManager"/>.
/// A task with an empty cache name runs cluster-wide (as administration and manager tasks do); a task
/// with a cache name and optional routing key runs in that cache's context (as script execution does).
/// </summary>
internal static class ScriptExecutor
{
    public static ValueTask<byte[]> ExecuteAsync(
        HotRodClient client,
        string cacheName,
        string taskName,
        IReadOnlyDictionary<string, byte[]> parameters,
        byte[]? routingKey,
        CancellationToken ct) =>
        client.ExecuteAsync(
            cacheName,
            Constants.ExecRequest,
            flags: 0,
            routingKey,
            DataFormat.None,
            w => AdminCodec.WriteExecuteRequest(w, taskName, parameters),
            (_, reader, c) => AdminCodec.ReadExecuteResponseAsync(reader, c),
            ct);
}
