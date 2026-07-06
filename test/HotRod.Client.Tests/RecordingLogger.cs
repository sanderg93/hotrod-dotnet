using Microsoft.Extensions.Logging;

namespace HotRod.Client.Tests;

/// <summary>
/// A minimal <see cref="ILogger"/>/<see cref="ILoggerFactory"/> that captures every emitted entry
/// (level, event id, rendered message, exception) so tests can assert what the client logged.
/// <see cref="ILogger.IsEnabled"/> always returns true so source-generated log methods are not skipped.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public sealed record Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(new Entry(logLevel, eventId, formatter(state, exception), exception));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>An <see cref="ILoggerFactory"/> that hands out a single shared <see cref="RecordingLogger"/>.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public RecordingLogger Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => Logger;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}
