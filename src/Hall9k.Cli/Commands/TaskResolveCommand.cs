using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The attestation exit from Failed (Decisions Log #27): the run failed, but the objective
/// was met anyway — the task ends Done, with the failure still on the stream. The reason is
/// required (an attestation without a why is a guess, the AGENTS.md never-guess rule) and
/// the exit is human-only: no monitor resolves a failure (never loop on judgment, log #11).
/// The other two exits from Failed are h9k task retry and h9k task abandon.
/// </summary>
public sealed class TaskResolveCommand : Hall9kAsyncCommand<TaskResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Required: why the objective counts as met despite the run failure — the attestation recorded on the stream and shown by h9k task show")]
        public string? Reason { get; init; }

        [CommandOption("--pr <URL>")]
        [Description("Where the work landed, when known (e.g. the merged pull request) — recorded on the task and shown by h9k status. When it names a real pull request on the project's own repository, it also enrolls that pull request in closeout's orphan sweep, so its merge later completes this task's closeout (unblocking dependents, removing the retained worktree) same as any watched run — except on a pr-review task, whose --pr names the pull request it reviewed and is never enrolled")]
        public string? PullRequestUrl { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        // Fence before aggregating: a resolve racing h9k task retry (or the dispatch loop
        // after one) must not land on a task that already left Failed.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;

        RunStreamPullRequestOutcome runStreamOutcome = await RecordPullRequestOnRunStreamAsync(
            session, task, settings.PullRequestUrl, resolvedAt, cancellationToken);

        // With no run stream at all, nothing on the run side will ever protect this task from
        // CloseoutEngine's missing-run sweep — TasksWithMissingRunRecordsAsync's own candidate
        // shape is PullRequestUrl != null && CurrentRunId != null with no RunDetails row ever
        // materializing to drop it back out, and that sweep's own inspection applies neither the
        // pr-review guard nor the repository-match guard the run-stream path above already
        // enforces (fixing the sweep itself is routed to a task of its own). Recording the URL on
        // the task stream at all is the one lever left that can make this task match that
        // candidate shape, so it only happens when it is exactly as safe as the run-stream path
        // above already requires (independent pre-PR review, cycle 1, medium).
        string? taskStreamPullRequestUrl = runStreamOutcome == RunStreamPullRequestOutcome.NoRunStream
            ? await SafePullRequestUrlWithoutRunStreamAsync(session, task, settings.PullRequestUrl, cancellationToken)
            : settings.PullRequestUrl;

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Resolve(
            task, settings.Reason ?? string.Empty, taskStreamPullRequestUrl, resolvedAt, context.OwnerId));

        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while resolving — check h9k status; re-run this command " +
                "only if the task is still Failed.");
        }

        AnsiConsole.MarkupLineInterpolated(taskStreamPullRequestUrl.IsBlank()
            ? (FormattableString)$"[dim]Task {taskId} resolved to Done — the failure stays on the stream.[/]"
            : $"[dim]Task {taskId} resolved to Done — the failure stays on the stream. PR: {taskStreamPullRequestUrl}[/]");

        // Told on stderr rather than left to the "nothing is watching this pull request any
        // more" wording on h9k status to teach: a --pr given while the run stream itself is
        // missing but was still safe to show on the task (see SafePullRequestUrlWithoutRunStreamAsync)
        // is the normal missing-run-sweep path (silent) and not this warning's concern — this fires
        // when a run stream existed to record onto and the URL still did not end up recorded there,
        // or when no run stream existed and the URL was not even safe to show on the task. The
        // reasons that can happen name different advice, and none of them names h9k pr resolve as a
        // fix (independent pre-PR review, cycle 1, medium): that command reopens the task onto its
        // own already-recorded PullRequestUrl and branch (TaskDecider.Reopen, PullRequestResolveCommand)
        // rather than accepting a new URL, so it cannot correct a mistyped or foreign one — it can
        // only dispatch a follow-up run onto a dead branch, or, worse, watch whatever pull request
        // the wrong number happens to name in the project's own repository. This task is Done, and
        // a fresh h9k task resolve --pr is never a real lever either way, since TaskDecider.Resolve
        // accepts only a Failed one.
        bool pullRequestUrlNotRecordedAnywhere = taskStreamPullRequestUrl.IsBlank();
        if (settings.PullRequestUrl.IsNotBlank()
            && (runStreamOutcome == RunStreamPullRequestOutcome.NotRecorded || pullRequestUrlNotRecordedAnywhere))
        {
            string visibility = pullRequestUrlNotRecordedAnywhere
                ? "it was not recorded at all — h9k task show and h9k status will not display it"
                : "h9k status will keep showing this run as unwatched, which is expected here";
            if (task.Type == TaskType.PrReview)
            {
                await Console.Error.WriteLineAsync(
                    $"Note: task {taskId} is a pull-request review — {settings.PullRequestUrl} names the "
                    + "pull request it reviewed, not one of this task's own, so closeout will never watch "
                    + "it for a merge. There is no lever that changes that: h9k pr resolve refuses a "
                    + $"pull-request review task outright. {visibility}.");
            }
            else
            {
                await Console.Error.WriteLineAsync(
                    $"Note: {settings.PullRequestUrl} does not look like a pull request on this task's own "
                    + "project repository, so closeout will not watch it for a merge. There is no lever "
                    + "that fixes this: h9k pr resolve reopens the task onto its already-recorded pull "
                    + $"request rather than accepting a corrected one, so it cannot record a different URL. {visibility}.");
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>What appending the run-side pull request came to, for the caller's stderr note and for tests.</summary>
    internal enum RunStreamPullRequestOutcome
    {
        /// <summary>
        /// <see cref="TaskAggregate.CurrentRunId"/> is null, or names a run whose stream was
        /// never started — an interactive claim's worktree cut failing before <c>h9k task work</c>
        /// starts the run stream, the same shape <c>RunLauncher.RecordLaunchFailureAsync</c>
        /// guards against. Appending here regardless would implicitly create that stream — Marten
        /// starts a stream on its first append — which would materialize a stub
        /// <c>RunDetails</c> row and drop the task out of
        /// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own candidate set, the one
        /// sweep that is actually built to complete closeout for exactly this shape (independent
        /// pre-PR review, cycle 1). Silent by design: the missing-run sweep watches this task's
        /// <c>PullRequestUrl</c> without needing a run stream at all — recorded on the task stream
        /// by <c>TaskDecider.Resolve</c> only when <see cref="SafePullRequestUrlWithoutRunStreamAsync"/>
        /// judges it safe to (independent pre-PR review, cycle 1, medium: that sweep's own
        /// inspection applies neither the pr-review guard nor the repository-match guard this
        /// command enforces everywhere else, so an unsafe URL recorded here regardless would still
        /// reach it).
        /// </summary>
        NoRunStream,

        /// <summary>
        /// A run stream existed, but <see cref="BuildFailedRunPullRequestEvent"/> built nothing to
        /// append onto it — no <c>--pr</c> was given, the URL did not parse to a pull request
        /// number, or it named a different repository than the project's own.
        /// </summary>
        NotRecorded,

        /// <summary><see cref="PullRequestRecordedOnFailedRun"/> was appended to the run stream.</summary>
        Recorded,
    }

    /// <summary>
    /// The run-side counterpart to the pull request <see cref="TaskDecider.Resolve"/> also
    /// carries onto the task stream: without this, <c>RunDetails.PullRequestNumber</c> stays null
    /// forever and the run — still Failed — never matches <c>CloseoutEngine</c>'s orphan sweep
    /// (Decisions Log #72), so the row sits needs-you even after the pull request actually
    /// merges. Never <c>PullRequestOpened</c> or <c>PullRequestUpdated</c> here: either one moves
    /// <c>RunState</c> to <c>AwaitingReview</c>, which would pull this Failed run into the
    /// WATCHED sweep instead — the watched path dispatches follow-up runs onto the branch, which
    /// is not what a dead run's recovery record wants. A <see cref="TaskType.PrReview"/> task's
    /// <c>--pr</c> names the pull request it reviewed, not one of its own, so it records nothing
    /// here regardless of the URL: recording it would enroll a foreign pull request as this
    /// run's merge signal, and that pull request's own merge would complete this task's closeout
    /// and run the remote branch-delete cleanup <c>TaskDecider.Reopen</c> already refuses the
    /// type to prevent (adversarial review, cycle 3, high).
    /// </summary>
    internal static async Task<RunStreamPullRequestOutcome> RecordPullRequestOnRunStreamAsync(
        IDocumentSession session,
        TaskAggregate task,
        string? pullRequestUrl,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken,
        ProcessRunner? processRunner = null)
    {
        if (task.CurrentRunId is not { } runId
            || await session.Events.FetchStreamStateAsync(runId, cancellationToken) is null)
        {
            return RunStreamPullRequestOutcome.NoRunStream;
        }

        if (task.Type == TaskType.PrReview)
        {
            return RunStreamPullRequestOutcome.NotRecorded;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        Uri? projectRepositoryUrl = await ResolveProjectRepositoryUrlAsync(project, processRunner, cancellationToken);
        if (BuildFailedRunPullRequestEvent(runId, pullRequestUrl, resolvedAt, projectRepositoryUrl)
            is not { } pullRequestRecorded)
        {
            return RunStreamPullRequestOutcome.NotRecorded;
        }

        session.Events.Append(runId, pullRequestRecorded);
        return RunStreamPullRequestOutcome.Recorded;
    }

    /// <summary>
    /// What <c>--pr</c> should still be recorded as on the task stream when
    /// <see cref="RecordPullRequestOnRunStreamAsync"/> found no run stream to append onto at all
    /// (<see cref="RunStreamPullRequestOutcome.NoRunStream"/>): with no run stream, no
    /// <c>RunDetails</c> row will ever materialize to drop this task back out of
    /// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own candidate shape
    /// (<c>PullRequestUrl != null &amp;&amp; CurrentRunId != null</c>), and that sweep's own
    /// inspection (<c>InspectMissingRunAsync</c>) applies neither the pr-review guard nor the
    /// repository-match guard <see cref="BuildFailedRunPullRequestEvent"/> already enforces on the
    /// run-stream path above — fixing the sweep itself is routed to a task of its own. Whether the
    /// URL is even recorded here for display is therefore the only lever this command has left to
    /// keep that sweep honest, so the same two guards apply here too (independent pre-PR review,
    /// cycle 1, medium).
    /// </summary>
    internal static async Task<string?> SafePullRequestUrlWithoutRunStreamAsync(
        IDocumentSession session,
        TaskAggregate task,
        string? pullRequestUrl,
        CancellationToken cancellationToken,
        ProcessRunner? processRunner = null)
    {
        if (pullRequestUrl.IsBlank() || task.Type == TaskType.PrReview)
        {
            return null;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        Uri? projectRepositoryUrl = await ResolveProjectRepositoryUrlAsync(project, processRunner, cancellationToken);
        return IsSafePullRequestUrl(pullRequestUrl, projectRepositoryUrl) ? pullRequestUrl : null;
    }

    /// <summary>
    /// The project's own repository, falling back to the same ambient <c>gh repo view</c>
    /// observation <c>RunLauncher.LaunchAsync</c> and <c>TaskPublishCommand</c> already use for a
    /// project registered with <c>--repo</c> and no <c>--repo-url</c> — the shape that leaves
    /// <see cref="ProjectDetails.RepositoryUrl"/> null forever, since nothing backfills it and
    /// there is no <c>h9k project set --repo-url</c> (independent pre-PR review, cycle 3, medium:
    /// an earlier version of this guard read only <c>RepositoryUrl</c> and treated that null as
    /// "unknown, proceed" — permanently inert for exactly that project shape). Best-effort, like
    /// both precedents: a <c>gh</c> that cannot resolve a remote at all leaves nothing observed, so
    /// the caller proceeds exactly as it would when no repository is recorded at all.
    /// </summary>
    private static async Task<Uri?> ResolveProjectRepositoryUrlAsync(
        ProjectDetails? project, ProcessRunner? processRunner, CancellationToken cancellationToken) =>
        project is null
            ? null
            : project.RepositoryUrl
                ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(
                    project.RepositoryPath, cancellationToken);

    /// <summary>
    /// The run-stream event to append alongside <see cref="TaskDecider.Resolve"/>'s pull request,
    /// or null when there is nothing to record: no <c>--pr</c> was given, the URL does not parse
    /// to a real pull request number (never guess a number, AGENTS.md's never-guess rule), or the
    /// URL names a repository other than <paramref name="projectRepositoryUrl"/>'s own — a
    /// mistyped or copy-pasted URL from an unrelated repository must not become this run's merge
    /// signal, since CloseoutEngine inspects strictly by number against the project's own
    /// repository and a false match would let an unrelated pull request's merge complete this
    /// task's closeout and delete this run's own branch out from under it (adversarial review,
    /// cycle 1). The repository check is a courtesy, the same shape
    /// <c>RunLauncher.LaunchAsync</c>'s pr-review repository check is: a project with no
    /// <paramref name="projectRepositoryUrl"/> recorded proceeds rather than blocking on
    /// information this command does not have.
    /// In every null case the caller appends nothing, leaving the run exactly as invisible to
    /// <c>CloseoutEngine</c>'s orphan sweep as a resolve with no <c>--pr</c> already left it.
    /// </summary>
    public static PullRequestRecordedOnFailedRun? BuildFailedRunPullRequestEvent(
        Guid runId, string? pullRequestUrl, DateTimeOffset recordedAt, Uri? projectRepositoryUrl = null) =>
        IsSafePullRequestUrl(pullRequestUrl, projectRepositoryUrl)
            ? new PullRequestRecordedOnFailedRun(
                runId, pullRequestUrl, PullRequestUrls.ParseNumber(pullRequestUrl), recordedAt)
            : null;

    /// <summary>
    /// Whether <paramref name="pullRequestUrl"/> is safe to treat as this task's own pull request
    /// anywhere it can be watched for a merge — the run stream (<see cref="BuildFailedRunPullRequestEvent"/>)
    /// or, with no run stream to protect it, the task stream itself
    /// (<see cref="SafePullRequestUrlWithoutRunStreamAsync"/>). False for a blank URL, one that
    /// does not parse to a real pull request number (never guess a number, AGENTS.md's never-guess
    /// rule), or one naming a repository other than <paramref name="projectRepositoryUrl"/>'s own —
    /// checked by host as well as owner/repo, since <see cref="PullRequestUrls.RepositoryFrom"/>
    /// alone reads path segments only and would otherwise treat
    /// <c>https://gitlab.com/x/y/pull/24</c> as the same repository as a project recorded at
    /// <c>https://github.com/x/y</c> (adversarial review, cycle 1, medium). A mistyped or
    /// copy-pasted URL from an unrelated repository must never become this run's merge signal:
    /// CloseoutEngine inspects strictly by number against the project's own repository, and a
    /// false match would let an unrelated pull request's merge complete this task's closeout and
    /// delete this run's own branch out from under it (adversarial review, cycle 1). The
    /// repository check is a courtesy, the same shape <c>RunLauncher.LaunchAsync</c>'s pr-review
    /// repository check is: a project whose repository cannot be resolved at all proceeds rather
    /// than blocking on information this command does not have.
    /// </summary>
    private static bool IsSafePullRequestUrl(
        [NotNullWhen(true)] string? pullRequestUrl, Uri? projectRepositoryUrl)
    {
        if (pullRequestUrl.IsBlank() || PullRequestUrls.ParseNumber(pullRequestUrl) <= 0)
        {
            return false;
        }

        if (projectRepositoryUrl is not null
            && Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out Uri? parsedPullRequestUrl)
            && PullRequestUrls.RepositoryFrom(projectRepositoryUrl) is { } projectRepository
            && PullRequestUrls.RepositoryFrom(parsedPullRequestUrl) is { } pullRequestRepository
            && (!string.Equals(projectRepositoryUrl.Host, parsedPullRequestUrl.Host, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(projectRepository, pullRequestRepository, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }
}
