namespace HotRod.Client.Protocol;

/// <summary>
/// Interprets a HotRod response status byte. Caches with object storage return a parallel set of
/// success codes (0x06–0x08), so each check covers both the normal and the object-storage variant.
/// </summary>
internal static class ResponseStatus
{
    public static bool IsSuccess(byte status) =>
        status is Constants.StatusSuccess or Constants.StatusSuccessObj
                or Constants.StatusSuccessWithPrevious or Constants.StatusSuccessWithPreviousObj;

    public static bool IsNotExecuted(byte status) =>
        status is Constants.StatusNotExecuted
                or Constants.StatusNotExecutedWithPrevious or Constants.StatusNotExecutedWithPreviousObj;

    /// <summary>True when the response body carries a previous/current value (the "with previous" codes).</summary>
    public static bool HasPrevious(byte status) =>
        status is Constants.StatusSuccessWithPrevious or Constants.StatusSuccessWithPreviousObj
                or Constants.StatusNotExecutedWithPrevious or Constants.StatusNotExecutedWithPreviousObj;

    public static bool KeyDoesNotExist(byte status) => status == Constants.StatusKeyDoesNotExist;

    public static bool IsError(byte status) => status >= Constants.StatusErrorThreshold;
}
