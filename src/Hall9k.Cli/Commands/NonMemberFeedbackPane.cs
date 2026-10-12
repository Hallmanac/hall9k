using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The needs-you list for feedback from someone outside the project's roster (task: a
/// changes-requested review or an unresolved review thread from a non-member never dispatches a build
/// lap; it raises a needs-you item). One line per review or thread, undecided only, each naming the
/// reviewer's login, what kind of feedback it is, and its link, and quoting none of it.
/// <para>
/// A list of its own rather than a cause on the task's row, on the precedent
/// <see cref="ReviewRequestPane"/> sets and for the reason the row cannot carry it: a task row has one
/// attention cause and a running lap's state replaces it, so several items on one task, or an item
/// beside a member's running lap, would otherwise show as the lap alone. Each item clears the moment
/// the owner answers it with <c>h9k pr feedback</c>, or GitHub no longer has the feedback.
/// </para>
/// </summary>
internal static class NonMemberFeedbackPane
{
    /// <summary>
    /// The line an item is rendered as, with the lever that answers it. The login and the link are the
    /// reviewer's and are one-lined and escaped; nothing else of theirs is here to print.
    /// </summary>
    internal static string ComposeLine(string shortId, string objective, NonMemberFeedbackItem item)
    {
        string key = ExternalText.OneLineMarkup(item.Key);
        return $"[red bold]NEEDS YOU[/] [red]{shortId.EscapeMarkup()} {ExternalText.OneLineMarkup(objective)}[/] "
            + $"[dim]— {Describe(item)}, who is not in this project's roster, has not been read by any lap.[/] "
            + $"[dim]Decide with:[/] h9k pr feedback {shortId.EscapeMarkup()} \"{key}\" --go [dim]or[/] --decline";
    }

    /// <summary>
    /// Who, what kind, and where, as markup-safe text with no word of the feedback itself, then what
    /// the evaluator made of it: only its recommendation word and the path of the assessment to read,
    /// never a word of the assessment, since the orchestrator window reads this with permissions
    /// bypassed. An evaluation that produced no assessment says so, and the item reads as it always has.
    /// </summary>
    internal static string Describe(NonMemberFeedbackItem item)
    {
        string described =
            $"{item.Kind.Noun} from @{ExternalText.OneLineMarkup(item.Reviewer)} {ExternalText.OneLineMarkup(item.Url)}";
        FeedbackEvaluation evaluation = item.EvaluationState;
        string? evaluator = evaluation.Status.Value switch
        {
            "Running" => "an evaluator is assessing it",
            "Assessed" when evaluation.Recommendation is { IsRecognized: true } recommendation =>
                $"evaluator recommends {recommendation.Value}; read the assessment at {ExternalText.OneLineMarkup(evaluation.AssessmentPath ?? "")}",
            "Failed" => $"no assessment: {ExternalText.OneLineMarkup(evaluation.Reason ?? "the evaluator did not produce one")}",
            "PastCap" => $"no evaluator ran: {ExternalText.OneLineMarkup(evaluation.Reason ?? "the task is past its evaluator allowance")}",
            _ => null,
        };
        return evaluator is null ? described : $"{described} [dim]({evaluator})[/]";
    }

    /// <summary>
    /// The block <c>h9k task show</c> prints for one task: every piece of non-member feedback it holds,
    /// whatever its status, oldest first, with the lever for the ones still waiting. Empty when the
    /// task holds none, so a task nobody outside the roster has reviewed shows nothing new.
    /// </summary>
    internal static IReadOnlyList<string> ComposeTaskShowLines(TaskDetails task)
    {
        IReadOnlyList<NonMemberFeedbackItem> items = [.. task.NonMemberFeedback.OrderBy(item => item.ObservedAt)];
        if (items.Count == 0)
        {
            return [];
        }

        string shortId = TaskListCommand.ShortId(task.Id);
        return
        [
            "",
            "[bold]Feedback from outside the project's roster[/] [dim](held for your go; none of its text is shown)[/]",
            .. items.Select(item => item.Status.NeedsOwner
                ? $"  [red]undecided[/] {Describe(item)} [dim]→[/] h9k pr feedback {shortId.EscapeMarkup()} \"{ExternalText.OneLineMarkup(item.Key)}\" --go [dim]or[/] --decline"
                : $"  [dim]{item.Status.Value.ToLowerInvariant().EscapeMarkup()}[/] {Describe(item)}"),
        ];
    }

    /// <summary>
    /// Every undecided item on the viewer's own live tasks, oldest first. A teammate's task is skipped
    /// (nothing here is theirs to answer), and so is one whose pull request has merged, since feedback
    /// on a merged pull request asks nothing of anyone.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ComposeAllAsync(
        IQuerySession session, IReadOnlyList<TaskStatusRow> rows, CancellationToken cancellationToken)
    {
        Dictionary<Guid, TaskStatusRow> rowsByTask = rows.ToDictionary(row => row.TaskId);
        IReadOnlyList<TaskDetails> candidates = await session.Query<TaskDetails>()
            .Where(task => task.HasUndecidedNonMemberFeedback)
            .ToListAsync(cancellationToken);

        List<(DateTimeOffset ObservedAt, string Line)> lines = [];
        foreach (TaskDetails task in candidates)
        {
            if (!rowsByTask.TryGetValue(task.Id, out TaskStatusRow? row)
                || row.IsTeammates
                || row.Group == AttentionBucket.Done)
            {
                continue;
            }

            foreach (NonMemberFeedbackItem item in task.NonMemberFeedback.Where(item => item.Status.NeedsOwner))
            {
                lines.Add((item.ObservedAt, ComposeLine(TaskListCommand.ShortId(task.Id), row.Objective, item)));
            }
        }

        return [.. lines.OrderBy(line => line.ObservedAt).Select(line => line.Line)];
    }
}
