using Hall9k.Cli.Infrastructure;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// An <see cref="IInteractiveConfirmation"/> a test drives by hand — the seam
/// <c>OwnerPromoteCommand</c> uses in place of the house <c>AnsiConsole.Profile.Capabilities.Interactive</c>
/// check and <c>AnsiConsole.Confirm</c>, neither of which a test can otherwise control without a
/// real terminal (a pty fakes the former; nothing in this codebase drives the latter without
/// blocking on real stdin).
/// <para>
/// <paramref name="onConfirm"/> is the seam for a test that needs to act as though the person being
/// asked took a while to answer: it runs before <see cref="Confirm"/> returns, so a caller can use
/// it to run something else against the same store first — standing in for a second invocation
/// racing ahead while an interactive prompt here is still waiting on a real person.
/// </para>
/// </summary>
internal sealed class FakeInteractiveConfirmation(bool isInteractive, bool confirmResult, Action? onConfirm = null)
    : IInteractiveConfirmation
{
    public bool IsInteractive { get; } = isInteractive;

    public int ConfirmCalls { get; private set; }

    public bool Confirm(string prompt, bool defaultValue)
    {
        ConfirmCalls++;
        onConfirm?.Invoke();
        return confirmResult;
    }
}
