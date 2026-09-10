using Microsoft.Extensions.Logging;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Captures every rendered log line so a test can assert on wording — the generation
/// fence's rejection message names both generations, and NullLogger throws that away.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly object _gate = new();

    // A snapshot rather than the live list: the logger this backs is often still being
    // written by a background monitor task while a test reads Lines, and enumerating a
    // plain List<string> concurrently with an Add throws (a completion log line can land
    // mid-assertion — origin: PR review on task a5f07d69).
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) { return [.. _lines]; } }
    }

    /// <summary>
    /// The same lines with their levels, for an assertion about the level itself rather than the
    /// wording — auto-pr-review's own "exactly one Info line per pull request" rule (Decisions Log
    /// #161) is a claim about Info specifically, and the Debug lines beside it are not the ones an
    /// orchestrator window's log tail reads.
    /// </summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) { return [.. _entries]; } }
    }

    public IEnumerable<string> InformationLines =>
        Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        lock (_gate)
        {
            _lines.Add(message);
            _entries.Add((logLevel, message));
        }
    }
}
