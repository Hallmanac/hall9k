using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one next act for a task that has not reached a run, and the exits from a Failed one: the
/// text <c>h9k task show</c> prints under its detail and <c>h9k task next</c> prints under its
/// standing. Both commands call this, so the two cannot name different commands for the same
/// state. The lifecycle has several explicit steps (Decisions Log #34), so every state says which
/// one it is waiting for rather than leaving the reader to remember the graph.
/// </summary>
internal static class TaskNextStep
{
    /// <summary>
    /// Whether any dependency still blocks the task from claiming, by the same rule the claim gate
    /// reads: a stacked parent releases its child earlier than a plain edge does
    /// (<see cref="StackedEdgeRules"/>). Fed to <see cref="Lines"/> because an open dependency
    /// changes which flag the interactive-claim hint names.
    /// </summary>
    public static bool HasOpenDependency(TaskDetails details, IReadOnlyList<TaskDependency> dependencies) =>
        dependencies.Any(dependency => StackedEdgeRules.Blocks(details.StackedOnTaskId, dependency));

    /// <summary>
    /// The next-step block and, for a Failed task, the three exits, as Spectre markup lines in print
    /// order. A blank string is a blank line. Empty when the state has no pre-dispatch step and is
    /// not Failed, which is every state where the phase and attention lines already say what moves.
    /// </summary>
    public static IReadOnlyList<string> Lines(TaskDetails details, bool hasOpenDependency)
    {
        List<string> lines = [];
        if (Next(details, hasOpenDependency) is { } next)
        {
            lines.Add(string.Empty);
            lines.Add(next);
        }

        if (details.State == TaskState.Failed)
        {
            string shortId = TaskListCommand.ShortId(details.Id);
            lines.Add(string.Empty);
            lines.Add("[bold]Failed is a waypoint, not an ending — three exits:[/]");
            lines.Add($"  [yellow]retry[/]    h9k task retry {shortId} --reason <why>              — run it again");
            lines.Add($"  [green]resolve[/]  h9k task resolve {shortId} --reason <why> [[--pr <url>]] — the objective was met despite the failure");
            lines.Add($"  [dim]abandon[/]  h9k task abandon {shortId} [[--reason <why>]]          — walk away");
        }

        return lines;
    }

    private static string? Next(TaskDetails details, bool hasOpenDependency)
    {
        string shortId = TaskListCommand.ShortId(details.Id);
        // h9k task drive is refused outright for a pr-review task (TaskDriveCommand.ClaimAndCutAsync:
        // "it has no diff of its own for an interactive session to build"), so the hint is named
        // only where the claim would actually be accepted (DescribeDoneRemedy's own rule,
        // independent pre-PR review, cycle 3). An open dependency no longer suppresses the hint
        // (task 0ac72cb8-h9k): h9k task drive now warns and asks instead of refusing outright, so
        // the hint just names the flag that answers it (adversarial lens, cycle 1).
        string interactiveClaimHint = details.Type == TaskType.PrReview
            ? string.Empty
            : hasOpenDependency
                ? $" (or h9k task drive {shortId} --acknowledge-unmet-dependencies to claim across the open dependency yourself)"
                : $" (or h9k task drive {shortId} to claim and work it yourself)";
        return details.State.Value switch
        {
            "Draft" => details.AcceptanceCriteria.Count == 0
                ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]— publishing needs at least one[/]"
                : $"[dim]Next:[/] h9k task publish {shortId} [dim]then[/] h9k task queue {shortId}",
            "Published" => $"[dim]Next:[/] h9k task queue {shortId} [dim]— it will not run until you do"
                + $"{interactiveClaimHint}[/]",
            "Blocked" => $"[dim]It queues itself when its dependencies close out. To stop waiting:[/] "
                + $"h9k task dequeue {shortId} [dim]→[/] h9k task draft {shortId} [dim]→[/] h9k task revise {shortId} --clear-dependencies"
                + $" [dim]— or claim across the open dependency yourself:[/] h9k task drive {shortId} --acknowledge-unmet-dependencies",
            "Queued" => $"[dim]Waiting for a dispatch cycle on one of the assignee's nodes. To take it back:[/] h9k task dequeue {shortId}",
            // A posted review waiting on its author (task: a pr-review task stays open while the
            // pull request's review threads are unresolved). Nothing is being asked of the
            // reviewer, so the hint says what the platform is doing and names the one lever that
            // stops it — never h9k task resolve, which refuses anything but a Failed task, or
            // h9k review resolve, whose park has already been resolved to get here.
            "AwaitingAuthor" => "[dim]The closeout watcher polls the pull request on its own cadence and flags "
                + "this needs-you the moment its author replies, pushes, re-requests your review, or someone "
                + "mentions this install's own login in a fresh comment on it. It reaches Done only when the "
                + $"pull request itself merges or closes. To stop watching:[/] h9k task abandon {shortId}",
            _ => null,
        };
    }
}
