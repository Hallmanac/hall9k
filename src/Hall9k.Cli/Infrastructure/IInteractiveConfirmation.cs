using Spectre.Console;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Seams the house interactive-confirmation check — <c>AnsiConsole.Profile.Capabilities.Interactive</c>
/// plus <c>AnsiConsole.Confirm</c>, the pattern <c>ProjectRemoveCommand</c> and <c>TaskPublishCommand</c>
/// already use inline — the way <c>NodeVouchCommand</c> seams <c>ILedger</c> and <c>NodeKeyStore</c>,
/// so a command whose own confirmation is worth pinning in a test (idea 6be68ee2's <c>h9k owner
/// promote</c>, a deliberate, loud act with no automatic retry) can drive it against a fake rather
/// than a real terminal. Neither half of the house check is itself testable any other way: a pty
/// fakes <c>Interactive</c>, and nothing in this codebase drives <c>AnsiConsole.Confirm</c> without
/// blocking on real stdin.
/// </summary>
public interface IInteractiveConfirmation
{
    /// <summary>The house check: whether this session has a terminal to prompt on at all.</summary>
    bool IsInteractive { get; }

    /// <summary>Asks <paramref name="prompt"/>, defaulting to <paramref name="defaultValue"/> on a bare Enter.</summary>
    bool Confirm(string prompt, bool defaultValue);
}

/// <summary>The real implementation, over <see cref="AnsiConsole"/> itself.</summary>
public sealed class ConsoleInteractiveConfirmation : IInteractiveConfirmation
{
    public bool IsInteractive => AnsiConsole.Profile.Capabilities.Interactive;

    public bool Confirm(string prompt, bool defaultValue) => AnsiConsole.Confirm(prompt, defaultValue);
}
