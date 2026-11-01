using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Briefing;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Prints the briefing for the stop before a pull request opens: the task's human gate, or interactive
/// mode's gates-to-pull-request boundary. Short and plain by design, because a large diff recreates the wall
/// of text the gate exists to spare its owner: the opening paragraph of the run's own pull request summary,
/// what was asked for, how automated review ended, the commits with a total, and then the levers (compare,
/// run, step in, and the outcomes).
/// <para>
/// Read-only. It needs no GitHub pull request (none exists at this stop), makes no GitHub call, starts no
/// session, and appends nothing. It reads the run's worktree, so it answers only on the machine that holds
/// it, and names the node that does when run anywhere else. Anywhere but this stop it refuses and names
/// <c>h9k task next</c>, which says where the task actually is.
/// </para>
/// </summary>
public sealed class ReviewBriefCommand : Hall9kAsyncCommand<ReviewBriefCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        string page = await ComposeAsync(session, taskId, ExternalProcess.Runner, cancellationToken);
        Console.Out.Write(page);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The whole briefing, over a session and a git runner the caller supplies, so a test drives it against
    /// a real store and a real throwaway repository without going through <see cref="CliStore.Open"/>.
    /// </summary>
    internal static async Task<string> ComposeAsync(
        IDocumentSession session, Guid taskId, ProcessRunner git, CancellationToken cancellationToken)
    {
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(NotAtTheStop(taskId, "it has no current run"));
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken)
            ?? throw new DomainConflictException(NotAtTheStop(taskId, "its current run has no stream"));

        // Both the human gate and interactive mode's gates-to-pull-request boundary park from MergeReady
        // as an interactive gate (the human gate records that phase whatever it was reached from), so this
        // is one test for either, and anything else is some other park or no park at all.
        bool atTheStop = run.State == RunState.ReviewParked
            && run.ParkedIsInteractiveGate
            && run.ParkedFromReviewPhase == ReviewPhase.MergeReady;
        if (!atTheStop)
        {
            throw new DomainConflictException(NotAtTheStop(taskId, $"its run is {StandingOf(run)}"));
        }

        RunDetails details = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(NotAtTheStop(taskId, "its run has no recorded details"));
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        if (details.WorktreePath.IsBlank() || !Directory.Exists(details.WorktreePath))
        {
            throw new DomainConflictException(
                $"Task {taskId}'s worktree is not on this machine: it lives at {details.WorktreePath} on "
                + $"{await DescribeNodeAsync(session, details.NodeId, cancellationToken)}. Run h9k review brief "
                + "there; the briefing reads the branch and the worktree, so it can only be composed where they are.");
        }

        string baseBranch = details.BaseBranchOr(project.BaseBranch);
        string? head = await BranchObservation.ReadBranchTipAsync(git, details.WorktreePath, details.Branch, cancellationToken);
        string baseCommit = await ResolveBaseAsync(git, details, baseBranch, cancellationToken);
        IReadOnlyList<BriefingCommit> commits = head is null
            ? []
            : await ListCommitsAsync(git, details.WorktreePath, baseCommit, head, cancellationToken);
        (int? files, int? lines) = head is null
            ? (null, null)
            : await CountChangeAsync(git, details.WorktreePath, baseCommit, head, cancellationToken);
        DateTimeOffset? tipAuthoredAt = head is null
            ? null
            : await ReadAuthorDateAsync(git, details.WorktreePath, head, cancellationToken);

        string runDirectory = RunPaths.ResolveCurrentDirectory(details.RunDirectory);
        string summaryPath = RunPaths.PrSummaryFile(runDirectory);
        string? summary = File.Exists(summaryPath) ? await File.ReadAllTextAsync(summaryPath, cancellationToken) : null;
        DateTimeOffset? summaryWrittenAt = File.Exists(summaryPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(summaryPath), TimeSpan.Zero) : null;

        HumanGateBriefing.Stop stoppedFor = run.ParkedIsHumanGate
            ? HumanGateBriefing.Stop.HumanGate
            : HumanGateBriefing.Stop.InteractiveMode;
        HumanGateBriefingFacts facts = new(
            taskId, project.Name, task.Objective, task.AcceptanceCriteria,
            summary, summaryWrittenAt, tipAuthoredAt, summaryPath,
            details.ReviewSettlement.Value, details.ReviewCycle, FindingsNotFixed(details),
            RunPaths.ReviewFindingsFile(runDirectory, details.ReviewCycle),
            commits, files, lines,
            details.WorktreePath, details.Branch, baseCommit, head ?? "(the branch tip could not be read)",
            ProjectRunSkillReader.Read(project) is not null,
            InteractiveBoundaryChoices.LeversFor(run.ParkedFromReviewPhase, run.ParkedIsHumanGate)
                ?? InteractiveBoundaryLevers.GatesToPullRequest,
            stoppedFor);
        return HumanGateBriefing.Render(facts);
    }

    /// <summary>
    /// Every residual finding the branch carries to the pull request unfixed, which is what the briefing's
    /// "left unfixed" sentence counts. <see cref="RunDetails.ReviewResidualsUnfixed"/> alone is only the
    /// findings decided fix-here that no fix session was ever handed; a ride-along below the fix bar, a
    /// finding routed to a task of its own, and one whose routing failed are not fixed on this branch
    /// either, and the reader of this page is the person the briefing spares from the report, so a
    /// sentence that left them out would read as a cleaner branch than it is.
    /// </summary>
    internal static int FindingsNotFixed(RunDetails details) =>
        details.ReviewResidualsUnfixed
        + details.ReviewResidualsRideAlong
        + details.ReviewResidualsRouted
        + details.ReviewResidualsRoutingFailed;

    /// <summary>
    /// The refusal everywhere but the stop, naming the one command that says where the task is. Quotes the
    /// rule it applies, so an agent can correct itself from the message alone.
    /// </summary>
    internal static string NotAtTheStop(Guid taskId, string why) =>
        $"h9k review brief reads the briefing for the stop before a pull request opens, either a task's human "
        + $"gate or interactive mode's gates-to-pull-request boundary, and task {taskId} is not at one: {why}. "
        + $"h9k task next {taskId} says where it is and what moves it.";

    private static string StandingOf(RunAggregate run) =>
        run.State == RunState.ReviewParked
            ? "parked for something other than that stop"
            : $"{run.State.Value}, not parked";

    /// <summary>
    /// Names a node the way a person recognises it: its machine name, then a short id. Falls back to the
    /// short id alone when the node's record is not here, rather than inventing a name.
    /// </summary>
    internal static async Task<string> DescribeNodeAsync(
        IQuerySession session, Guid nodeId, CancellationToken cancellationToken)
    {
        NodeDetails? node = await session.LoadAsync<NodeDetails>(nodeId, cancellationToken);
        string shortId = DomainId.Short(nodeId);
        return node is { MachineName.Length: > 0 }
            ? $"node {shortId} ({node.MachineName})"
            : $"node {shortId}";
    }

    /// <summary>
    /// The commit the branch left the base at: the merge base with the remote-tracking base branch, then the
    /// local one, then the base commit the dispatch recorded. The recorded one goes last because a rebase
    /// before the final review pass moves the real boundary on from it.
    /// </summary>
    private static async Task<string> ResolveBaseAsync(
        ProcessRunner git, RunDetails details, string baseBranch, CancellationToken cancellationToken)
    {
        foreach (string reference in new[] { $"origin/{baseBranch}", baseBranch })
        {
            string? mergeBase = await RunAsync(
                git, details.WorktreePath, ["merge-base", reference, $"refs/heads/{details.Branch}"], cancellationToken);
            if (mergeBase.IsNotBlank())
            {
                return mergeBase.Trim();
            }
        }

        return details.BaseCommit.IsNotBlank() ? details.BaseCommit : "(the base commit could not be read)";
    }

    private static async Task<IReadOnlyList<BriefingCommit>> ListCommitsAsync(
        ProcessRunner git, string worktree, string baseCommit, string head, CancellationToken cancellationToken)
    {
        string? log = await RunAsync(
            git, worktree, ["log", "--reverse", "--format=%H%x1f%s", $"{baseCommit}..{head}"], cancellationToken);
        if (log.IsBlank())
        {
            return [];
        }

        return
        [
            .. log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\u001f', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => new BriefingCommit(parts[0], parts[1])),
        ];
    }

    private static async Task<(int? Files, int? Lines)> CountChangeAsync(
        ProcessRunner git, string worktree, string baseCommit, string head, CancellationToken cancellationToken)
    {
        string? numstat = await RunAsync(git, worktree, ["diff", "--numstat", baseCommit, head], cancellationToken);
        if (numstat is null)
        {
            return (null, null);
        }

        int files = 0;
        int lines = 0;
        foreach (string row in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = row.TrimEnd('\r').Split('\t');
            if (columns.Length < 3)
            {
                continue;
            }

            files++;
            // A binary file reports "-" for both counts: it is a changed file with no countable lines.
            lines += (int.TryParse(columns[0], CultureInfo.InvariantCulture, out int added) ? added : 0)
                + (int.TryParse(columns[1], CultureInfo.InvariantCulture, out int deleted) ? deleted : 0);
        }

        return (files, lines);
    }

    private static async Task<DateTimeOffset?> ReadAuthorDateAsync(
        ProcessRunner git, string worktree, string head, CancellationToken cancellationToken)
    {
        string? date = await RunAsync(git, worktree, ["log", "-1", "--format=%aI", head], cancellationToken);
        return DateTimeOffset.TryParse(date?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static async Task<string?> RunAsync(
        ProcessRunner git, string worktree, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await git("git", arguments, worktree, cancellationToken);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
