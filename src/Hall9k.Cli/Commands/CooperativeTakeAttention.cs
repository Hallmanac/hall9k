namespace Hall9k.Cli.Commands;

/// <summary>
/// Pure display logic for an outstanding cooperative take request (idea 202383dc, item 5) —
/// factored out of <see cref="StatusCommand"/>/<see cref="TaskShowCommand"/> so the overdue
/// computation and its wording are tested directly rather than only through a full command's own
/// console output, the same <c>ComposePassageLines</c> idiom <see cref="TaskShowCommand"/> already
/// uses for its own passage rendering.
/// </summary>
internal static class CooperativeTakeAttention
{
    /// <summary>Whether a request sent at <paramref name="requestedAt"/> has gone unanswered past the project's own take-timeout.</summary>
    public static bool IsOverdue(DateTimeOffset requestedAt, int timeoutMinutes, DateTimeOffset now) =>
        now - requestedAt > TimeSpan.FromMinutes(timeoutMinutes);

    /// <summary>
    /// The one-line h9k status row for an outstanding request — scoped to the one node that reads
    /// it (the caller only ever composes this for the holder or the requester, never a third node
    /// that merely replicated the same <c>TaskTakeRequested</c>, independent pre-PR review, cycle
    /// 1, adversarial lens), and worded for that side alone: the holder always gets the grant/
    /// refuse levers, since only the holder can pull them, and the requester gets <c>--force</c>
    /// only once overdue — never a lever that would grant the requester's own request, and never a
    /// line blaming the requester for the holder's own silence.
    /// </summary>
    public static string ComposeStatusLine(
        string id, string objective, string counterpartLabel, string reason, bool isHolder, bool overdue,
        int timeoutMinutes) =>
        isHolder
            ? $"[red bold]Take requested[/] {id} {objective} [dim]— {counterpartLabel} asks: {reason}"
                + (overdue ? $" (no answer for {timeoutMinutes} minute(s) already)" : string.Empty) + "[/] "
                + $"[dim]→[/] h9k task grant {id} / h9k task refuse {id} --reason \"<why>\""
            : overdue
                ? $"[red bold]Take timed out[/] {id} {objective} [dim]— no answer from {counterpartLabel} after "
                    + $"{timeoutMinutes} minute(s)[/] [dim]→[/] h9k task take {id} --force --reason \"<why>\""
                : $"[red bold]Take requested[/] {id} {objective} [dim]— waiting on {counterpartLabel} to answer: "
                    + $"{reason}[/]";

    /// <summary>The h9k task show line for the same overdue fact, on a task's own detail page.</summary>
    public static string ComposeTaskShowLine(string id, bool overdue, int timeoutMinutes) =>
        overdue
            ? $"  [red]No answer within {timeoutMinutes} minute(s)[/] — the holder answers with "
                + $"h9k task grant {id} / h9k task refuse {id} --reason, or the requester takes over with "
                + $"h9k task take {id} --force --reason \"<why>\"."
            : $"  [dim]The holder answers with h9k task grant {id} / h9k task refuse {id} --reason.[/]";
}
