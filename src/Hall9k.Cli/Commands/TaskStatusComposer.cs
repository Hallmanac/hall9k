using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one truth about what state a task is in. Every surface that shows a task —
/// h9k status, h9k task list, h9k project list's rollups, h9k project show — composes
/// its rows here rather than re-deriving the rules, so a task never reads as Running on
/// one screen and AwaitingReview on the next.
/// </summary>
internal static class TaskStatusComposer
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromHours(1);

    /// <summary>
    /// Loads every task and composes its display row. The whole table is read because the
    /// rollups count across it; when task volume outgrows that, the bounding that list
    /// commands apply to their output is where server-side paging goes.
    /// </summary>
    public static async Task<IReadOnlyList<TaskStatusRow>> ComposeAllAsync(
        IQuerySession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> projects = (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id, p => p.Name);
        Dictionary<Guid, RunListItem> runs = (await session.Query<RunListItem>().ToListAsync(cancellationToken))
            .ToDictionary(r => r.Id);
        Dictionary<Guid, RunActivity> activity = (await session.Query<RunActivity>().ToListAsync(cancellationToken))
            .ToDictionary(a => a.Id);

        return [.. tasks.Select(task => Compose(task, runs, activity, projects, now))];
    }

    /// <summary>
    /// Display state composition per TASK-MODEL.md §2: the task's work state, refined by
    /// the current run's execution state while claimed. A done task with a PR is in the
    /// closeout phase (log #18/#22): the current run's closeout state refines the
    /// display — AwaitingReview while quiet, ChecksFailing/ReviewPending when observed,
    /// NeedsHuman when parked, Done once the merge landed. A queued/claimed task that
    /// still carries a PR URL is a follow-up run in flight: ClosingOut.
    /// </summary>
    public static TaskStatusRow Compose(
        TaskListItem task,
        IReadOnlyDictionary<Guid, RunListItem> runs,
        IReadOnlyDictionary<Guid, RunActivity> activity,
        IReadOnlyDictionary<Guid, string> projects,
        DateTimeOffset now)
    {
        RunListItem? run = task.CurrentRunId is { } runId ? runs.GetValueOrDefault(runId) : null;
        bool inCloseout = task.PullRequestUrl.IsNotBlank();

        string bucket = task.State.Value switch
        {
            // A review-parked run outranks the closeout composition: the loop handed
            // the diff to the human before any pull request could open (log #24).
            "Claimed" when run?.State == RunState.ReviewParked => "NeedsHuman",
            "Queued" when inCloseout => "ClosingOut",
            "Claimed" when inCloseout => "ClosingOut",
            "Claimed" when run is not null => run.State.Value,
            "Done" when inCloseout => run?.State.Value switch
            {
                "ChecksFailing" => "ChecksFailing",
                "ReviewPending" => "ReviewPending",
                "CloseoutParked" => "NeedsHuman",
                // Completed = merged; Failed = PR closed without merge. Closeout is over either way.
                "Completed" or "Failed" => "Done",
                _ => "AwaitingReview",
            },
            _ => task.State.Value,
        };

        bool bucketIsLive = bucket is "Running" or "Verifying" or "UnderReview" or "Dispatched"
            || (bucket == "ClosingOut" && run is not null && run.State.IsLive);
        bool stalled = false;
        string activityText = string.Empty;
        if (bucketIsLive && run is not null && activity.TryGetValue(run.Id, out RunActivity? runActivity))
        {
            TimeSpan silence = now - runActivity.LastActivityAt;
            stalled = silence > StallThreshold;
            activityText = RelativeAge(silence);
        }

        return new TaskStatusRow(
            task.Id,
            task.ProjectId,
            bucket,
            StatusMarkup(bucket, stalled),
            Attention(bucket, stalled),
            projects.GetValueOrDefault(task.ProjectId) ?? "?",
            task.Objective,
            task.Type.Value,
            activityText,
            task.PullRequestUrl ?? string.Empty,
            stalled,
            Priority(bucket, stalled),
            task.AddedAt);
    }

    /// <summary>The composed bucket, coloured; a live bucket gone quiet says so loudly.</summary>
    private static string StatusMarkup(string bucket, bool stalled) => bucket switch
    {
        "NeedsHuman" => "[red bold]NeedsHuman[/]",
        "AwaitingReview" => "[magenta]AwaitingReview[/]",
        "ChecksFailing" => "[red]ChecksFailing[/]",
        "ReviewPending" => "[magenta]ReviewPending[/]",
        "Running" or "Verifying" or "UnderReview" or "Dispatched" or "ClosingOut" or "Claimed" => stalled
            ? $"[red]{bucket} ⚠ STALLED[/]"
            : $"[yellow]{bucket}[/]",
        "Queued" => "[blue]Queued[/]",
        "Done" => "[green]Done[/]",
        "Failed" => "[red]Failed[/]",
        "Abandoned" => "[dim]Abandoned[/]",
        _ => bucket.EscapeMarkup(),
    };

    /// <summary>
    /// Attention-first ordering: what needs a human, then what has gone quiet, then what
    /// is working, then what is waiting on someone else, then the settled rows.
    /// </summary>
    private static int Priority(string bucket, bool stalled) => bucket switch
    {
        "NeedsHuman" => 0,
        // A failed task waits for a human decision (retry, resolve, abandon — log #27):
        // it ranks with stalled work, right under the explicit NeedsHuman parks.
        "Failed" => 1,
        _ when stalled => 1,
        "Running" or "Verifying" or "UnderReview" or "Dispatched" or "ClosingOut" => 2,
        // The dispatch handoff ranks with the work it is becoming, not with the settled rows.
        "Claimed" or "Completed" or "Killed" or "Superseded" => 2,
        "AwaitingReview" or "ChecksFailing" or "ReviewPending" => 3,
        "Queued" => 4,
        "Done" => 6,
        _ => 7,
    };

    /// <summary>
    /// The coarse bucket the rollups count and h9k status groups by. Single assignment on
    /// purpose: every task lands in exactly one, so a project's counts sum to its tasks.
    /// </summary>
    private static AttentionBucket Attention(string bucket, bool stalled) => bucket switch
    {
        // Failed is a needs-human waypoint (Decisions Log #27): it waits for retry,
        // resolve, or abandon, so it counts toward the attention surface.
        "NeedsHuman" or "Failed" => AttentionBucket.NeedsYou,
        _ when stalled => AttentionBucket.Stalled,
        "Running" or "Verifying" or "UnderReview" or "Dispatched" or "ClosingOut" => AttentionBucket.Active,
        // A claim without a run to refine it is the dispatch handoff caught mid-step: TaskClaimed
        // commits in its own transaction and the run document only appears once RunLauncher has
        // inspected the pull request and checked the worktree out, so every dispatch spends
        // seconds here — and if the daemon dies inside that window the task stays here until a
        // human moves it, with no lease sweep running to requeue it. The same arm carries a
        // terminal run state on a still-claimed task, which is the closing half of the same
        // handoff. Either way the platform holds the claim, so the task is live work; counting
        // it as Closed would drop live and stuck work out of h9k status altogether.
        "Claimed" or "Completed" or "Killed" or "Superseded" => AttentionBucket.Active,
        "AwaitingReview" or "ChecksFailing" or "ReviewPending" => AttentionBucket.InReview,
        "Queued" => AttentionBucket.Queued,
        "Done" => AttentionBucket.Done,
        _ => AttentionBucket.Closed,
    };

    /// <summary>How long ago, in the resolution a human actually reads at a glance.</summary>
    public static string RelativeAge(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 90 } => $"{(int)elapsed.TotalMinutes}m ago",
        { TotalHours: < 36 } => $"{(int)elapsed.TotalHours}h ago",
        _ => $"{(int)elapsed.TotalDays}d ago",
    };
}
