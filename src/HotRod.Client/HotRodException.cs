using HotRod.Client.Protocol;

namespace HotRod.Client;

/// <summary>Raised on protocol errors (bad magic, truncated stream) or server-reported error statuses.</summary>
public class HotRodException : Exception
{
    /// <summary>The HotRod status byte, when the error originated from a server response.</summary>
    public byte? Status { get; }

    public HotRodException(string message) : base(message) { }

    public HotRodException(string message, byte status) : base(message) => Status = status;

    public HotRodException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>An error reported by the server (status ≥ 0x80), carrying the status and server message.</summary>
public class HotRodServerException : HotRodException
{
    public HotRodServerException(string message, byte status) : base(message, status) { }

    /// <summary>Builds the most specific exception for a server error status.</summary>
    internal static HotRodServerException Create(byte status, string serverMessage)
    {
        string message = $"Server error (status 0x{status:X2}, {Describe(status)}): {serverMessage}";
        return status == Constants.StatusCommandTimeout
            ? new HotRodTimeoutException(message, status)
            : new HotRodServerException(message, status);
    }

    private static string Describe(byte status) => status switch
    {
        Constants.StatusInvalidMagicOrMessageId => "invalid magic or message id",
        Constants.StatusUnknownCommand => "unknown command",
        Constants.StatusUnknownVersion => "unknown version",
        Constants.StatusRequestParsingError => "request parsing error",
        Constants.StatusServerError => "server error",
        Constants.StatusCommandTimeout => "command timeout",
        Constants.StatusNodeSuspected => "node suspected",
        Constants.StatusIllegalLifecycleState => "illegal lifecycle state",
        _ => "unknown",
    };
}

/// <summary>The server timed out executing the operation (status 0x86) — typically worth retrying.</summary>
public sealed class HotRodTimeoutException : HotRodServerException
{
    public HotRodTimeoutException(string message, byte status) : base(message, status) { }
}
