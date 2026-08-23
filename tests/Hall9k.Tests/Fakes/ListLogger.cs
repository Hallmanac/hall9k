using Microsoft.Extensions.Logging;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Captures every rendered log line so a test can assert on wording — the generation
/// fence's rejection message names both generations, and NullLogger throws that away.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception));
}
