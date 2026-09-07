using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Sets a task's standing pre-approval after publish (task: a task can be published
/// pre-approved) — settable on any live task whose pull request has not yet merged, without the
/// unassign/draft/revise/publish ceremony a readiness-contract change would otherwise need. A
/// Draft takes it too, now that publish carries a standing grant forward instead of overwriting
/// it — <see cref="TaskDecider.SetPreApproved"/>'s own doc carries the history of why it did not.
/// <para>
/// Three-valued since the mode that waits for human review landed (task: the people a pull request
/// is waiting on are named, and pre-approval gains a mode that waits for human review), which also
/// makes this the emergency door: after-human-review to on merges on the next closeout sweep if
/// GitHub's own gates read satisfied, without waiting for a reviewer who is not coming.
/// </para>
/// </summary>
public sealed class TaskSetPreApprovedCommand : Hall9kAsyncCommand<TaskSetPreApprovedCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<on|off|after-human-review>")]
        [Description(
            "on/true to give standing pre-approval — the daemon merges once GitHub's own gates read "
            + "satisfied; off/false to withdraw it, and the owner becomes a synchronous gate at the pull "
            + "request again the moment this lands; after-human-review to merge on those same gates but "
            + "only once a human reviewer has actually been requested on the pull request and every "
            + "requested reviewer has approved the current head. Add the reviewers you want in GitHub — "
            + "hall9k stores no reviewer setting and requests no reviews. Flipping after-human-review to on "
            + "is the emergency path: the next sweep merges on GitHub's own gates alone")]
        public string Value { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        // Passed through unvetted: TaskDecider.SetPreApproved refuses an unrecognized mode with the
        // whole vocabulary quoted, which is the one message an agent can self-correct from — a
        // parse here would have to duplicate it to say anything as useful.
        PreApprovalMode requested = settings.Value;

        // The same "task is Done and its current run reached RunState.Completed" test
        // TaskDependencyQuery.IsClosedOut uses for true closeout — the aggregate alone cannot
        // answer it, since a merge observation lands on the run stream, never the task's.
        bool taskClosedOut = task.State == TaskState.Done
            && task.CurrentRunId is { } currentRunId
            && (await session.LoadAsync<RunDetails>(currentRunId, cancellationToken))?.State == RunState.Completed;

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskPreApprovedSet set = TaskDecider.SetPreApproved(
            task, requested, DateTimeOffset.UtcNow, context.OwnerId, taskClosedOut);
        session.Events.Append(taskId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        PreApprovalMode landed = set.EffectivePreApproval;
        AnsiConsole.MarkupLine(landed.MergesAutomatically
            ? $"[green]Task {shortId} is now pre-approved ({PreApprovalInput.Word(landed)})[/] — "
                + $"{PreApprovalInput.Describe(landed)}; every human waypoint (Failed, a review park, a "
                + "cap trip) still stops it exactly as before."
            : $"[green]Task {shortId} is no longer pre-approved[/] — the owner is a synchronous gate at its "
                + "pull request again.");
        return ExitCodes.Ok;
    }
}
