using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins down how a response status byte is interpreted. The "with previous" and object-storage
/// variants are easy to miss, so each predicate is checked across the whole relevant set.
/// </summary>
public class ResponseStatusTests
{
    [Theory]
    [InlineData(Constants.StatusSuccess)]
    [InlineData(Constants.StatusSuccessObj)]
    [InlineData(Constants.StatusSuccessWithPrevious)]
    [InlineData(Constants.StatusSuccessWithPreviousObj)]
    public void IsSuccess_covers_every_success_code(byte status) =>
        Assert.True(ResponseStatus.IsSuccess(status));

    [Theory]
    [InlineData(Constants.StatusNotExecuted)]
    [InlineData(Constants.StatusKeyDoesNotExist)]
    [InlineData(Constants.StatusServerError)]
    public void IsSuccess_is_false_for_non_success_codes(byte status) =>
        Assert.False(ResponseStatus.IsSuccess(status));

    [Theory]
    [InlineData(Constants.StatusNotExecuted)]
    [InlineData(Constants.StatusNotExecutedWithPrevious)]
    [InlineData(Constants.StatusNotExecutedWithPreviousObj)]
    public void IsNotExecuted_covers_every_not_executed_code(byte status) =>
        Assert.True(ResponseStatus.IsNotExecuted(status));

    [Theory]
    [InlineData(Constants.StatusSuccessWithPrevious)]
    [InlineData(Constants.StatusSuccessWithPreviousObj)]
    [InlineData(Constants.StatusNotExecutedWithPrevious)]
    [InlineData(Constants.StatusNotExecutedWithPreviousObj)]
    public void HasPrevious_is_true_only_for_with_previous_codes(byte status) =>
        Assert.True(ResponseStatus.HasPrevious(status));

    [Theory]
    [InlineData(Constants.StatusSuccess)]
    [InlineData(Constants.StatusNotExecuted)]
    [InlineData(Constants.StatusKeyDoesNotExist)]
    public void HasPrevious_is_false_without_a_previous_value(byte status) =>
        Assert.False(ResponseStatus.HasPrevious(status));

    [Fact]
    public void KeyDoesNotExist_matches_only_its_code()
    {
        Assert.True(ResponseStatus.KeyDoesNotExist(Constants.StatusKeyDoesNotExist));
        Assert.False(ResponseStatus.KeyDoesNotExist(Constants.StatusSuccess));
    }

    [Theory]
    [InlineData(Constants.StatusInvalidMagicOrMessageId, true)]
    [InlineData(Constants.StatusServerError, true)]
    [InlineData(Constants.StatusCommandTimeout, true)]
    [InlineData(Constants.StatusIllegalLifecycleState, true)]
    [InlineData(Constants.StatusSuccess, false)]
    [InlineData(Constants.StatusNotExecuted, false)]
    [InlineData(Constants.StatusKeyDoesNotExist, false)]
    public void IsError_holds_above_the_error_threshold(byte status, bool expected) =>
        Assert.Equal(expected, ResponseStatus.IsError(status));
}
