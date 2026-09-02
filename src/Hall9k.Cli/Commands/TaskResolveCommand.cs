using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
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

        // The run-side counterpart to the pull request TaskResolved just carried onto the task
        // stream: without this, RunDetails.PullRequestNumber stays null forever and the run —
        // still Failed — never matches CloseoutEngine's orphan sweep (Decisions Log #72), so the
        // row sits needs-you even after the pull request actually merges. Never PullRequestOpened
        // or PullRequestUpdated here: either one moves RunState to AwaitingReview, which would
        // pull this Failed run into the WATCHED sweep instead — the watched path dispatches
        // follow-up runs onto the branch, which is not what a dead run's recovery record wants.
        if (task.CurrentRunId is { } runId
            && BuildFailedRunPullRequestEvent(runId, settings.PullRequestUrl, resolvedAt) is { } pullRequestRecorded)
        {
            session.Events.Append(runId, pullRequestRecorded);
        }

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
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The run-stream event to append alongside <see cref="TaskDecider.Resolve"/>'s pull request,
    /// or null when there is nothing to record: no <c>--pr</c> was given, or the URL does not
    /// parse to a real pull request number (never guess a number, AGENTS.md's never-guess rule).
    /// In either null case the caller appends nothing, leaving the run exactly as invisible to
    /// <c>CloseoutEngine</c>'s orphan sweep as a resolve with no <c>--pr</c> already left it.
    /// </summary>
    public static PullRequestRecordedOnFailedRun? BuildFailedRunPullRequestEvent(
        Guid runId, string? pullRequestUrl, DateTimeOffset recordedAt)
    {
        if (pullRequestUrl.IsBlank())
        {
            return null;
        }

        int pullRequestNumber = PullRequestUrls.ParseNumber(pullRequestUrl);
        return pullRequestNumber > 0
            ? new PullRequestRecordedOnFailedRun(runId, pullRequestUrl, pullRequestNumber, recordedAt)
            : null;
    }
}
