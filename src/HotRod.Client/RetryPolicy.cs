using System.IO;
using System.Net.Sockets;
using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>
/// Runs an operation with transparent, bounded retry. HotRod operations are safe to retry, so a
/// failure that leaves the current connection unusable — a dropped socket, a protocol desync, or a
/// server status that means the node is going away — is retried on a fresh attempt rather than being
/// surfaced. Each attempt is told its zero-based index so the caller can spread retries across nodes.
/// After the attempt budget is spent the last failure propagates unchanged.
/// </summary>
internal static class RetryPolicy
{
    /// <summary>
    /// Invokes <paramref name="attempt"/> up to <paramref name="maxAttempts"/> times, retrying while a
    /// failure is retriable and the budget is not yet spent. The final attempt's exception (retriable or
    /// not) propagates, so the caller sees the last error after retries are exhausted.
    /// </summary>
    public static async ValueTask<T> ExecuteAsync<T>(
        int maxAttempts,
        Func<int, CancellationToken, ValueTask<T>> attempt,
        CancellationToken ct)
    {
        Exception? last = null;
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                return await attempt(i, ct);
            }
            catch (Exception ex) when (i + 1 < maxAttempts && IsRetriable(ex))
            {
                last = ex;
            }
        }

        // Reached only if maxAttempts is non-positive; a positive budget always returns or throws above.
        throw last ?? new HotRodException("The operation was not attempted.");
    }

    /// <summary>
    /// Decides whether a failed exchange is worth another attempt on a different connection or node.
    /// Transport failures (dropped socket, stream error) and protocol desyncs fault the connection and
    /// may succeed elsewhere; the node-suspected and illegal-lifecycle-state server statuses mean the
    /// node is unavailable, so another owner is tried. A cancellation, or any other clean server error
    /// (including a command timeout), is left to surface.
    /// </summary>
    public static bool IsRetriable(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        HotRodServerException server =>
            server.Status is Constants.StatusNodeSuspected or Constants.StatusIllegalLifecycleState,
        HotRodException library => library.Status is null,
        IOException => true,
        SocketException => true,
        _ => false,
    };
}
