using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
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
        [Description("Where the work landed, when known (e.g. the merged pull request) — recorded on the task and shown by h9k status")]
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
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Resolve(
            task, settings.Reason ?? string.Empty, settings.PullRequestUrl, resolvedAt, context.OwnerId));

        RunStreamPullRequestOutcome runStreamOutcome = await RecordPullRequestOnRunStreamAsync(
            session, task, settings.PullRequestUrl, resolvedAt, cancellationToken);

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

        AnsiConsole.MarkupLineInterpolated(settings.PullRequestUrl.IsBlank()
            ? (FormattableString)$"[dim]Task {taskId} resolved to Done — the failure stays on the stream.[/]"
            : $"[dim]Task {taskId} resolved to Done — the failure stays on the stream. PR: {settings.PullRequestUrl}[/]");

        // Told on stderr rather than left to the "nothing is watching this pull request any
        // more" wording on h9k status to teach: a --pr given while the run stream itself is
        // missing is the normal missing-run-sweep path (silent, see RecordPullRequestOnRunStreamAsync's
        // own comment) and not this warning's concern — this fires only when a run stream existed
        // to record onto and the URL still did not end up recorded, because it did not parse to a
        // pull request number or did not name this task's own project repository.
        if (runStreamOutcome == RunStreamPullRequestOutcome.NotRecorded && settings.PullRequestUrl.IsNotBlank())
        {
            await Console.Error.WriteLineAsync(
                $"Note: {settings.PullRequestUrl} does not look like a pull request on this task's own "
                + "project repository, so closeout will not watch it for a merge — h9k status will keep "
                + "showing this run as unwatched until h9k pr resolve or a fresh h9k task resolve --pr "
                + "records one it recognizes.");
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
        /// pre-PR review, cycle 1). Silent by design: the missing-run sweep already watches this
        /// task's <c>PullRequestUrl</c> (recorded on the task stream by <c>TaskDecider.Resolve</c>
        /// regardless of this outcome) without needing a run stream at all.
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
    /// The run-side counterpart to the pull request <see cref="TaskDecider.Resolve"/> just
    /// carried onto the task stream: without this, <c>RunDetails.PullRequestNumber</c> stays null
    /// forever and the run — still Failed — never matches <c>CloseoutEngine</c>'s orphan sweep
    /// (Decisions Log #72), so the row sits needs-you even after the pull request actually
    /// merges. Never <c>PullRequestOpened</c> or <c>PullRequestUpdated</c> here: either one moves
    /// <c>RunState</c> to <c>AwaitingReview</c>, which would pull this Failed run into the
    /// WATCHED sweep instead — the watched path dispatches follow-up runs onto the branch, which
    /// is not what a dead run's recovery record wants.
    /// </summary>
    internal static async Task<RunStreamPullRequestOutcome> RecordPullRequestOnRunStreamAsync(
        IDocumentSession session,
        TaskAggregate task,
        string? pullRequestUrl,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken)
    {
        if (task.CurrentRunId is not { } runId
            || await session.Events.FetchStreamStateAsync(runId, cancellationToken) is null)
        {
            return RunStreamPullRequestOutcome.NoRunStream;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (BuildFailedRunPullRequestEvent(runId, pullRequestUrl, resolvedAt, project?.RepositoryUrl)
            is not { } pullRequestRecorded)
        {
            return RunStreamPullRequestOutcome.NotRecorded;
        }

        session.Events.Append(runId, pullRequestRecorded);
        return RunStreamPullRequestOutcome.Recorded;
    }

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
        Guid runId, string? pullRequestUrl, DateTimeOffset recordedAt, Uri? projectRepositoryUrl = null)
    {
        if (pullRequestUrl.IsBlank())
        {
            return null;
        }

        int pullRequestNumber = PullRequestUrls.ParseNumber(pullRequestUrl);
        if (pullRequestNumber <= 0)
        {
            return null;
        }

        if (RepositoryFrom(projectRepositoryUrl) is { } projectRepository
            && Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out Uri? parsedPullRequestUrl)
            && RepositoryFrom(parsedPullRequestUrl) is { } pullRequestRepository
            && !string.Equals(projectRepository, pullRequestRepository, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new PullRequestRecordedOnFailedRun(runId, pullRequestUrl, pullRequestNumber, recordedAt);
    }

    /// <summary>
    /// "owner/repo" out of a repository or pull-request URL's first two path segments — the same
    /// shape <c>RunLauncher.OwnerRepoFrom</c> reads a project's own repository URL with, reused
    /// here rather than shared across the Cli/Daemon boundary those two projects don't cross.
    /// </summary>
    private static string? RepositoryFrom(Uri? url) =>
        url is not null && url.AbsolutePath.Trim('/').Split('/') is [{ Length: > 0 } owner, { Length: > 0 } repository, ..]
            ? $"{owner}/{TrimGitSuffix(repository)}"
            : null;

    private static string TrimGitSuffix(string repository) =>
        repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repository[..^4] : repository;
}
