using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class StatusCommand : Hall9kAsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    private static readonly TimeSpan StallThreshold = TimeSpan.FromHours(1);

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing tracked yet. Queue work with h9k task add.[/]");
            return ExitCodes.Ok;
        }

        Dictionary<Guid, string> projects = (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id, p => p.Name);
        Dictionary<Guid, RunListItem> runs = (await session.Query<RunListItem>().ToListAsync(cancellationToken))
            .ToDictionary(r => r.Id);
        Dictionary<Guid, RunActivity> activity = (await session.Query<RunActivity>().ToListAsync(cancellationToken))
            .ToDictionary(a => a.Id);

        List<StatusRow> rows = [.. tasks
            .Select(task => Compose(task, runs, activity, projects, DateTimeOffset.UtcNow))
            .OrderBy(row => row.Priority)
            .ThenByDescending(row => row.AddedAt)];

        // Failed is a needs-human waypoint (Decisions Log #27): it waits for retry,
        // resolve, or abandon, so it counts toward the attention surface.
        int needsHuman = rows.Count(r => r.Bucket is "NeedsHuman" or "Failed");
        int stalled = rows.Count(r => r.Stalled);
        int awaitingReview = rows.Count(r => r.Bucket == "AwaitingReview");
        int active = rows.Count(r => r.Bucket is "Running" or "Verifying" or "UnderReview" or "Dispatched" or "Queued" or "ClosingOut");

        AnsiConsole.MarkupLine(
            $"[bold]h9k status[/] · " +
            (needsHuman > 0 ? $"[red bold]{needsHuman} need you[/] · " : string.Empty) +
            (stalled > 0 ? $"[red]{stalled} stalled[/] · " : string.Empty) +
            (awaitingReview > 0 ? $"[magenta]{awaitingReview} awaiting review[/] · " : string.Empty) +
            $"{active} active");

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Id", "Status", "Project", "Objective", "Activity", "PR");
        foreach (StatusRow row in rows)
        {
            table.AddRow(row.Id, row.StatusMarkup, row.Project, row.Objective, row.Activity, row.PullRequest);
        }

        AnsiConsole.Write(table);
        return ExitCodes.Ok;
    }

    internal sealed record StatusRow(
        string Id, string Bucket, string StatusMarkup, string Project, string Objective,
        string Activity, string PullRequest, bool Stalled, int Priority, DateTimeOffset AddedAt);

    /// <summary>
    /// Display state composition per TASK-MODEL.md §2: the task's work state, refined by
    /// the current run's execution state while claimed. A done task with a PR is in the
    /// closeout phase (log #18/#22): the current run's closeout state refines the
    /// display — AwaitingReview while quiet, ChecksFailing/ReviewPending when observed,
    /// NeedsHuman when parked, Done once the merge landed. A queued/claimed task that
    /// still carries a PR URL is a follow-up run in flight: ClosingOut.
    /// </summary>
    internal static StatusRow Compose(
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
            activityText = Age(silence);
        }

        string status = bucket switch
        {
            "NeedsHuman" => "[red bold]NeedsHuman[/]",
            "AwaitingReview" => "[magenta]AwaitingReview[/]",
            "ChecksFailing" => "[red]ChecksFailing[/]",
            "ReviewPending" => "[magenta]ReviewPending[/]",
            "Running" or "Verifying" or "UnderReview" or "Dispatched" or "ClosingOut" => stalled
                ? $"[red]{bucket} ⚠ STALLED[/]"
                : $"[yellow]{bucket}[/]",
            "Queued" => "[blue]Queued[/]",
            "Done" => "[green]Done[/]",
            "Failed" => "[red]Failed[/]",
            "Abandoned" => "[dim]Abandoned[/]",
            _ => bucket,
        };

        int priority = bucket switch
        {
            "NeedsHuman" => 0,
            // A failed task waits for a human decision (retry, resolve, abandon — log #27):
            // it ranks with stalled work, right under the explicit NeedsHuman parks.
            "Failed" => 1,
            _ when stalled => 1,
            "Running" or "Verifying" or "UnderReview" or "Dispatched" or "ClosingOut" => 2,
            "AwaitingReview" or "ChecksFailing" or "ReviewPending" => 3,
            "Queued" => 4,
            "Done" => 6,
            _ => 7,
        };

        return new StatusRow(
            $"[dim]{TaskListCommand.ShortId(task.Id)}[/]",
            bucket,
            status,
            (projects.GetValueOrDefault(task.ProjectId) ?? "?").EscapeMarkup(),
            TaskListCommand.Truncate(task.Objective, 46).EscapeMarkup(),
            activityText,
            task.PullRequestUrl.IsNotBlank() ? $"[link={task.PullRequestUrl}]#{PrNumber(task.PullRequestUrl)}[/]" : string.Empty,
            stalled,
            priority,
            task.AddedAt);
    }

    private static string PrNumber(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static string Age(TimeSpan silence) => silence switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 90 } => $"{(int)silence.TotalMinutes}m ago",
        { TotalHours: < 36 } => $"{(int)silence.TotalHours}h ago",
        _ => $"{(int)silence.TotalDays}d ago",
    };
}
