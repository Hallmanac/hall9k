using Hall9k.Cli.Infrastructure;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// An <see cref="IInteractiveConfirmation"/> a test drives by hand — the seam
/// <c>OwnerPromoteCommand</c> uses in place of the house <c>AnsiConsole.Profile.Capabilities.Interactive</c>
/// check and <c>AnsiConsole.Confirm</c>, neither of which a test can otherwise control without a
/// real terminal (a pty fakes the former; nothing in this codebase drives the latter without
/// blocking on real stdin).
/// </summary>
internal sealed class FakeInteractiveConfirmation(bool isInteractive, bool confirmResult) : IInteractiveConfirmation
{
    public bool IsInteractive { get; } = isInteractive;

    public int ConfirmCalls { get; private set; }

    public bool Confirm(string prompt, bool defaultValue)
    {
        ConfirmCalls++;
        return confirmResult;
    }
}
