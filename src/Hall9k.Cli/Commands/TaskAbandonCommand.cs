using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskAbandonCommand : Hall9kAsyncCommand<TaskAbandonCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why this task is being walked away from; recorded on TaskAbandoned and left unknown "
            + "when omitted, never inferred (Decisions Log #27)")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        DateTimeOffset abandonedAt = DateTimeOffset.UtcNow;
        bool releasesHolder = task.HolderNodeId == context.NodeId;
        object[] taskEvents = releasesHolder
            ? [TaskDecider.Abandon(task, settings.Reason, abandonedAt, context.OwnerId), TaskDecider.ReleaseHolder(task, abandonedAt)]
            : [TaskDecider.Abandon(task, settings.Reason, abandonedAt, context.OwnerId)];
        session.Events.Append(taskId, taskEvents);
        session.Delete<TaskLease>(taskId);

        // Otherwise an abandoned interactive claim's run reads Running (or Dispatched) forever:
        // it holds no TaskLease (an interactive claim writes none) and its NodeId is the
        // Guid.Empty sentinel, so neither AdoptOrphansAsync's NodeId filter nor
        // SweepExpiredLeasesAsync's lease scan will ever retire it — mirrors TaskReleaseCommand
        // and TaskHandbackCommand's own retirement of the run they displace (conformance review,
        // cycle 4). Deliberately scoped to an interactive claim, not every headless one too
        // (independent pre-PR review, cycle 1, adversarial lens): a daemon-dispatched headless
        // run in Dispatched or Running is a live agent process RunSupervisor's own in-memory
        // Monitor is actively watching, with the stream.jsonl file handle held open by that
        // child process — stamping RunSuperseded on it here races that supervision instead of
        // deferring to it: ProjectHomeRenderEngine's archive sweep would move the task directory
        // out from under the still-running agent (its own Abandoned branch waits on run
        // liveness specifically to avoid that), and AdoptOrphansAsync's AdoptableRunStates
        // excludes Superseded, so a daemon restart before the agent exits would leave an orphan
        // process nothing ever adopts or terminates. A headless run past that point still gets
        // stopped: the terminal-task check every dispatch site now runs (task: abandoning a task
        // halts its in-flight run entirely) refuses the next verification, review, fix, or park
        // attempt and retires the run there instead, on its very next dispatch attempt — the
        // same fix already applied for the origin incident (2026-08-27, task ab484e89) that
        // motivated stamping the conclusion early for the interactive lane below. Excludes a
        // pr-review task outright, unlike the state check above: PrReviewSentinelClaim's own
        // IsLive branch names "h9k task abandon" as the honest way out of a live sentinel run,
        // so abandon must keep working there, but that run is launched through RunLauncher and
        // monitored by RunSupervisor exactly like an ordinary headless dispatch —
        // RunSupervisor.CompleteRunAsync appends its own TokensRecorded from the identical
        // stream.jsonl the moment the agent's result line lands, and its own
        // AgentSessionCompleted append later resurrects a run this command marked Superseded
        // (RunDetails.Apply unconditionally sets RunState.Verifying), the exact hazard
        // TaskDeliverCommand's own re-check exists to catch. Recovering tokens or superseding the
        // run here would double-book the former and race the latter against the daemon's own
        // supervisor (independent pre-PR review, cycle 1, both lenses).
        // IsInteractiveClaim is the sentinel NodeId (Guid.Empty) alone — it does not, and
        // cannot, tell a genuinely interactive h9k task work claim (no agent process at all)
        // apart from h9k task start's own deliberate headless dispatch (TaskDecider.ClaimDeliberately
        // records the identical sentinel for both), so this branch's immediate stamp still lands
        // on a live headless agent process a `h9k task start` claimed, not only on an interactive
        // one — pre-existing behaviour (508da8bc), not this branch's own to change, but worth
        // naming rather than letting the three hazards above read as excluded by this check when
        // they are not.
        if (task.State == TaskState.Claimed && task.IsInteractiveClaim && task.Type != TaskType.PrReview
            && task.CurrentRunId is { } currentRunId)
        {
            RunDetails? run = await session.LoadAsync<RunDetails>(currentRunId, cancellationToken);
            if (run is not null && (run.State == RunState.Dispatched || run.State == RunState.Running))
            {
                DateTimeOffset supersededAt = DateTimeOffset.UtcNow;
                // A start-it-mine claim abandoned mid-run had already spent tokens its own
                // stream.jsonl is the only record of — otherwise never read back once this run
                // is retired (conformance review, cycle 1, on h9k task start).
                HeadlessTokenRecovery.AppendIfRecorded(session, run, supersededAt);
                HeadlessTokenRecovery.AppendDelegatedPhaseTokens(session, run, supersededAt);
                session.Events.Append(currentRunId, new RunSuperseded(currentRunId, task.LeaseGeneration, supersededAt));
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-abandoned:{taskId}", cancellationToken);

        if (releasesHolder)
        {
            await ReleaseLedgerHolderBestEffortAsync(store, context, task, cancellationToken);
            await MirrorTrackerReleaseBestEffortAsync(store, context, task, cancellationToken);
        }

        AnsiConsole.MarkupLine($"[dim]Task {taskId} abandoned.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The tracker-assignee twin of <see cref="ReleaseLedgerHolderBestEffortAsync"/> (criterion 5,
    /// idea 202383dc, A3b: "on every holder change the tracker assignee is set to match"): abandon
    /// gives the ledger holder back, so this install's own tracker identity is cleared off the
    /// linked item too, best effort — a named outcome short of a confirmed clear leaves a
    /// <see cref="TaskTrackerReleaseMirrorPending"/> row for <c>DispatchEngine</c>'s own
    /// <c>SweepPendingTrackerMirrorsAsync</c> to retry, the identical row the lease-expiry release
    /// already gives this same mirror (conformance review, this branch's fix cycle: abandon cleared
    /// the ledger holder without ever clearing the tracker assignee).
    /// </summary>
    private static async Task MirrorTrackerReleaseBestEffortAsync(
        Marten.DocumentStore store, BootstrapContext context, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.ExternalReference is null)
        {
            return;
        }

        try
        {
            await using IDocumentSession session = store.LightweightSession();
            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
            if (project is null)
            {
                return;
            }

            TrackerAssignmentTake trackerAssignmentTake = new(new ProjectScopedGitHubRunner(store).Runner);
            TrackerRelease release = await trackerAssignmentTake.ReleaseAsync(
                store, ClaimGate.TrackerAssignee, task.ExternalReference, project.RepositoryPath, cancellationToken);
            if (!release.Succeeded)
            {
                await StorePendingTrackerReleaseMirrorAsync(
                    store, task, context, release.FailureReason ?? string.Empty, cancellationToken);
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Clearing task {task.Id}'s tracker assignee failed; it is retried on this node's next dispatch sweep. ({release.FailureReason})[/]");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StorePendingTrackerReleaseMirrorAsync(store, task, context, exception.Message, cancellationToken);
        }
    }

    private static async Task StorePendingTrackerReleaseMirrorAsync(
        Marten.DocumentStore store,
        TaskAggregate task,
        BootstrapContext context,
        string failureReason,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new TaskTrackerReleaseMirrorPending
        {
            Id = TaskTrackerReleaseMirrorPending.KeyFor(task.Id, context.NodeId),
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            NodeId = context.NodeId,
            RecordedAt = DateTimeOffset.UtcNow,
            LastFailureReason = failureReason,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Release: abandonment clears the ledger holder by the same conditional write a claim used
    /// (idea 202383dc, A3b) — best effort, since the domain-side abandon and holder release have
    /// already landed by the time this runs; a write that cannot complete here is retried by
    /// whichever node's own dispatch sweep next finds this task's holder pointing at a run that no
    /// longer exists ("a task whose holder is this node but whose run is gone is released on the
    /// next sweep").
    /// </summary>
    private static async Task ReleaseLedgerHolderBestEffortAsync(
        Marten.DocumentStore store,
        BootstrapContext context,
        TaskAggregate task,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
            if (project is null)
            {
                return;
            }

            ILedger ledger = new GitLedger(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitLedger>.Instance);
            string recordsRef = LedgerRefRegistry.Records.RefspecSource;
            string recordPath = LedgerRefRegistry.RecordPath(task.Id);
            LedgerFile record = await ledger.ReadAsync(project.RepositoryPath, recordsRef, recordPath, cancellationToken);
            if (!record.Exists)
            {
                // A fetch that itself failed is not a confirmed absence (LedgerFile.FetchFailed):
                // the domain-side release has already landed by the time this runs, so silently
                // returning here would drop the ledger's own release for good, with no pending row
                // for any later sweep to ever pick back up — unlike DispatchEngine's own release
                // helper, an abandoned task's lease is already gone and nothing else ever revisits
                // it (adversarial review, this branch, class sweep from DispatchEngine.cs's
                // identical fetch-failure gap on the claim side).
                if (record.FetchFailed)
                {
                    await StorePendingReleaseAsync(
                        store, task, context,
                        "the ledger's own fetch failed before this node could tell whether a record exists "
                        + "here at all",
                        cancellationToken);
                }

                return;
            }

            (LedgerCommitter committer, LedgerSigningKey signingKey, _) =
                await TaskRecordPublication.ResolveIdentityAsync(session, context, cancellationToken);
            HolderReleaseResult result = await TaskLedgerHolder.TryReleaseAsync(
                ledger, project.RepositoryPath, task.Id, context.NodeId, committer, signingKey, cancellationToken);

            // Written on the identical shape DispatchEngine's and CloseoutEngine's own release
            // helpers use, so this node's next dispatch sweep (SweepPendingHolderReleasesAsync)
            // actually finds and retries it — a caught exception below reaches this same branch
            // too, since either shape leaves the ledger holder stuck on this node otherwise: an
            // abandoned task has no lease left to expire and nothing else ever revisits it.
            if (result.Verdict == HolderReleaseVerdict.Failed)
            {
                await StorePendingReleaseAsync(store, task, context, result.FailureReason ?? string.Empty, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StorePendingReleaseAsync(store, task, context, exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// Records this node's own owed-but-unconfirmed release, on the identical shape
    /// <c>DispatchEngine</c>'s and <c>CloseoutEngine</c>'s own release helpers use, so this node's
    /// next dispatch sweep (<c>SweepPendingHolderReleasesAsync</c>) actually finds and retries it —
    /// every caller reaches this same helper, since each of them leaves the ledger holder stuck on
    /// this node otherwise: an abandoned task has no lease left to expire and nothing else ever
    /// revisits it.
    /// </summary>
    private static async Task StorePendingReleaseAsync(
        Marten.DocumentStore store,
        TaskAggregate task,
        BootstrapContext context,
        string failureReason,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new TaskHolderReleasePending
        {
            Id = TaskHolderReleasePending.KeyFor(task.Id, context.NodeId),
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            NodeId = context.NodeId,
            RecordedAt = DateTimeOffset.UtcNow,
            LastFailureReason = failureReason,
        });
        await session.SaveChangesAsync(cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]Releasing task {task.Id}'s ledger holder failed; it is retried on this node's next dispatch sweep. ({failureReason})[/]");
    }
}
