using Microsoft.Extensions.Logging;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Captures every rendered log line so a test can assert on wording — the generation
/// fence's rejection message names both generations, and NullLogger throws that away.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    /// <summary>
    /// The same lines with their levels, for an assertion about the level itself rather than the
    /// wording — auto-pr-review's own "exactly one Info line per pull request" rule (Decisions Log
    /// #161) is a claim about Info specifically, and the Debug lines beside it are not the ones an
    /// orchestrator window's log tail reads.
    /// </summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> InformationLines =>
        Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        Lines.Add(message);
        Entries.Add((logLevel, message));
    }
}
