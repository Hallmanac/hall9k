namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// What GitHub says about one issue's own state at closeout time — open or closed, and the
/// labels it carries — or why that could not be read (task: a task's linked GitHub issue is
/// closed at true closeout under a configurable rule). The same two-outcome shape
/// <see cref="TrackerAssigneeRead"/> already uses for a closeout-time gh read: only a failed
/// read is an error, and the one caller weighing a close decides what an unreadable answer
/// means for it (leave the issue alone) rather than unwinding a stack — closeout itself must
/// never fail or retry on account of an issue it cannot read.
/// </summary>
public sealed record GitHubIssueCloseoutRead(bool IsOpen, IReadOnlyList<string> Labels, string? Error = null)
{
    /// <summary>The issue was read: its current open/closed state and the labels it carries right now.</summary>
    public static GitHubIssueCloseoutRead Found(bool isOpen, IReadOnlyList<string> labels) => new(isOpen, labels);

    /// <summary>gh could not say — missing, unreadable, or the tool itself failed. This one caller's whole remedy is to leave the issue alone.</summary>
    public static GitHubIssueCloseoutRead Unreadable(string error) => new(false, [], error);

    /// <summary>Whether this read failed rather than answered.</summary>
    public bool Failed => Error is not null;
}
