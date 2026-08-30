using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Writes <see cref="GitWorktreeManager"/>'s own log lines to the operator's terminal instead of
/// discarding them. The daemon's <c>GitWorktreeManager</c> logs to its structured daemon log,
/// where a human isn't watching; <c>h9k task work</c> is the one caller where a human is, and the
/// cross-process lock's periodic "waiting on another h9k process" line exists specifically for
/// that case (GitWorktreeManager.cs's own doc comment) — a <c>NullLogger</c> there discards the
/// exact line it was added to show (conformance + adversarial review, cycle 1).
/// </summary>
public sealed class ConsoleWorktreeLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]{formatter(state, exception)}[/]");
    }
}
