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
    /// The one-line h9k status row for an outstanding request — names <c>--force</c> as the way on
    /// once it is overdue, and the grant/refuse levers otherwise.
    /// </summary>
    public static string ComposeStatusLine(
        string id, string objective, string requesterLabel, string reason, bool overdue, int timeoutMinutes) =>
        overdue
            ? $"[red bold]Take timed out[/] {id} {objective} [dim]— no answer from {requesterLabel} after "
                + $"{timeoutMinutes} minute(s)[/] [dim]→[/] h9k task take {id} --force --reason \"<why>\""
            : $"[red bold]Take requested[/] {id} {objective} [dim]— {requesterLabel} asks: {reason}[/] "
                + $"[dim]→[/] h9k task grant {id} / h9k task refuse {id} --reason \"<why>\"";

    /// <summary>The h9k task show line for the same overdue fact, on a task's own detail page.</summary>
    public static string ComposeTaskShowLine(string id, bool overdue, int timeoutMinutes) =>
        overdue
            ? $"  [red]No answer within {timeoutMinutes} minute(s)[/] — the holder answers with "
                + $"h9k task grant {id} / h9k task refuse {id} --reason, or the requester takes over with "
                + $"h9k task take {id} --force --reason \"<why>\"."
            : $"  [dim]The holder answers with h9k task grant {id} / h9k task refuse {id} --reason.[/]";
}
