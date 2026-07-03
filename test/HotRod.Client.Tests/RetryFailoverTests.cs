using System.IO;
using System.Net.Sockets;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins down the transparent retry/failover policy at the execution chokepoint: a retriable failure
/// (a dropped connection, a protocol desync, or a node-unavailable server status) is retried on a
/// fresh attempt, while the last error surfaces once the attempt budget is spent and non-retriable
/// failures are never retried. The attempt delegate stands in for "borrow a connection and run the
/// exchange", so this exercises the same policy every cache operation flows through.
/// </summary>
public class RetryFailoverTests
{
    // maxAttempts = MaxRetries + 1, so this mirrors a default-ish budget of two retries.
    private const int MaxAttempts = 3;

    [Fact]
    public async Task Retries_after_a_connection_failure_and_ultimately_succeeds()
    {
        var attempt = Substitute.For<Func<int, CancellationToken, ValueTask<string>>>();
        attempt(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string>>(
                _ => throw new IOException("connection reset"), // first attempt: the connection dies mid-flight
                _ => ValueTask.FromResult("ok"));               // second attempt: a fresh connection succeeds

        string result = await RetryPolicy.ExecuteAsync(MaxAttempts, attempt, CancellationToken.None);

        Assert.Equal("ok", result);
        await attempt.Received(2).Invoke(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Spreads_retries_by_passing_the_incrementing_attempt_index()
    {
        var seen = new List<int>();

        string result = await RetryPolicy.ExecuteAsync<string>(
            MaxAttempts,
            (i, _) =>
            {
                seen.Add(i);
                return i < 2 ? throw new SocketException() : ValueTask.FromResult("ok");
            },
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(new[] { 0, 1, 2 }, seen); // the caller uses the index to fail over to another node
    }

    [Fact]
    public async Task Gives_up_with_the_last_exception_once_retries_are_exhausted()
    {
        var attempt = Substitute.For<Func<int, CancellationToken, ValueTask<string>>>();
        var last = new IOException("still down");
        attempt(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string>>(
                _ => throw new IOException("down"),
                _ => throw new IOException("still down again"),
                _ => throw last);

        IOException thrown = await Assert.ThrowsAsync<IOException>(
            () => RetryPolicy.ExecuteAsync(MaxAttempts, attempt, CancellationToken.None).AsTask());

        Assert.Same(last, thrown); // the final attempt's error is surfaced unchanged
        await attempt.Received(MaxAttempts).Invoke(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_retry_a_non_retriable_server_error()
    {
        var attempt = Substitute.For<Func<int, CancellationToken, ValueTask<string>>>();
        var serverError = HotRodServerException.Create(Constants.StatusServerError, "boom");
        attempt(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns<ValueTask<string>>(_ => throw serverError);

        HotRodServerException thrown = await Assert.ThrowsAsync<HotRodServerException>(
            () => RetryPolicy.ExecuteAsync(MaxAttempts, attempt, CancellationToken.None).AsTask());

        Assert.Same(serverError, thrown);
        await attempt.Received(1).Invoke(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_retry_a_caller_cancellation()
    {
        var attempt = Substitute.For<Func<int, CancellationToken, ValueTask<string>>>();
        attempt(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string>>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RetryPolicy.ExecuteAsync(MaxAttempts, attempt, CancellationToken.None).AsTask());

        await attempt.Received(1).Invoke(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Constants.StatusNodeSuspected, true)]
    [InlineData(Constants.StatusIllegalLifecycleState, true)]
    [InlineData(Constants.StatusServerError, false)]
    [InlineData(Constants.StatusCommandTimeout, false)] // a command timeout is a real result, not a transport fault
    public void IsRetriable_classifies_server_statuses(byte status, bool expected) =>
        Assert.Equal(expected, RetryPolicy.IsRetriable(HotRodServerException.Create(status, "msg")));

    [Fact]
    public void IsRetriable_covers_transport_and_protocol_failures_but_not_cancellation()
    {
        Assert.True(RetryPolicy.IsRetriable(new IOException()));
        Assert.True(RetryPolicy.IsRetriable(new SocketException()));
        Assert.True(RetryPolicy.IsRetriable(new HotRodException("protocol desync"))); // no server status
        Assert.False(RetryPolicy.IsRetriable(new OperationCanceledException()));
        Assert.False(RetryPolicy.IsRetriable(new InvalidOperationException()));
    }
}
