using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Exceptions;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// One sweep's tally: runs whose pull request was actually inspected, and how many of
/// those inspections observed the merge. Feeds the monitor's cadence logging and the
/// startup catch-up report (Decisions Log #31). Counts both the watched runs and the
/// orphaned ones the sweep also checks (Decisions Log #72) — a reader of this number
/// does not need to know which pass found a merge, only that one did.
/// </summary>
/// <param name="Failures">
/// How many inspections this sweep caught an exception from — almost always <c>gh</c> itself
/// (a rate limit, a network blip, an outage), since that is the one remote call each inspection
/// makes. It is not itself a retry signal here — the run stays in the watch set and the very
/// next sweep tries it again regardless of what this count says. <see cref="PullRequestMonitor"/>
/// reads it alongside <paramref name="RunsInspected"/> (<c>IsSweepFailure</c>) to widen its own
/// poll interval only when EVERY run this sweep looked at failed (independent pre-PR review,
/// cycle 4) — a lone permanently-broken pull request must not pin the interval at the ceiling
/// and delay every other healthy one this node also watches. <paramref name="Skipped"/> plays no
/// part in that check; see its own doc for why.
/// </param>
/// <param name="Skipped">
/// How many watched or orphaned runs this sweep passed over without ever calling <c>gh</c> — a
/// stale fence, a superseded run, a task that has not reached Done. A mid-sweep task-stream
/// advance discovered only after the inspector call already answered does NOT count here
/// (independent pre-PR review, cycle 2 adversarial): <c>gh</c> demonstrably answered for that
/// run even though the read was then discarded as stale, so it counts toward
/// <paramref name="RunsInspected"/> instead — otherwise a sweep pairing that race with one
/// genuine failure would read as "every attempted inspection failed" despite the one successful
/// read proving <c>gh</c> itself was healthy. A skip says nothing about whether <c>gh</c> is
/// healthy, but it is not gh trouble either, so <c>IsSweepFailure</c> (independent pre-PR review,
/// cycle 5) deliberately excludes it from both sides of the check rather than reading it as
/// evidence either way: a sweep containing only a broken pull request and otherwise-skipped
/// ones — a Done task reopened and then unassigned sits in the watch set returning Skipped
/// forever — pins the interval at the ceiling on the strength of the one real failure alone,
/// with the skips neither adding to nor diluting that verdict.
/// </param>
public sealed record CloseoutSweepResult(int RunsInspected, int MergesObserved, int Failures = 0, int Skipped = 0);

/// <summary>
/// The closeout core (Decisions Log #18/#22), extracted from the monitor loop so it
/// tests against a bare store and a fake inspector. Each node watches the
/// awaiting-review runs it executed (RunDetails.NodeId — the task itself is Done and
/// lease-free, so run provenance is the only honest owner). Per PR it observes merge,
/// close, failing checks, unresolved review threads from every reviewer (Decisions Log
/// #62 — Copilot is one reviewer among many), and errored Copilot reviews (an error
/// placeholder produces zero threads — never mistaken for a clean pass), dispatching
/// follow-up runs through the standard reopen pipeline and re-requesting errored reviews
/// through the API until the bounded automatic budget is spent — then it parks the run
/// for the human and keeps watching for the merge only. Where the owner or the project
/// opted in, a quiet pull request whose fixes were just pushed also gets a countersign
/// re-request, bounded by its own pass cap.
/// <para>
/// Every sweep also gives one read to each Delivered row whose run left the watch set by
/// failing rather than merging (Decisions Log #72) — a crash, a kill, or a stream from
/// before this monitor existed. A merge found there is recorded exactly as a watched one
/// is; anything else is left for the row's existing rendering to say, because a dead run
/// is not a run this engine dispatches follow-ups onto.
/// </para>
/// </summary>
public sealed class CloseoutEngine(
    IDocumentStore store,
    NodeContext node,
    DaemonConnection connection,
    IPullRequestInspector inspector,
    IWorktreeManager worktrees,
    StackedParentWatch stackedParents,
    ProcessRunner processRunner,
    JiraRequester jiraRequester,
    IOptions<DaemonOptions> options,
    ILogger<CloseoutEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>How one run's inspection ended — unpersisted in-process outcome, so an enum is fine (TASK-MODEL.md §8).</summary>
    private enum InspectionOutcome
    {
        Skipped,
        Inspected,
        MergeObserved,
    }

    /// <summary>One sweep over this node's watched pull requests, plus its orphans (Decisions Log #72).</summary>
    public async Task<CloseoutSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> watched;
        IReadOnlyList<RunDetails> orphaned;
        await using (IQuerySession query = store.QuerySession())
        {
            Guid nodeId = node.NodeId;
            // ReviewPending is watched too: a run holds there while an errored review's
            // re-request waits for the reviewer to answer. h9k task deliver records the
            // delivering node's own id on AgentSessionCompleted (Decisions Log #103), so an
            // interactively delivered run reaching these states already carries a real node id,
            // not the TaskAggregate.IsInteractiveClaim sentinel — no NodeId == Guid.Empty
            // widening here: DeliveredByNodeId is new alongside this feature, so no pre-fix
            // stream exists to widen for, and a widening would only let another node's daemon
            // match and drive this run concurrently (conformance review, cycle 4).
            watched = await query.Query<RunDetails>()
                .Where(r => r.NodeId == nodeId)
                .Where(r => r.MatchesSql(
                    "d.data ->> 'state' in (?, ?, ?)",
                    RunState.AwaitingReview.Value, RunState.ReviewPending.Value, RunState.CloseoutParked.Value))
                .ToListAsync(cancellationToken);

            // A run this node dispatched can leave the watch above by failing rather than
            // by merging: a crash before the monitor ever ran, a pre-monitor stream (the
            // six PR-8-through-12-era rows the orphan sweep exists for), a kill. Both Failed
            // and Killed are candidates — TaskStatusComposer renders a Done task's Delivered
            // row the same way for either, so a Killed run with no dispatched follow-up would
            // otherwise sit unwatched exactly like a Failed one. Its pull request does not
            // stop existing just because nothing is watching it any more. PullRequestClosedWithoutMerge
            // is excluded — that run already recorded the one thing an inspection here could
            // tell it, and asking GitHub again would spend a read to relearn a fact already on
            // the stream.
            orphaned = await query.Query<RunDetails>()
                .Where(r => r.NodeId == nodeId)
                .Where(r => r.MatchesSql(
                    "d.data ->> 'state' in (?, ?)", RunState.Failed.Value, RunState.Killed.Value))
                .Where(r => r.PullRequestNumber != null)
                .Where(r => r.FailureReason != RunDetails.PullRequestClosedWithoutMerge)
                .ToListAsync(cancellationToken);
        }

        int inspected = 0;
        int merges = 0;
        int failures = 0;
        int skipped = 0;
        foreach (RunDetails run in watched)
        {
            try
            {
                switch (await InspectAndActAsync(run, cancellationToken))
                {
                    case InspectionOutcome.MergeObserved:
                        inspected++;
                        merges++;
                        break;
                    case InspectionOutcome.Inspected:
                        inspected++;
                        break;
                    case InspectionOutcome.Skipped:
                        skipped++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogWarning(exception, "Closeout poll failed for run {RunId} ({Url}); will retry next sweep",
                    run.Id, run.PullRequestUrl);
            }
        }

        foreach (RunDetails run in orphaned)
        {
            try
            {
                switch (await InspectOrphanAsync(run, cancellationToken))
                {
                    case InspectionOutcome.MergeObserved:
                        inspected++;
                        merges++;
                        break;
                    case InspectionOutcome.Inspected:
                        inspected++;
                        break;
                    case InspectionOutcome.Skipped:
                        skipped++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogWarning(
                    exception, "Closeout orphan sweep failed for run {RunId} ({Url}); will retry next sweep",
                    run.Id, run.PullRequestUrl);
            }
        }

        foreach (Guid missingRunTaskId in await TasksWithMissingRunRecordsAsync(cancellationToken))
        {
            try
            {
                switch (await InspectMissingRunAsync(missingRunTaskId, cancellationToken))
                {
                    case InspectionOutcome.MergeObserved:
                        inspected++;
                        merges++;
                        break;
                    case InspectionOutcome.Inspected:
                        inspected++;
                        break;
                    case InspectionOutcome.Skipped:
                        skipped++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogWarning(
                    exception, "Closeout missing-run sweep failed for task {TaskId}; will retry next sweep",
                    missingRunTaskId);
            }
        }

        return new CloseoutSweepResult(inspected, merges, failures, skipped);
    }

    /// <summary>
    /// Done tasks carrying a pull request whose recorded run has no run record at all, or whose
    /// run record exists but will never be picked up by either RunDetails-driven query above —
    /// RunLauncher's declined-dispatch path (origin: 2026-08-28 needs-you cleanup) can complete
    /// a task's closeout without ever starting that run's stream, which leaves nothing here for
    /// the RunDetails-driven <c>orphaned</c> query above to ever find. Read from the task side
    /// instead, and deliberately not node-scoped the way the two RunDetails-driven queries above
    /// are: there is no RunDetails row to read a NodeId from, and any node reconstructing this
    /// run record races safely — Marten's StartStream in <see cref="ReconstructAndCompleteAsync"/>
    /// refuses a run id a concurrent winner already started.
    /// <para>
    /// Every task that ever merged a pull request stays in this candidate set forever (nothing
    /// clears <c>PullRequestUrl</c>/<c>CurrentRunId</c> on a Done task), unlike the two
    /// RunDetails-driven sweeps above, which are bounded by a transient run state. The
    /// <c>TaskListItem</c> read is projected to the two scalar fields
    /// <see cref="InspectMissingRunAsync"/> actually needs — it re-derives everything else from
    /// the task's own event stream — rather than materializing the full document (objective,
    /// criteria, dependency lists) for every one of them on every sweep (independent pre-PR
    /// review, cycle 1; same fix as <see cref="ReviewRerequestCountAsync"/>'s
    /// <c>ReviewRerequestScalars</c>). The matching <c>RunDetails</c> read pushes
    /// <see cref="NeedsMissingRunSweep"/>'s own terminal-state test server-side via
    /// <c>MatchesSql</c>, the same way <see cref="PollOnceAsync"/>'s own <c>orphaned</c> query
    /// does, so a candidate set that only grows with the install's history still projects to
    /// nothing heavier than an id per run (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> TasksWithMissingRunRecordsAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<MissingRunCandidate> doneWithPullRequest = await query.Query<TaskListItem>()
            .Where(t => t.MatchesSql("d.data ->> 'state' = ?", TaskState.Done.Value))
            .Where(t => t.PullRequestUrl != null && t.CurrentRunId != null)
            .Select(t => new MissingRunCandidate(t.Id, t.CurrentRunId!.Value))
            .ToListAsync(cancellationToken);

        if (doneWithPullRequest.Count == 0)
        {
            return [];
        }

        Guid[] runIds = [.. doneWithPullRequest.Select(t => t.CurrentRunId)];
        HashSet<Guid> recorded = [.. await query.Query<RunDetails>()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)];
        HashSet<Guid> needsSweep = [.. await query.Query<RunDetails>()
            .Where(r => runIds.Contains(r.Id))
            .Where(r => r.PullRequestNumber == null)
            .Where(r => r.FailureReason != RunDetails.PullRequestClosedWithoutMerge)
            .Where(r => r.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?)",
                RunState.Failed.Value, RunState.Killed.Value, RunState.Superseded.Value))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken)];

        return [.. doneWithPullRequest
            .Where(t => !recorded.Contains(t.CurrentRunId) || needsSweep.Contains(t.CurrentRunId))
            .Select(t => t.Id)];
    }

    private sealed record MissingRunCandidate(Guid Id, Guid CurrentRunId);

    /// <summary>
    /// True when no other sweep will ever complete this run's closeout on its own — the shape
    /// task 1735bffc's own real history hit: an earlier generation opened the pull request
    /// (completing the task's own <c>PullRequestUrl</c>), a later generation reused the same
    /// task and failed without ever recording that pull-request number on ITS OWN run, so
    /// neither the watched query above (wrong state entirely) nor the orphaned query above
    /// (<c>state in (Failed, Killed)</c> AND <c>PullRequestNumber != null</c>) ever reads this
    /// run again — an intact, terminal record that is nonetheless invisible to both. Completed
    /// is excluded deliberately: that state means a closeout already ran to completion, so
    /// there is nothing left here to finish. <c>PullRequestClosedWithoutMerge</c> is excluded
    /// too, the same way the orphaned query above already excludes it: without this,
    /// <see cref="InspectMissingRunAsync"/> recording that fact on an intact run's own stream
    /// (below) would never actually stop this row from matching here, since a closed-without-
    /// merge run's own <c>PullRequestNumber</c> stays null forever — costing a full aggregate
    /// replay and a live pull-request read on every sweep for a fact that will never change
    /// again (independent pre-PR review, cycle 1, adversarial).
    /// </summary>
    private static bool NeedsMissingRunSweep(RunDetails run) =>
        run.State.IsTerminal && run.State != RunState.Completed && run.PullRequestNumber is null
        && run.FailureReason != RunDetails.PullRequestClosedWithoutMerge;

    /// <summary>
    /// One read of a Done task's pull request when its own recorded run is invisible to both
    /// RunDetails-driven queries above — the companion to <see cref="InspectOrphanAsync"/> for a
    /// run that either never started at all, or started, went terminal, and never recorded its
    /// own pull-request number (<see cref="NeedsMissingRunSweep"/>). Merge-only for a wholly
    /// missing run: nothing here dispatches a follow-up or invents a close-without-merge record
    /// onto a run stream that does not exist yet, so an open or closed-without-merge pull request
    /// on that shape is a true no-op and stays needs-you until a human or a future sweep observes
    /// a merge. An intact-but-terminal run (<see cref="NeedsMissingRunSweep"/>'s other admitted
    /// shape) already has a stream to record a close onto, though, so a closed-without-merge
    /// pull request there is recorded the same way <see cref="InspectOrphanAsync"/>'s own
    /// <c>RecordClosedAsync</c> call records it — otherwise this row would cost a full task-stream
    /// replay and a live pull-request read on every sweep forever, for a fact that will never
    /// change again (independent pre-PR review, cycle 1, adversarial).
    /// <para>
    /// Applies the same two guards <c>TaskResolveCommand</c> already enforces on the run-stream
    /// path before a URL ever reaches this candidate set — a pr-review task's <c>PullRequestUrl</c>
    /// names the pull request it reviewed, never one of its own, and
    /// <see cref="PullRequestUrls.IsSafePullRequestUrl"/> refuses a URL naming a repository other
    /// than the project's own (independent pre-PR review, cycle 1, medium: this sweep read
    /// straight off the task stream with neither guard, so a foreign or pr-review URL
    /// <c>TaskResolveCommand</c> judged safe to display only because no run stream existed to
    /// protect could still reach a live inspection here).
    /// </para>
    /// </summary>
    private async Task<InspectionOutcome> InspectMissingRunAsync(Guid candidateId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? fence = await session.Events.FetchStreamStateAsync(candidateId, cancellationToken);
        if (fence is null)
        {
            return InspectionOutcome.Skipped;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            candidateId, version: fence.Version, token: cancellationToken);
        if (task is null
            || task.State != TaskState.Done
            || task.Type == TaskType.PrReview
            || task.PullRequestUrl.IsBlank()
            || task.CurrentRunId is not { } runId)
        {
            return InspectionOutcome.Skipped;
        }

        // Revalidate: this task's run may have been reconstructed, completed, or genuinely
        // dispatched by a sibling sweep, another node, or a fresh claim since the candidate
        // list was read. A run that already exists is still this sweep's to finish only when
        // it still satisfies NeedsMissingRunSweep — anything else (still live, already carrying
        // a pull-request number some other query will pick up, or already Completed) belongs
        // to a different sweep or is already done.
        RunDetails? existingRun = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (existingRun is not null && !NeedsMissingRunSweep(existingRun))
        {
            return InspectionOutcome.Skipped;
        }

        int pullRequestNumber = PullRequestUrls.ParseNumber(task.PullRequestUrl);
        if (pullRequestNumber <= 0)
        {
            return InspectionOutcome.Skipped;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null || project.IsArchived)
        {
            // An archived project (task: a project can be archived, listed as archived,
            // reactivated, and renamed) gets the same skip the render and auto-pr-review sweeps
            // already give it — this install is no longer maintaining that repository, so no gh
            // inspection, merge, or closeout event runs against it; h9k project reactivate resumes
            // this sweep for it immediately (independent pre-PR review, cycle 1, adversarial lens).
            return InspectionOutcome.Skipped;
        }

        Uri? projectRepositoryUrl = project.RepositoryUrl
            ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(
                project.RepositoryPath, cancellationToken);
        if (!PullRequestUrls.IsSafePullRequestUrl(task.PullRequestUrl, projectRepositoryUrl))
        {
            return InspectionOutcome.Skipped;
        }

        PullRequestStateSnapshot snapshot = await inspector.InspectStateAsync(
            project.RepositoryPath, task.PullRequestUrl, pullRequestNumber, cancellationToken);

        // The inspection is a slow network call; revalidate before acting, exactly as the two
        // RunDetails-driven sweeps above do.
        StreamState? current = await session.Events.FetchStreamStateAsync(candidateId, cancellationToken);
        if (current is null || current.Version != fence.Version)
        {
            logger.LogDebug(
                "Task {TaskId} advanced while inspecting its unrecorded run's pull request {Url}; deferring to the next sweep",
                candidateId, task.PullRequestUrl);
            return InspectionOutcome.Inspected;
        }

        // The archive check above is only as fresh as the moment it ran — the same slow network
        // call could just as easily straddle an archive landing mid-inspection, and a project
        // archive never advances the task stream the check above just revalidated (review thread,
        // PR #336). Re-read it here too, rather than trusting the earlier answer through the call.
        if (await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken) is not { IsArchived: false })
        {
            logger.LogDebug(
                "Project for task {TaskId} was archived while inspecting its unrecorded run's pull request {Url}; deferring to the next sweep",
                candidateId, task.PullRequestUrl);
            return InspectionOutcome.Inspected;
        }

        if (!snapshot.IsMerged)
        {
            // An intact-but-terminal run has its own stream to record a close onto — do it the
            // same way InspectOrphanAsync's own RecordClosedAsync call does, so
            // NeedsMissingRunSweep's own exclusion (above) actually stops this row from matching
            // here again. A wholly missing run (existingRun is null) has no stream yet to record
            // onto and stays the documented no-op: still open, or closed without merge, is not
            // this sweep's to act on either way, and the row's Delivered rendering already says
            // the honest thing (no run record is watching it).
            if (existingRun is not null && snapshot.IsClosed)
            {
                await RecordClosedAsync(
                    session, existingRun, project, snapshot.ClosedAt, DateTimeOffset.UtcNow, cancellationToken);
            }

            return InspectionOutcome.Inspected;
        }

        bool committed = await ReconstructAndCompleteAsync(
            session, task, project, runId, node.NodeId, node.OwnerId, snapshot.MergedAt, DateTimeOffset.UtcNow,
            cancellationToken);
        return committed ? InspectionOutcome.MergeObserved : InspectionOutcome.Inspected;
    }

    /// <summary>
    /// Completes a task's closeout for a pull request that is known to be merged, when the run
    /// that would watch it has no run record — RunLauncher's declined-dispatch path (a queued
    /// follow-up whose pull request turned out to already be merged before the run ever spawned)
    /// and <see cref="InspectMissingRunAsync"/> above both reach here. A wholly unrecorded
    /// <paramref name="runId"/> gets a minimal run record reconstructed (<see
    /// cref="RunRecordReconstructed"/>) in the SAME <see cref="IDocumentSession.SaveChangesAsync"/>
    /// call as the ordinary merged-run closeout (<see cref="CompleteCloseoutAsync"/>) that follows
    /// it, rather than two separate commits: a failure between them would otherwise leave a run
    /// reconstructed-but-not-completed forever invisible to every sweep (independent pre-PR
    /// review, cycle 1), since none of the three candidate queries admits a run in that shape. A
    /// <paramref name="runId"/> a concurrent caller has already carried all the way to <see
    /// cref="RunState.Completed"/> — the declined-dispatch path and the missing-run sweep can race
    /// the same run id, since the sweep is deliberately not node-scoped (see <see
    /// cref="TasksWithMissingRunRecordsAsync"/>) — is left alone instead: completing it a second
    /// time would double the merge comment, the handoff and the dependents-unblock. A concurrent
    /// caller still mid-race — both readers saw no run record and both tried to reconstruct it —
    /// is caught as <see cref="ExistingStreamIdCollisionException"/> from the single combined
    /// commit below: Marten refuses the whole batch when the stream id collides, so the loser's
    /// attempt lands as a no-op rather than a duplicate completion (independent pre-PR review,
    /// cycle 1, adversarial finding).
    /// <para>
    /// An intact-but-terminal <paramref name="runId"/> (<see cref="NeedsMissingRunSweep"/>'s other
    /// admitted shape) already has a stream, so <c>StartStream</c>'s own collision guard never
    /// runs for it — two sweeps racing the same owner-unscoped candidate set can both load the
    /// same not-yet-completed run and both try to complete it. The run stream's own version is
    /// fenced the same way <see cref="RerequestReviewAfterFixesAsync"/> already fences a countersign
    /// append: fetched here, before the (possibly slow) work in <see cref="CompleteCloseoutAsync"/>
    /// runs, and carried in as its expected version so the loser's commit is refused rather than
    /// silently doubling the merge comment, the handoff, and the dependents-unblock (independent
    /// pre-PR review, cycle 1, both lenses).
    /// </para>
    /// <para>
    /// Returns whether this call actually committed the closeout. A concurrent winner already
    /// having finished it (the <see cref="RunState.Completed"/> short-circuit above, the
    /// <see cref="ExistingStreamIdCollisionException"/> catch, or the
    /// <see cref="EventStreamUnexpectedMaxEventIdException"/> catch — all three mean nothing was
    /// committed by this call) returns <see langword="false"/>, so <see
    /// cref="InspectMissingRunAsync"/> does not report a merge this call did not itself observe
    /// (independent pre-PR review, cycle 1, conformance finding).
    /// </para>
    /// </summary>
    public async Task<bool> ReconstructAndCompleteAsync(
        IDocumentSession session,
        TaskAggregate task,
        ProjectDetails project,
        Guid runId,
        Guid nodeId,
        Guid ownerId,
        DateTimeOffset? mergedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        long? expectedRunVersion = null;
        if (run is null)
        {
            int pullRequestNumber = PullRequestUrls.ParseNumber(task.PullRequestUrl ?? string.Empty);
            var reconstructed = new RunRecordReconstructed(
                runId, task.Id, nodeId, ownerId, task.PullRequestUrl,
                pullRequestNumber > 0 ? pullRequestNumber : null, now);
            session.Events.StartStream<RunAggregate>(runId, reconstructed);

            // Mirrors RunDetailsProjection.Create(IEvent<RunRecordReconstructed>) — the event
            // just staged above is not committed yet, so its projection is not queryable until
            // CompleteCloseoutAsync's own SaveChangesAsync lands it alongside the closeout
            // events, which is the whole point of building the view in memory here instead of
            // saving and reloading it.
            run = new RunDetails
            {
                Id = reconstructed.Id,
                TaskId = reconstructed.TaskId,
                NodeId = reconstructed.NodeId,
                OwnerId = reconstructed.OwnerId,
                RunDirectory = RunPaths.GlobalDirectory(reconstructed.Id),
                State = RunState.Dispatched,
                DispatchedAt = reconstructed.ReconstructedAt,
                PullRequestUrl = reconstructed.PullRequestUrl,
                PullRequestNumber = reconstructed.PullRequestNumber,
            };
        }
        else if (run.State == RunState.Completed)
        {
            logger.LogDebug(
                "Run {RunId} was already completed by a concurrent closeout; not completing it twice", runId);
            return false;
        }
        else
        {
            // The stream already exists, so nothing here protects a second caller from loading
            // the identical not-yet-completed run and racing this one to CompleteCloseoutAsync's
            // own appends — see this method's own doc, second paragraph.
            StreamState? runFence = await session.Events.FetchStreamStateAsync(runId, cancellationToken);
            if (runFence is null)
            {
                logger.LogDebug(
                    "Run {RunId} disappeared between the load above and its own fence; deferring to the next sweep",
                    runId);
                return false;
            }

            expectedRunVersion = runFence.Version;
        }

        try
        {
            await CompleteCloseoutAsync(session, run, project, task, mergedAt, now, expectedRunVersion, cancellationToken);
            return true;
        }
        catch (ExistingStreamIdCollisionException)
        {
            // A concurrent winner reconstructed and completed this same run id first, inside
            // the single transaction above — not a launch or sweep failure, just a race this
            // call lost. There is nothing left here to do.
            logger.LogDebug(
                "Run {RunId} was reconstructed and completed by a concurrent closeout while this call was in flight",
                runId);
            return false;
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // The intact-run counterpart of the collision above: a concurrent sweep advanced this
            // run's stream past the version fenced just above before this call's own commit landed
            // — completed it, superseded it, or otherwise moved it — so this call's attempt is a
            // lost race rather than a fault. There is nothing left here to do.
            logger.LogDebug(
                "Run {RunId} advanced past its fenced version while this call was completing its closeout; "
                + "another sweep got there first", runId);
            return false;
        }
    }

    /// <summary>
    /// One read of a pull request nothing is watching any more, for the sole purpose of
    /// finding out whether it merged or closed (Decisions Log #72). This is deliberately
    /// thinner than <see cref="InspectAndActAsync"/>: a Failed run is not a run anyone is
    /// driving, so a failing check or an unresolved thread here dispatches nothing and parks
    /// nothing — the row's existing needs-you rendering (<c>AttentionComposer.Delivered</c>'s
    /// Failed arm) already says the honest thing, and inventing a follow-up onto a dead run's
    /// branch is not this sweep's job. A merge or a close is recorded exactly as the watched
    /// path records it, because both are facts the row's rendering and the orphan query's own
    /// exclusion filter (see <c>PollOnceAsync</c>) depend on; a still-open answer is the only
    /// true no-op.
    /// </summary>
    private async Task<InspectionOutcome> InspectOrphanAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        StreamState? fence = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (fence is null)
        {
            return InspectionOutcome.Skipped;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            run.TaskId, version: fence.Version, token: cancellationToken);
        if (task is null)
        {
            return InspectionOutcome.Skipped;
        }

        // The archive check runs ahead of the CurrentRunId branch below (independent review, PR
        // #336): that branch appends RunSuperseded on its own, unfenced by anything here, and an
        // archived project (task: a project can be archived, listed as archived, reactivated, and
        // renamed) gets the same skip the render and auto-pr-review sweeps already give it before
        // any event lands on any of its streams — this install is no longer maintaining that
        // repository, so no gh inspection, merge, or closeout event runs against it;
        // h9k project reactivate resumes this sweep for it immediately.
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null || project.IsArchived)
        {
            return InspectionOutcome.Skipped;
        }

        // A newer run owns this task's pull request now; this Failed run's own history is
        // no longer the task's current story and there is nothing here to complete. Retire
        // it the same way the watched path does (InspectAndActAsync) so it stops matching
        // the orphan query on every future sweep — otherwise a Failed run superseded by
        // `h9k pr resolve` would sit in this candidate set, paying a stream fetch and a full
        // aggregate replay forever, for a task-state mismatch that will never change.
        if (task.CurrentRunId != run.Id)
        {
            if (task.CurrentRunId is not null)
            {
                session.Events.Append(run.Id, new RunSuperseded(run.Id, task.LeaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
            }

            return InspectionOutcome.Skipped;
        }

        // Blocked is admitted alongside Done: task.CurrentRunId == run.Id already narrowed this
        // to the one case a Blocked task can still carry it — Apply(TaskReopened) landed here
        // behind a still-open dependency and kept CurrentRunId pointing at this run rather than
        // nulling it, precisely so its own merge/close detection keeps running while nothing else
        // dispatches a follow-up (adversarial review, cycle 1, on h9k task start).
        if ((task.State != TaskState.Done && task.State != TaskState.Blocked)
            || task.PullRequestUrl.IsBlank() || run.PullRequestNumber is not > 0)
        {
            return InspectionOutcome.Skipped;
        }

        // State-only: this sweep never dispatches a follow-up onto a dead run, so the
        // reviews-and-checks half of a full InspectAsync (a second remote read while the
        // PR is still open — GitHubPullRequestInspector.cs's own InspectReviewsAsync
        // call) would spend a read this method has no use for.
        PullRequestStateSnapshot snapshot = await inspector.InspectStateAsync(
            project.RepositoryPath, task.PullRequestUrl, run.PullRequestNumber.Value, cancellationToken);

        // The inspection is a slow network call; revalidate before acting; see the identical
        // guard in InspectAndActAsync.
        StreamState? current = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (current is null || current.Version != fence.Version)
        {
            logger.LogDebug(
                "Task {TaskId} advanced while inspecting the orphaned pull request {Url}; deferring to the next sweep",
                run.TaskId, run.PullRequestUrl);
            // gh already answered above — only the read is discarded as stale, so this is
            // evidence gh is healthy, not a run this sweep passed over without calling it
            // (independent pre-PR review, cycle 2 adversarial).
            return InspectionOutcome.Inspected;
        }

        // The archive check above is only as fresh as the moment it ran — the same slow network
        // call could just as easily straddle an archive landing mid-inspection, and a project
        // archive never advances the task stream the check above just revalidated (review thread,
        // PR #336). Re-read it here too, rather than trusting the earlier answer through the call.
        if (await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken) is not { IsArchived: false })
        {
            logger.LogDebug(
                "Project for task {TaskId} was archived while inspecting the orphaned pull request {Url}; deferring to the next sweep",
                run.TaskId, run.PullRequestUrl);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.IsMerged)
        {
            await CompleteCloseoutAsync(
                session, run, project, task, snapshot.MergedAt, DateTimeOffset.UtcNow, expectedVersion: null, cancellationToken);
            return InspectionOutcome.MergeObserved;
        }

        if (snapshot.IsClosed)
        {
            // Closed without a merge: record it the same way the watched path does
            // (RecordClosedAsync), so FailureReason becomes PullRequestClosedWithoutMerge —
            // otherwise AttentionComposer.UnwatchedRemedy keeps pointing the human at
            // `h9k pr resolve`, which reopens the task onto a pull request nobody can merge,
            // and this row would keep matching the orphan query's exclusion filter forever.
            await RecordClosedAsync(session, run, project, snapshot.ClosedAt, DateTimeOffset.UtcNow, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        // Still open: the row already renders exactly that — nothing is invented.
        return InspectionOutcome.Inspected;
    }

    private async Task<InspectionOutcome> InspectAndActAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Fence before aggregating (the DispatchEngine order): the reopen below carries
        // this version as expectedVersion, so a task-stream write landing after this
        // point — h9k pr resolve above all — fails the commit instead of being silently
        // absorbed by a version fetched too late.
        StreamState? fence = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (fence is null)
        {
            return InspectionOutcome.Skipped;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            run.TaskId, version: fence.Version, token: cancellationToken);
        if (task is null)
        {
            return InspectionOutcome.Skipped;
        }

        // The archive check runs ahead of the CurrentRunId branch below (independent review, PR
        // #336): that branch appends RunSuperseded on its own, unfenced by anything here, and an
        // archived project (task: a project can be archived, listed as archived, reactivated, and
        // renamed) gets the same skip the render and auto-pr-review sweeps already give it before
        // any event lands on any of its streams — this install is no longer maintaining that
        // repository, so no gh inspection, merge, or closeout event runs against it;
        // h9k project reactivate resumes this sweep for it immediately.
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null || project.IsArchived)
        {
            return InspectionOutcome.Skipped;
        }

        // A newer run owns this task's PR now (a follow-up pushed after this one) — this
        // run's watch is over; retire it so the watch set stays bounded.
        if (task.CurrentRunId != run.Id)
        {
            if (task.CurrentRunId is not null)
            {
                session.Events.Append(run.Id, new RunSuperseded(run.Id, task.LeaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
            }

            return InspectionOutcome.Skipped;
        }

        // Only a Done task is ordinarily in closeout; a reopened one that landed Queued has a
        // follow-up in flight instead. Blocked is admitted alongside Done for the one case that
        // still carries this exact run's id: Apply(TaskReopened) landed here behind a still-open
        // dependency and kept CurrentRunId pointing at this run rather than nulling it (the check
        // above already narrowed CurrentRunId == run.Id to reach this line), precisely so merge/close
        // detection keeps running while nothing else dispatches a follow-up (adversarial review,
        // cycle 1, on h9k task start).
        if ((task.State != TaskState.Done && task.State != TaskState.Blocked)
            || task.PullRequestUrl.IsBlank() || run.PullRequestNumber is not > 0)
        {
            return InspectionOutcome.Skipped;
        }

        PullRequestSnapshot snapshot = await inspector.InspectAsync(
            project.RepositoryPath, task.PullRequestUrl, run.PullRequestNumber.Value, cancellationToken);

        // The inspection is a slow network call. Revalidate the fence before acting: a
        // reopen that landed mid-call may already have a follow-up agent working in the
        // reused worktree, and the merged/closed paths below touch the filesystem with
        // no expectedVersion to protect them. Deferring one sweep is always safe.
        StreamState? current = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (current is null || current.Version != fence.Version)
        {
            logger.LogDebug(
                "Task {TaskId} advanced while inspecting {Url}; deferring to the next sweep",
                run.TaskId, run.PullRequestUrl);
            // gh already answered above — only the read is discarded as stale, so this is
            // evidence gh is healthy, not a run this sweep passed over without calling it
            // (independent pre-PR review, cycle 2 adversarial).
            return InspectionOutcome.Inspected;
        }

        // The archive check above is only as fresh as the moment it ran — the same slow network
        // call could just as easily straddle an archive landing mid-inspection, and a project
        // archive never advances the task stream the check above just revalidated (review thread,
        // PR #336). Re-read it here too, rather than trusting the earlier answer through the call.
        if (await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken) is not { IsArchived: false })
        {
            logger.LogDebug(
                "Project for task {TaskId} was archived while inspecting {Url}; deferring to the next sweep",
                run.TaskId, run.PullRequestUrl);
            return InspectionOutcome.Inspected;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot.IsMerged)
        {
            await CompleteCloseoutAsync(
                session, run, project, task, snapshot.MergedAt, now, expectedVersion: null, cancellationToken);
            return InspectionOutcome.MergeObserved;
        }

        if (snapshot.IsClosed)
        {
            await RecordClosedAsync(session, run, project, snapshot.ClosedAt, now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        // Recorded every sweep the pull request is still open, ahead of every branch below:
        // the Delivered phase line reads this regardless of which finding (or none) the rest
        // of this method goes on to act on (origin: PR #50 sat Delivered for 23 minutes with a
        // landed Copilot review nobody had read before the merge).
        await RecordExternalReviewObservationAsync(session, run, snapshot, now, cancellationToken);

        // A parked run gets merge/close detection only; dispatch decisions were handed
        // to the human when the automatic budget ran out.
        if (run.State == RunState.CloseoutParked)
        {
            return InspectionOutcome.Inspected;
        }

        // Checked ahead of the conflict read, and therefore ahead of checks and threads too
        // (task: a stacked pull-request edge exists as an explicit opt-in dependency). A stacked
        // child whose parent branch has moved is the strictly earlier fact: GitHub will often
        // report the child CONFLICTING as a consequence, and the mechanical rebase below already
        // refuses to act on a pull request whose base is not the project's own — so without this
        // check first, the correct operation (a replay onto the parent's new head, or onto the
        // project's base once the parent merged) would never be reached, and the child would take
        // a full review lap for a conflict that is not its own to resolve.
        //
        // The cheap local test runs first and covers every ordinary run: only a pull request whose
        // recorded base is not the project's own can be a stacked child at all, so nothing here
        // costs an unstacked pull request a single git or provider call.
        if (StackedParentWatch.IsStackedChild(run, project)
            && await TryReplayStackedChildAsync(
                session, task, run, project, fence.Version, snapshot, now, cancellationToken))
        {
            return InspectionOutcome.Inspected;
        }

        // Checked ahead of checks and review threads, deliberately (backlog 44, origin PR
        // 26): a conflicting branch makes both of those readings moot — CI ran against a
        // diff that is about to be superseded by a rebase, and a review thread answers a
        // version of the code the merge will discard. The observation is GitHub's own
        // mergeable read, never inferred from how long the branch has sat open.
        if (snapshot.IsConflicting)
        {
            // Recommendation 3 (idea fc85f609, amended by Brian 2026-09-04): a mechanical
            // fetch+rebase+push, no model session and no local gates, is tried first — GitHub's
            // own CI on the push is the authoritative gate here, so a local build/test run would
            // only duplicate it. A clean apply is never reopened for; anything else (a real
            // conflict, a missing or unusable worktree, a refused push) falls back byte-for-byte
            // to the reopen-and-review lap below, unchanged.
            //
            // GitHub's own CONFLICTING read is against the pull request's ACTUAL base, which a
            // human can retarget away from project.BaseBranch on GitHub itself (the stacked-PR
            // shape this platform now declares explicitly, Decisions Log #144). Rebasing onto
            // project.BaseBranch in that case would not address what GitHub reads as conflicting,
            // could still "make progress" and force-push (the no-progress guard below only catches
            // the narrower case where the branch already contains origin/<base>'s tip), and would
            // silently rewrite the branch onto a base it was never meant to be on — so the
            // mechanical attempt is skipped outright, without ever fetching or rebasing, whenever
            // GitHub reports a base other than the project's own (independent pre-PR review,
            // cycle 1, adversarial lens).
            // BaseRefName null (a provider read that predates this field) proceeds exactly as
            // before rather than guessing at a mismatch that was never observed.
            MechanicalRebaseOutcome mechanical =
                snapshot.BaseRefName is not null && snapshot.BaseRefName != project.BaseBranch
                    ? new MechanicalRebaseOutcome(
                        false,
                        $"the pull request's actual base is {snapshot.BaseRefName}, not this project's own base "
                        + $"branch {project.BaseBranch} — a mechanical rebase onto {project.BaseBranch} would not "
                        + "address what GitHub reads as conflicting",
                        null)
                    : await TryMechanicalRebaseAsync(session, fence.Version, project, run, cancellationToken);
            session.Events.Append(run.Id, new PullRequestMechanicalRebaseAttempted(
                run.Id, mechanical.Succeeded, mechanical.Detail, mechanical.PushedCommit, now));

            if (mechanical.Succeeded)
            {
                // The run stays exactly where it was (AwaitingReview): the very next sweep
                // re-inspects the newly pushed head and naturally observes whatever GitHub's CI
                // reports for it — all green needs nothing further here, a failing check reopens
                // through the ordinary failing-checks obstruction below, unchanged.
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Task {TaskId}: pull request {Url} rebased mechanically onto its base and force-pushed "
                    + "cleanly — no reopen, watching for GitHub's own check result",
                    task.Id, task.PullRequestUrl);
                return InspectionOutcome.Inspected;
            }

            session.Events.Append(run.Id, new PullRequestConflictObserved(run.Id, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.Rebase,
                [snapshot.HeadCommit ?? "unknown-head"],
                snapshot,
                $"The pull request's branch conflicts with its base branch. The mechanical rebase "
                + $"fell back to a full review lap: {mechanical.Detail}",
                // Rebase is excluded from the opening-cycle review scope seed (Brian's 2026-09-04
                // triage ruling governs that path unchanged): the follow-up's own job is resolving
                // the conflict, not a diff a reviewer would read against a prior push.
                now, pullRequestHeadSha: null, stackReplayUpstreamCommit: null, stackReplayOntoCommit: null,
                predecided: null, changesRequestedReviews: null,
                cancellationToken);
            return InspectionOutcome.Inspected;
        }

        // Review feedback is detected ahead of every CI read below, and the pending-checks
        // short-circuit that used to precede it now yields to it (Brian's ruling, 2026-09-09
        // 09:25 EDT, Decisions Log #164): a broken CI may be what the review
        // found, so the fix lap must be allowed to run. Origin incident: arx-platform PR #2042,
        // whose .NET Framework build stage lost its hosted agent and never received a final check
        // status, so the check read pending for nine hours while four Copilot threads sat
        // unaddressed and every sweep stopped at that gate.
        //
        // Computed before the short-circuit rather than inside the thread branch below, because
        // the short-circuit's own condition now depends on the answer: a thread set this run has
        // already answered in full is not feedback a lap could act on, so it must not buy the
        // incomplete CI picture a dispatch that would spend a lap and get nothing for it.
        IReadOnlyList<string> outstandingThreadIds = OutstandingReviewThreadIds(run, snapshot);
        bool threadsNeedALap = snapshot.UnresolvedReviewThreadCount > 0
            // snapshot.ThreadIds.Count == 0 is a deliberate extra arm, not a redundant one: the
            // provider read always populates it 1:1 with UnresolvedReviewThreadCount
            // (GitHubPullRequestInspector.ReadReviewObservation), but nothing here can tell an
            // inspector reading that ids the count > 0 came before them existed
            // (FakeInspector.Quiet() with { UnresolvedReviewThreadCount = N } and no ids, used
            // throughout this class's own tests) — the empty answer out of
            // OutstandingReviewThreadIds is identical for "nothing outstanding" and "no id-level
            // data to compare against", and only the id list can tell them apart. Without this
            // arm a bare count would read as fully accounted for and never dispatch.
            && (snapshot.ThreadIds.Count == 0 || outstandingThreadIds.Count > 0);
        bool reviewFeedbackNeedsALap = snapshot.ChangesRequested.Count > 0 || threadsNeedALap;

        if (snapshot.HasPendingChecks && !reviewFeedbackNeedsALap)
        {
            // The CI picture is incomplete and no review feedback is waiting behind it; acting on
            // the checks now would hand a follow-up run a partial failure list. The next sweep
            // sees the full result, and the merge stays held meanwhile either way — this return
            // sits ahead of TryAutoMergeAsync exactly as it always has.
            return InspectionOutcome.Inspected;
        }

        // Failing checks observed on the SAME sweep as review feedback ride in that one lap rather
        // than buying a second one: the sentence the reopen records — and so the follow-up's own
        // prompt — names both, and the lap's own post-fix gates decide whether CI is green
        // afterwards. The obstruction identity deliberately stays the review feedback's own
        // (the thread ids, the review urls) and never folds the check names in: a check flipping
        // between failing and pending would otherwise read as a fresh obstruction every sweep and
        // hand the progress cap an endless supply of laps to grant.
        bool failingChecksRideAlong = reviewFeedbackNeedsALap && snapshot.FailingChecks.Count > 0;
        // The list is named as observed, and said to be possibly incomplete where it genuinely is:
        // with other checks still reporting, the very partial-failure-list concern the short-circuit
        // above encodes applies to this rider too. It is no longer a reason to withhold the lap —
        // the review feedback earns it either way — but handing a session a partial list as though
        // it were the whole picture would be the guess the never-guess rule forbids, and a lap that
        // reads "these and possibly more" runs its own full gates rather than stopping at two.
        string failingChecksRider = failingChecksRideAlong
            ? $" CI checks are failing on the same pull request: {string.Join(", ", snapshot.FailingChecks)}."
                + (snapshot.HasPendingChecks
                    ? " Other checks were still reporting when this was observed, so that list may be incomplete."
                    : string.Empty)
                + " Fix them in this same lap; the gates you run before you finish are what decide "
                + "whether CI is green afterwards."
            : string.Empty;
        // Recorded whether or not this lap is the checks' own: the sweep observed them failing, and
        // an observation the record drops is one no reader can tell from a green pull request
        // (AGENTS.md, never guess at unobserved facts). Appended ahead of the review-feedback event
        // below so the run's own state lands on what the dispatched lap actually answers.
        if (failingChecksRideAlong)
        {
            session.Events.Append(run.Id, new PullRequestChecksFailed(run.Id, snapshot.FailingChecks, now));
        }

        // Checked ahead of the thread count, deliberately (task: a changes-requested pull-request
        // review from a human becomes a fix lap). A person's changes-requested review ordinarily
        // opens threads too, so with the thread branch first every such review would take the
        // automated thread path — which replies to a human's disagreement itself, the one thing
        // this lap exists to stop (Brian's ruling, 2026-09-06 12:15). The narrower fact wins, and
        // the thread branch below keeps every case this one does not claim: a comment-only review,
        // a bot's review, and threads left behind with no review state at all.
        if (snapshot.ChangesRequested.Count > 0)
        {
            IReadOnlyList<ChangesRequestedReview> changesRequested = snapshot.ChangesRequested;
            session.Events.Append(run.Id, new PullRequestChangesRequested(run.Id, changesRequested, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.ReviewRequestedChanges,
                // The reviews themselves are the obstruction identity: a review's url is unique to
                // it, so the SAME reviewer submitting a fresh review after this lap pushes reads as
                // a different obstruction and earns its own lap, while a lap that pushed nothing
                // the reviewer accepted re-reads the identical url and spends the progress cap. The
                // reviewer login would collapse those two; the finding text would make an edited
                // comment a new obstruction.
                [.. changesRequested.Select(review => review.ReviewUrl)],
                snapshot,
                DescribeChangesRequested(changesRequested) + failingChecksRider,
                // The pull request head this sweep just observed — the follow-up's own opening
                // Discovery cycle seeds its diff instruction from it (task: a lap reviews only what
                // it changed), so the reviewer reads the fix rather than the whole branch again.
                now, snapshot.HeadCommit, stackReplayUpstreamCommit: null, stackReplayOntoCommit: null,
                predecided: null,
                changesRequestedReviews: changesRequested,
                cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (threadsNeedALap)
        {
            session.Events.Append(run.Id, new ReviewFeedbackReceived(
                run.Id, snapshot.UnresolvedReviewThreadCount, now, snapshot.UnresolvedHumanThreadCount));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.ReviewFeedback,
                outstandingThreadIds,
                snapshot,
                DescribeUnresolvedThreads(snapshot) + failingChecksRider,
                // Same reasoning as the changes-requested branch above: seed the follow-up's own
                // opening Discovery cycle from the pull request head this sweep just observed.
                now, snapshot.HeadCommit, stackReplayUpstreamCommit: null, stackReplayOntoCommit: null,
                predecided: null, changesRequestedReviews: null,
                cancellationToken);
            return InspectionOutcome.Inspected;
        }

        // No review feedback needs a lap, so what is left is the checks on their own — and the
        // short-circuit above already returned if the CI picture was still incomplete, so this
        // branch reads a settled result exactly as it always has.
        if (snapshot.FailingChecks.Count > 0)
        {
            session.Events.Append(run.Id, new PullRequestChecksFailed(run.Id, snapshot.FailingChecks, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.FailingChecks,
                snapshot.FailingChecks,
                snapshot,
                $"CI checks failing on the pull request: {string.Join(", ", snapshot.FailingChecks)}.",
                // Same reasoning as the two review branches above: seed the follow-up's own opening
                // Discovery cycle from the pull request head this sweep just observed.
                now, snapshot.HeadCommit, stackReplayUpstreamCommit: null, stackReplayOntoCommit: null,
                predecided: null, changesRequestedReviews: null,
                cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.UnresolvedReviewThreadCount > 0)
        {
            // Nothing left that a follow-up could act on: every unresolved thread is one this run
            // already triaged as decline or route and replied to, waiting only on the human it was
            // left open for. A visible wait, not a park (design ruling 3's own terms) — the next
            // sweep re-reads and either finds the human closed it, or reads this exact same set and
            // takes this same branch again, spending nothing either time. Placed after the
            // failing-checks branch above, not in front of it: a run resting on threads it has
            // already answered still owes a lap for a check that failed, and returning here first
            // would swallow it.
            return InspectionOutcome.Inspected;
        }

        if (snapshot.ErroredReview is { } erroredReview)
        {
            await RerequestReviewOrParkAsync(
                session, task, run, project.RepositoryPath, task.PullRequestUrl,
                run.PullRequestNumber.Value, snapshot, erroredReview, now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        // Nothing needs answering: the checks pass, every thread is resolved, and the
        // review that produced them was real. That is the moment a countersign is worth
        // asking for, and the only moment it is.
        bool reRequestedThisSweep = await RerequestReviewAfterFixesAsync(
            session, run, project, fence.Version, task.PullRequestUrl, run.PullRequestNumber.Value,
            snapshot, now, cancellationToken);

        // The owner's standing pre-approval (task: a task can be published pre-approved) only
        // ever reads as a green light here — after every obstruction above (conflicting, pending
        // or failing checks, unresolved threads, an errored review) has already had its own say.
        // Reaching this line with pre-approval in either automatic mode is the "nothing else needs
        // a human, and nothing else needs an agent" moment the feature exists for — unless this same sweep
        // just re-requested a countersign above: that request was issued against this exact
        // snapshot, so merging on the snapshot's now-stale "no outstanding reviewer" reading
        // would merge past the very review this sweep just asked for. The next sweep reads the
        // request as an outstanding reviewer and decides fresh (independent pre-PR review,
        // cycle 1, both lenses).
        if (task.PreApproval.MergesAutomatically && !reRequestedThisSweep)
        {
            return await TryAutoMergeAsync(
                session, task, run, project, fence.Version, snapshot, now, cancellationToken);
        }

        return InspectionOutcome.Inspected;
    }

    /// <summary>
    /// The pre-approved auto-merge gate (task: a task can be published pre-approved). Reached only
    /// once every obstruction <see cref="InspectAndActAsync"/> checks ahead of it has already
    /// cleared — CI green, no unresolved thread, no errored review, no conflict — so what remains
    /// is exactly the four gates the acceptance criteria name: the review decision, outstanding
    /// requested reviewers (Copilot handled on its own bounded settle window), and the fourth gate
    /// enforced by construction rather than by a check written here — this method runs only when
    /// <c>task.State</c> is Done (or the Blocked-behind-a-still-open-dependency edge case) with
    /// <c>task.CurrentRunId == run.Id</c>, which is already "no follow-up live or queued for this
    /// task", and the task-stream fence was revalidated moments ago against a snapshot read after
    /// that fence, which is already "the head postdates the last observation this sweep trusts".
    /// <para>
    /// A required human approval or an outstanding human reviewer is a visible waiting state, never
    /// a park (design ruling 3): the daemon does nothing but let <see cref="RecordExternalReviewObservationAsync"/>'s
    /// own append (already run, above) keep <c>h9k status</c> honest, and returns to wait for the
    /// next sweep. A requested Copilot review is the one case with a clock: it either lands (which
    /// flows into the ordinary thread-triage path above on ITS OWN next sweep) or, past the bounded
    /// settle window, parks with that reason instead of waiting forever (design ruling 2).
    /// </para>
    /// <para>
    /// <see cref="PreApprovalMode.AfterHumanReview"/> adds two more gates behind those four (task:
    /// the people a pull request is waiting on are named, and pre-approval gains a mode that waits
    /// for human review): at least one human reviewer must have been requested on the pull request
    /// at some point, and every requested reviewer must have approved the current head. They are
    /// checked last because they are strictly narrower — a plain pre-approved task can merge before
    /// any person has been asked at all, and closing that gap is the whole of the mode. Both are
    /// visible waits with no clock, on the same design-ruling-3 terms as the human-approval wait
    /// above: the owner adds a reviewer on GitHub, or flips the mode to
    /// <see cref="PreApprovalMode.On"/>, and the next sweep decides fresh.
    /// </para>
    /// </summary>
    private async Task<InspectionOutcome> TryAutoMergeAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        ProjectDetails project,
        long taskFenceVersion,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // A stacked child cannot merge before its parent (task: a stacked pull-request edge exists
        // as an explicit opt-in dependency). Reaching this line means the check that owns the
        // stacked path above found the child still aligned with a parent that has NOT merged — an
        // un-retargeted pull request, aimed at the parent's branch. Merging it there would land the
        // child's commits on the parent's branch, which is not a merge into anything this project
        // ships from. A visible wait rather than a park, on exactly the design-ruling-3 terms this
        // method already applies to an outstanding human reviewer: nothing is wrong, something is
        // simply owed first, and the retarget that clears it is a sweep away.
        if (StackedParentWatch.IsStackedChild(run, project))
        {
            logger.LogInformation(
                "Task {TaskId}: pre-approved, but its pull request is still stacked on {ParentBranch} rather "
                + "than {BaseBranch} — not at the merge bar until its parent merges and it is retargeted",
                task.Id, run.BaseBranch, project.BaseBranch);
            return InspectionOutcome.Inspected;
        }

        if (!snapshot.HasObservedChecks)
        {
            DateTimeOffset lastPush = LastHeadPushObservedAt(run);
            if (now - lastPush < _options.ChecksRegistrationSettleWindow)
            {
                // GitHub has not reported a single check run yet for this head — indistinguishable
                // from a repository with no CI configured at all until the settle window elapses.
                // A visible wait, not a park: the next sweep re-reads and either finds real checks
                // (which flow into the ordinary HasPendingChecks/FailingChecks branches above on
                // THEIR OWN next sweep) or, once the window is spent, the continued silence is
                // trusted as "no CI" (independent pre-PR review, cycle 1, adversarial finding).
                return InspectionOutcome.Inspected;
            }
        }

        if (snapshot.ReviewThreadsTruncated)
        {
            // GitHub's own 100-thread page cap left real threads unread, so
            // UnresolvedReviewThreadCount reading zero here means only "the first 100 happened to
            // be resolved", not "every thread is resolved". Nothing here can ever change that
            // (the pull request will always carry more than 100 threads), so — unlike the settle
            // windows above — this parks immediately rather than waiting out a clock that will
            // never run out (independent pre-PR review, cycle 1, adversarial finding).
            await ParkAsync(
                session, run,
                "Pre-approved, but this pull request carries more review threads than the provider "
                + "read can see (GitHub's own 100-thread page cap) — the platform cannot tell whether "
                + "every thread is actually resolved, and that will not change on its own. Review the "
                + "threads directly on GitHub and merge it by hand, or turn off pre-approval with "
                + "h9k task set-pre-approved off so ordinary human-supervised closeout takes over.",
                now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.CopilotReviewState == ExternalReviewState.RequestedPending)
        {
            DateTimeOffset pendingSince = run.CopilotReviewRequestPendingSince ?? now;
            if (now - pendingSince < _options.CopilotReviewSettleWindow)
            {
                // Still within the settle window: a visible wait, not a park — the next sweep
                // checks again.
                return InspectionOutcome.Inspected;
            }

            string parkReason =
                $"Pre-approved, but Copilot's requested review never arrived within the "
                + $"{_options.CopilotReviewSettleWindow.TotalHours:0.#}-hour settle window (requested "
                + $"{pendingSince:u}). Re-request the review by hand, merge without it, or grant "
                + "another attempt with h9k pr resolve.";
            await ParkAsync(session, run, parkReason, now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (!snapshot.ReviewDecisionSatisfied || snapshot.HasOutstandingHumanReviewer)
        {
            // Waiting on human approval — visible (AttentionComposer reads the ExternalReviewObserved
            // already recorded above), never nudged, never parked (design ruling 3): the owner
            // takes whatever social action they choose, on their own initiative.
            return InspectionOutcome.Inspected;
        }

        // The two extra gates PreApprovalMode.AfterHumanReview adds (task: the people a pull
        // request is waiting on are named, and pre-approval gains a mode that waits for human
        // review), checked last because they are strictly narrower than everything above: plain
        // pre-approval can merge before any person has been brought into the loop at all, which is
        // the gap this mode closes. Both are visible waits on design ruling 3's own terms, never
        // parks — nothing is wrong, a person simply has not spoken yet, and the owner's two levers
        // (add a reviewer on GitHub, or h9k task set-pre-approved on) both land on the next sweep.
        //
        // Hall9k requests no review of its own here, deliberately: reviewers are assigned in
        // GitHub, by humans, and a platform that added one to satisfy its own gate would be
        // approving its own work by proxy.
        if (task.PreApproval.WaitsForHumanReview)
        {
            if (!snapshot.HasEverRequestedHumanReviewer)
            {
                logger.LogInformation(
                    "Task {TaskId}: pre-approved after-human-review, but no human reviewer has ever been "
                    + "requested on {Url} — waiting for the owner to add one, or to flip the mode with "
                    + "h9k task set-pre-approved {TaskId} on",
                    task.Id, task.PullRequestUrl, task.Id);
                return InspectionOutcome.Inspected;
            }

            if (snapshot.HumanReviewersAwaitingApproval.Count > 0)
            {
                logger.LogInformation(
                    "Task {TaskId}: pre-approved after-human-review, waiting on {Reviewers} to approve "
                    + "{HeadCommit} on {Url}",
                    task.Id, string.Join(", ", snapshot.HumanReviewersAwaitingApproval),
                    snapshot.HeadCommit ?? "the current head", task.PullRequestUrl);
                return InspectionOutcome.Inspected;
            }
        }

        if (task.MechanicalResolutionAttempts >= _options.MaxMechanicalResolutionAttempts)
        {
            string parkReason =
                $"Pre-approved merge attempts spent ({task.MechanicalResolutionAttempts}/"
                + $"{_options.MaxMechanicalResolutionAttempts}): {DescribeAutoMergeFailure(run)}. "
                + "Merge it by hand, or grant another attempt with h9k pr resolve.";
            await ParkAsync(session, run, parkReason, now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        try
        {
            await inspector.MergeAsync(
                project.RepositoryPath, task.PullRequestUrl!, run.PullRequestNumber!.Value, snapshot.HeadCommit,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            session.Events.Append(run.Id, new PullRequestAutoMergeAttempted(run.Id, false, exception.Message, now));
            session.Events.Append(task.Id, expectedVersion: taskFenceVersion + 1,
                new TaskMechanicalResolutionAttempted(task.Id, exception.Message, now));
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Run {RunId}: pre-approved merge attempt failed ({Spent}/{Max}): {Reason}",
                run.Id, task.MechanicalResolutionAttempts + 1, _options.MaxMechanicalResolutionAttempts,
                exception.Message);
            return InspectionOutcome.Inspected;
        }

        session.Events.Append(run.Id, new PullRequestAutoMergeAttempted(run.Id, true, null, now));
        // The same deterministic transition an observed operator merge produces — Done on the
        // observed merge, closeout comment only, GitHub issue never closed, Jira never transitioned
        // (design ruling 8) — since this daemon code IS the merge, not an inspection of one that
        // already happened elsewhere. mergedAt is null rather than this sweep's own `now`: nothing
        // here re-reads GitHub's own merge timestamp after the call above, and CompleteCloseoutAsync's
        // own contract for that parameter is "what GitHub reported, not when this node noticed" — the
        // ObservedAt argument that follows is what the daemon-noticed timestamp is for (independent
        // pre-PR review, cycle 1, adversarial finding).
        await CompleteCloseoutAsync(session, run, project, task, mergedAt: null, now, expectedVersion: null, cancellationToken);
        logger.LogInformation(
            "Task {TaskId}: pre-approved pull request {Url} merged automatically", task.Id, task.PullRequestUrl);
        return InspectionOutcome.MergeObserved;
    }

    /// <summary>The itemized reason a mechanical-resolution park names — the last attempt's own failure, or an honest gap for a stream older than this field.</summary>
    private static string DescribeAutoMergeFailure(RunDetails run) =>
        run.LastAutoMergeFailureReason.IsNotBlank()
            ? run.LastAutoMergeFailureReason
            : "the last attempt's own reason was not recorded";

    /// <summary>
    /// When the run's current head was last actually pushed — the anchor the checks-registration
    /// settle window measures from. A clean mechanical rebase (<see cref="RunDetails.LastMechanicalRebaseSucceeded"/>)
    /// force-pushes a new head without ever appending <see cref="PullRequestOpened"/> or
    /// <see cref="PullRequestUpdated"/> (the run deliberately stays AwaitingReview for that path, see
    /// the comment above <see cref="TryMechanicalRebaseAsync"/>'s own call site), so its own
    /// <see cref="RunDetails.LastMechanicalRebaseAt"/> wins whenever it postdates the opener's own
    /// <see cref="RunDetails.PullRequestPushedAt"/>. <see cref="RunDetails.DispatchedAt"/> is the
    /// last fallback, for a run recorded before either field existed.
    /// </summary>
    private static DateTimeOffset LastHeadPushObservedAt(RunDetails run) =>
        run.LastMechanicalRebaseSucceeded == true && run.LastMechanicalRebaseAt is { } rebasedAt
            && (run.PullRequestPushedAt is null || rebasedAt > run.PullRequestPushedAt)
            ? rebasedAt
            : run.PullRequestPushedAt ?? run.DispatchedAt;

    /// <summary>
    /// One append per change: the post-PR review watcher's fact only lands on the run stream
    /// when it actually moved, so a quiet pull request does not grow a same-state event every
    /// sweep (mirrors the errored-review dedup in RerequestReviewOrParkAsync). Read only by
    /// the Delivered phase line — never a task lifecycle status, never a driver of RunState.
    /// <para>
    /// <c>ChecksPending</c> is included in the dedup comparison as its own axis (independent
    /// pre-PR review, cycle 3): a sweep where only the CI picture completed — same review state,
    /// same thread count — is exactly the transition the Delivered surfaces need in order to
    /// stop caveating a landed review, so it must land its own event even when nothing else
    /// changed.
    /// </para>
    /// <para>
    /// <c>ReviewDecision</c> and <c>OutstandingReviewerLogins</c> join the comparison for the
    /// identical reason (task: a task can be published pre-approved): a pre-approved task's
    /// "waiting on human approval" display reads these two off <see cref="RunDetails"/>, so a
    /// change in either — a reviewer approving, a new one being requested — must land its own
    /// event even when Copilot's own state is unchanged.
    /// </para>
    /// <para>
    /// The three named-reviewer facts join it on the same terms (task: the people a pull request is
    /// waiting on are named, and pre-approval gains a mode that waits for human review): who
    /// requested changes, whether a human review has ever been requested, and which requested
    /// reviewers have not approved the head are exactly what the waiting line names, so a sweep
    /// where only one of them moved — the last outstanding reviewer approving, a request being
    /// withdrawn — is a sweep the display needs to see.
    /// </para>
    /// </summary>
    private async Task RecordExternalReviewObservationAsync(
        IDocumentSession session,
        RunDetails run,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (snapshot.CopilotReviewState == run.ExternalReviewState
            && snapshot.CopilotReviewThreadCount == run.ExternalReviewThreadCount
            && snapshot.HasPendingChecks == run.ExternalReviewChecksPending
            && snapshot.ReviewDecision == run.ExternalReviewDecision
            && snapshot.OutstandingReviewers.SequenceEqual(run.ExternalOutstandingReviewerLogins)
            && snapshot.HumanChangesRequestedBy.SequenceEqual(run.ExternalChangesRequestedByLogins)
            && snapshot.HasEverRequestedHumanReviewer == run.ExternalHumanReviewEverRequested
            && snapshot.HumanReviewersAwaitingApproval.SequenceEqual(
                run.ExternalHumanReviewersAwaitingApprovalLogins))
        {
            return;
        }

        session.Events.Append(run.Id, new ExternalReviewObserved(
            run.Id, snapshot.CopilotReviewState, snapshot.CopilotReviewThreadCount,
            snapshot.HasPendingChecks, now, snapshot.ReviewDecision, snapshot.OutstandingReviewers,
            snapshot.OutstandingHumanReviewers, snapshot.HumanChangesRequestedBy,
            snapshot.HasEverRequestedHumanReviewer, snapshot.HumanReviewersAwaitingApproval));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The unresolved review threads a follow-up could actually act on: every one this sweep
    /// observed, less the human-authored ones this exact run already declined or routed.
    /// <para>
    /// A human-authored thread this run already declined or routed stays unresolved by design
    /// (Decisions Log #159: "a human-authored one stays open" — closing it is not the agent's to
    /// do). Left in <c>snapshot.ThreadIds</c> unchanged, it would otherwise read as a fresh
    /// obstruction on every sweep after this one, buying a second follow-up dispatch that can only
    /// repeat "never re-litigate a point a previous run already answered" and push nothing before
    /// the per-obstruction cap parks the run anyway (independent pre-PR review, cycle 1,
    /// adversarial lens: before this exclusion, the identical already-answered thread cost a wasted
    /// extra lap on the way to that same park). This shapes the DISPATCH decision only —
    /// <c>snapshot.UnresolvedReviewThreadCount</c> itself still gates everything downstream,
    /// including auto-merge, exactly as before, so a thread left open on purpose still blocks a
    /// pre-approved merge until the human closes it. Excluding it here does not risk missing real,
    /// later human engagement on it: the per-obstruction cap's own human-engagement bypass
    /// (HasHumanEngagement) already only ever recognizes a thread id newly appearing, never a reply
    /// added to one it already knows — so a reply on this thread was never going to grant a bypass
    /// either way, and this exclusion costs nothing beyond what that gap already did.
    /// </para>
    /// <para>
    /// Scoped to <c>snapshot.HumanThreadIds</c>, not every declined/routed thread: the design this
    /// exclusion implements only ever leaves a HUMAN thread open on purpose (a bot thread gets
    /// resolved by the follow-up itself, per the same #159 asymmetry). Filtering on the outcome's
    /// own <c>kind=</c>/<c>IsHuman</c> self-report instead would trust the agent's own tag for a
    /// decision it was never meant to gate (see <c>ReviewThreadOutcome.IsHuman</c>'s own doc); this
    /// reads the provider's own actor-type classification instead, the same one the sweep already
    /// trusts for <c>UnresolvedHumanThreadCount</c>. A bot thread whose <c>resolveReviewThread</c>
    /// mutation never landed therefore stays outstanding here, so it keeps buying a follow-up (and
    /// eventually the per-obstruction cap's own park) instead of stalling this run forever with no
    /// dispatch, no park, and — since the thread branch returns ahead of TryAutoMergeAsync — no
    /// merge either (independent pre-PR review, cycle 3, adversarial and conformance lenses).
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> OutstandingReviewThreadIds(
        RunDetails run, PullRequestSnapshot snapshot)
    {
        IReadOnlyList<string> alreadyAnsweredThreadIds = [.. run.LastReviewThreadOutcomes
            .Where(outcome => (outcome.Disposition == ReviewThreadDisposition.Decline
                    || outcome.Disposition == ReviewThreadDisposition.Route)
                && snapshot.HumanThreadIds.Contains(outcome.ThreadId))
            .Select(outcome => outcome.ThreadId)];
        return [.. snapshot.ThreadIds.Except(alreadyAnsweredThreadIds)];
    }

    /// <summary>
    /// Why a review follow-up was dispatched, in the words the agent's prompt will carry.
    /// The human count is stated separately rather than folded into the total because it
    /// changes what the follow-up must do: a person is waiting for an answer, and the
    /// prompt's care rules key off exactly that (Decisions Log #62).
    /// </summary>
    private static string DescribeUnresolvedThreads(PullRequestSnapshot snapshot) =>
        snapshot.UnresolvedHumanThreadCount > 0
            ? $"{snapshot.UnresolvedReviewThreadCount} unresolved review thread(s) on the pull request, "
                + $"{snapshot.UnresolvedHumanThreadCount} of them started by a human reviewer."
            : $"{snapshot.UnresolvedReviewThreadCount} unresolved review thread(s) on the pull request.";

    /// <summary>
    /// The countersign (Decisions Log #62): a fix follow-up pushed answers to this pull
    /// request's findings, so the reviewers who raised them are asked to look again and say
    /// whether they were addressed. Opt-in — the project's setting, else the owner's, else
    /// the node default, which is off — because each pass costs review quota and invites the
    /// refinement loop this is bounded against.
    /// <para>
    /// Four guards, and each closes a different door. Only a follow-up run asks, because a
    /// first run's pull request is reviewed on open anyway — any follow-up, whether it was
    /// dispatched for review threads or for failing checks, because either way the diff the
    /// reviewer read has changed underneath them. Each run asks at most once, which
    /// is the natural dedup: a run pushes its fixes once, so "this run has asked" and "these
    /// fixes have been countersigned" are the same fact. Only reviewers whose latest review
    /// predates the head are asked, because a reviewer who has already read these commits —
    /// a recovered Copilot pass, a human who re-approved — has nothing to countersign, and
    /// asking anyway would spend a pass, reset a fresh approval to pending, and invite a
    /// redundant bot pass whose new nits spend the OTHER budget. And the passes are summed
    /// across the task's runs against MaxReviewRerequestsAfterFixes, because the counter has
    /// to outlive the run that spent it — every follow-up is a fresh run, so a per-run cap
    /// would be no cap at all. At the cap the pull request settles on the internal review,
    /// the thread replies, and CI, which is what it would have settled on with the option off.
    /// </para>
    /// <para>
    /// The pass is recorded BEFORE the requests are issued, which is the opposite of the
    /// errored-review path above and deliberate. That path issues one request; this one issues
    /// N, and appending only after all N succeed meant a single rejected reviewer (a
    /// non-collaborator, an account that cannot be requested) threw with earlier POSTs already
    /// landed and no pass recorded — so the next sweep three minutes later did it all again,
    /// forever, with the cap never binding. A spent pass with a partially issued request is an
    /// honest record; an unbounded loop is not. For the same reason a reviewer the provider
    /// refuses is logged and stepped over rather than allowed to abort the pass.
    /// </para>
    /// <para>
    /// Returns whether this call actually issued a countersign this sweep. The pre-approved
    /// auto-merge gate (task: a task can be published pre-approved) reads this to defer a merge
    /// to the next sweep rather than deciding it against the snapshot read before the request —
    /// a request this call just issued would otherwise be evaluated as though it had never been
    /// asked (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    private async Task<bool> RerequestReviewAfterFixesAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        long taskFenceVersion,
        string pullRequestUrl,
        int pullRequestNumber,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!run.IsFollowUp || run.ReviewRerequestsAfterFixes > 0)
        {
            return false;
        }

        IReadOnlyList<PullRequestReviewer> outstanding = ReviewersBehindTheHead(snapshot);
        if (outstanding.Count == 0)
        {
            return false;
        }

        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(project.OwnerId, cancellationToken);
        ReviewRerequestPolicy policy = ReviewRerequestPolicy.Resolve(
            project.ReviewRerequest, owner?.ReviewRerequest, _options.DefaultReviewRerequest);
        if (policy != ReviewRerequestPolicy.Enabled)
        {
            return false;
        }

        // The fence, carried the way the reopen path carries it. Three parts, because the
        // decision was made from documents read before a slow gh call: the task stream is
        // revalidated (an h9k pr resolve landing in that window means a follow-up is already
        // in flight and this pull request is no longer settled), the run is re-read at its
        // current version (a sibling sweep may have spent this run's one pass meanwhile), and
        // the append is versioned on the run stream so two sweeps that both got this far
        // cannot both commit. A lost race defers a sweep, which is always safe.
        StreamState? current = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (current is null || current.Version != taskFenceVersion)
        {
            logger.LogDebug(
                "Task {TaskId} advanced before the countersign for {Url}; deferring to the next sweep",
                run.TaskId, pullRequestUrl);
            return false;
        }

        StreamState? runFence = await session.Events.FetchStreamStateAsync(run.Id, cancellationToken);
        RunDetails? fresh = await session.LoadAsync<RunDetails>(run.Id, cancellationToken);
        if (runFence is null || fresh is null || fresh.ReviewRerequestsAfterFixes > 0)
        {
            return false;
        }

        int passesSpent = await ReviewRerequestPassesAsync(session, run.TaskId, cancellationToken);
        if (passesSpent >= _options.MaxReviewRerequestsAfterFixes)
        {
            logger.LogInformation(
                "Run {RunId}: review re-request cap reached ({Spent}/{Max}) — {Url} settles on the internal "
                + "review, the thread replies, and CI",
                run.Id, passesSpent, _options.MaxReviewRerequestsAfterFixes, pullRequestUrl);
            return false;
        }

        // Recorded before the requests are issued: see the note above — a pass that is only
        // recorded after every reviewer accepted is a pass that a single refusal turns into
        // an unbounded retry. Reviewers here is who the pass was ADDRESSED to; whether each
        // provider accepted is logged below, never assumed.
        session.Events.Append(run.Id, expectedVersion: runFence.Version + 1, new ReviewRerequestedAfterFixes(
            run.Id, [.. outstanding.Select(reviewer => reviewer.Login)], passesSpent + 1, now));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug(
                "Run {RunId} advanced while recording a countersign pass; another sweep got there first", run.Id);
            return false;
        }

        logger.LogInformation(
            "Run {RunId}: fixes pushed to {Url} — re-requesting review from {Reviewers} (pass {Pass}/{Max})",
            run.Id, pullRequestUrl, string.Join(", ", outstanding.Select(reviewer => reviewer.Login)),
            passesSpent + 1, _options.MaxReviewRerequestsAfterFixes);

        foreach (PullRequestReviewer reviewer in outstanding)
        {
            try
            {
                await inspector.RerequestReviewAsync(
                    project.RepositoryPath, pullRequestUrl, pullRequestNumber, reviewer, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One reviewer the provider will not accept (no longer a collaborator, an
                // account that cannot be requested) is that reviewer's answer, not the
                // pass's. The rest of the pass still goes out.
                logger.LogWarning(
                    exception, "Run {RunId}: {Url} refused a review request for {Reviewer}; the rest of the pass stands",
                    run.Id, pullRequestUrl, reviewer.Login);
            }
        }

        return true;
    }

    /// <summary>
    /// The reviewers a countersign has something to ask, which is the ones whose latest review
    /// predates the pull request's head. A reviewer already sitting on the head has read the
    /// fixes: asking again resets their standing verdict to pending and, for a bot, buys
    /// another sample of nits whose follow-up spends the closeout budget (Decisions Log #62).
    /// <para>
    /// Both sides have to be observed for the comparison to mean anything. A head the provider
    /// did not report, or a review reported without a commit, leaves the reviewer in the list:
    /// the honest reading of an unobserved commit is "cannot tell", and asking a reviewer who
    /// may be up to date costs a pass, while skipping one who is not loses the countersign the
    /// option was turned on for.
    /// </para>
    /// </summary>
    private static IReadOnlyList<PullRequestReviewer> ReviewersBehindTheHead(PullRequestSnapshot snapshot) =>
        snapshot.HeadCommit.IsBlank()
            ? snapshot.Reviewers
            : [.. snapshot.Reviewers.Where(reviewer =>
                !string.Equals(reviewer.LastReviewedCommit, snapshot.HeadCommit, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Countersign passes spent on this task, summed over every run that carried its pull
    /// request. Task-scoped rather than run-scoped on purpose: each follow-up is a new run,
    /// so the counter has to live where the pull request does.
    /// </summary>
    private static async Task<int> ReviewRerequestPassesAsync(
        IQuerySession session, Guid taskId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> runs = await session.Query<RunDetails>()
            .Where(candidate => candidate.TaskId == taskId)
            .ToListAsync(cancellationToken);

        return runs.Sum(candidate => candidate.ReviewRerequestsAfterFixes);
    }

    /// <summary>
    /// An errored review (zero threads, no verdict) must not read as review-clean: the
    /// run holds at ReviewPending while the monitor re-requests the review through the
    /// API — never the website, which may be down when this matters (origin incident:
    /// PR #6, 2026-08-17, GitHub partial outage). Each errored review is re-requested
    /// exactly once (the recorded review URL is the dedup key across sweeps), each
    /// re-request draws on the shared automatic budget, and a reviewer that keeps
    /// erroring parks the run with the errored review named for the human. A successful
    /// re-review stops matching as errored and flows through the normal thread path.
    /// </summary>
    private async Task RerequestReviewOrParkAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        string repositoryPath,
        string pullRequestUrl,
        int pullRequestNumber,
        PullRequestSnapshot snapshot,
        ErroredReview erroredReview,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // This errored review was already re-requested; the reviewer just hasn't
        // answered yet. Re-requesting again every sweep would burn the budget on one
        // observation.
        if (erroredReview.Url == run.ErroredReviewUrl)
        {
            return;
        }

        session.Events.Append(run.Id, new ReviewErrored(run.Id, erroredReview.Reviewer, erroredReview.Url, now));

        int automaticActionsSpent = await AutomaticActionsSpentAsync(session, task, cancellationToken);
        if (automaticActionsSpent >= _options.MaxAutomaticCloseoutRuns)
        {
            string parkReason =
                $"Copilot review keeps erroring: {erroredReview.Reviewer}'s latest review ({erroredReview.Url}) " +
                "says it was unable to review the pull request. " +
                $"Automatic closeout budget spent ({automaticActionsSpent}/{_options.MaxAutomaticCloseoutRuns} action(s)) — " +
                $"{DescribeAutomaticLapHistory(task, automaticActionsSpent)}. " +
                "Re-request the review by hand, merge without it, or grant another attempt with h9k pr resolve.";
            session.Events.Append(run.Id, new CloseoutParked(run.Id, parkReason, now));
            await session.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Run {RunId}: closeout parked for the human — {Reason}", run.Id, parkReason);
            return;
        }

        // The API call precedes the append: no ReviewRerequested lands without the
        // request actually made. A failure here rolls the observation back with it and
        // the next sweep retries the whole step.
        //
        // The reviewer is looked up in the snapshot rather than reconstructed, so the
        // [bot]-suffix decision stays the provider's answer. Only an app account can post
        // an error placeholder, so the fallback says bot: the errored review was matched by
        // Copilot's own login in the first place.
        PullRequestReviewer reviewer = snapshot.Reviewers.FirstOrDefault(
                candidate => candidate.Login == erroredReview.Reviewer)
            ?? new PullRequestReviewer(erroredReview.Reviewer, ReviewerKind.Bot);
        await inspector.RerequestReviewAsync(
            repositoryPath, pullRequestUrl, pullRequestNumber, reviewer, cancellationToken);
        session.Events.Append(run.Id, new ReviewRerequested(run.Id, erroredReview.Reviewer, erroredReview.Url, now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: Copilot review errored ({Url}); re-requested review from {Reviewer} (action {Action}/{Max})",
            run.Id, erroredReview.Url, erroredReview.Reviewer,
            automaticActionsSpent + 1, _options.MaxAutomaticCloseoutRuns);
    }

    /// <summary>
    /// One budget for every automatic closeout action: reopen dispatches count on the
    /// task (CloseoutAttempts), review re-requests summed over every run that carried the
    /// task's CURRENT pull request since the last human grant — a follow-up is always a
    /// fresh run with its own <see cref="RunDetails.ReviewRerequestCount"/> starting at
    /// zero, so a per-run read would let each reopen quietly reset the re-request half of
    /// the lifetime ceiling (independent pre-PR review, cycle 3: the same reasoning <see
    /// cref="ReviewRerequestPassesAsync"/> already applies to countersign passes).
    /// h9k pr resolve resets both — the manual reopen zeroes CloseoutAttempts, and
    /// <see cref="ReviewRerequestCountAsync"/> stops counting re-requests dispatched
    /// before the grant it records (independent pre-PR review, cycle 4).
    /// </summary>
    private static async Task<int> AutomaticActionsSpentAsync(
        IQuerySession session, TaskAggregate task, CancellationToken cancellationToken) =>
        task.CloseoutAttempts + await ReviewRerequestCountAsync(session, task, cancellationToken);

    /// <summary>
    /// Errored-review re-requests spent since the last human grant, summed over every run
    /// that carried the task's current pull request. Scoped by pull request rather than by
    /// task (independent pre-PR review, cycle 4) — a <c>h9k task retry</c> onto a second
    /// pull request must not start that PR's closeout already debited by the first one's
    /// spend. Scoped by grant time rather than read as a raw sum (same review, same cycle)
    /// — an ungated lifetime sum never shrinks, so a <c>h9k pr resolve</c> late in a busy
    /// PR's life would restore less budget than the one before it, down to none at all. A
    /// run's own re-request count freezes the moment it is superseded or granted (the next
    /// automatic decision watches a fresh run), so filtering by <see
    /// cref="RunDetails.DispatchedAt"/> against the latest <see
    /// cref="RunDetails.HumanGrantedAt"/> this task's runs carry is exactly "since the
    /// grant", with no separate cursor to keep in sync.
    /// </summary>
    private static async Task<int> ReviewRerequestCountAsync(
        IQuerySession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        // Runs on every closeout decision, so this projects to only the four scalar fields
        // read below instead of materializing full RunDetails documents (PR #37 review).
        IReadOnlyList<ReviewRerequestScalars> runs = await session.Query<RunDetails>()
            .Where(candidate => candidate.TaskId == task.Id)
            .Select(candidate => new ReviewRerequestScalars(
                candidate.PullRequestUrl, candidate.DispatchedAt, candidate.HumanGrantedAt,
                candidate.ReviewRerequestCount))
            .ToListAsync(cancellationToken);

        DateTimeOffset? lastGrantedAt = runs
            .Select(candidate => candidate.HumanGrantedAt)
            .Where(grantedAt => grantedAt is not null)
            .Max();

        return runs
            .Where(candidate => candidate.PullRequestUrl == task.PullRequestUrl
                && (lastGrantedAt is null || candidate.DispatchedAt > lastGrantedAt))
            .Sum(candidate => candidate.ReviewRerequestCount);
    }

    private sealed record ReviewRerequestScalars(
        string? PullRequestUrl,
        DateTimeOffset DispatchedAt,
        DateTimeOffset? HumanGrantedAt,
        int ReviewRerequestCount);

    /// <summary>
    /// The lap history a park message reads back, honest about the gap between it and the
    /// lifetime spend <paramref name="automaticActionsSpent"/> counts: <see
    /// cref="TaskAggregate.AutomaticLapHistory"/> only ever grows from an automatic
    /// <c>TaskReopened</c> that carried an <c>ObstructionSummary</c>, so the gap can hold budget
    /// spent re-requesting a review after it errored (<see cref="RunDetails.ReviewRerequestCount"/>,
    /// summed across every run) as well as an older-shape automatic reopen recorded before this
    /// obstruction vocabulary existed. Rather than asserting which of those the gap it has not
    /// observed is (the never-guess rule, AGENTS.md), this states the gap as a bare number.
    /// </summary>
    private static string DescribeAutomaticLapHistory(TaskAggregate task, int automaticActionsSpent)
    {
        int unitemized = automaticActionsSpent - task.AutomaticLapHistory.Count;
        string history = task.AutomaticLapHistory.Count > 0
            ? string.Join("; ", task.AutomaticLapHistory.Select((lap, index) => $"lap {index + 1}: {lap}"))
            : "no automatic lap recorded an obstruction";

        return unitemized > 0
            ? $"{history} ({unitemized} further automatic action(s) not itemized above)"
            : history;
    }

    /// <summary>
    /// The merge is the end of the story: RunCompleted finally lands (the event
    /// TASK-MODEL.md reserved for exactly this), then the workspace is cleaned up — the
    /// worktree retained through closeout (log #21) and the task branch everywhere it
    /// lingers (origin incident: five merged task branches accumulated locally because
    /// nothing owned this step).
    /// <para>
    /// RunCompleted is dated <paramref name="now"/> — when this sweep observed the merge —
    /// never <paramref name="mergedAt"/>, GitHub's own merge timestamp. The two read minutes
    /// apart on a normally-watched run, but the orphan sweep (Decisions Log #72) can observe
    /// a merge that happened days ago, and dating the platform's own completion record to a
    /// fact it did not just witness is exactly the guess the never-guess rule forbids
    /// (AGENTS.md). PullRequestMerged keeps <paramref name="mergedAt"/> honestly — that value
    /// names what GitHub reported, not when this node noticed.
    /// </para>
    /// <para>
    /// This is also the landing half of the handoff's capture-then-land split (Decisions Log
    /// #36). The text was captured from the agents' own session ends long before now, but the
    /// event carrying it is appended here, in the same transaction as PullRequestMerged and
    /// RunCompleted and immediately before the dependents are unblocked. That ordering IS the
    /// guarantee: an unmerged run has no RunHandoffRecorded, so its summary can never travel
    /// to work that builds on code which never landed.
    /// </para>
    /// <para>
    /// <paramref name="expectedVersion"/> is the run stream's own fenced version, versioning
    /// every append below when a caller has one to give — <see cref="ReconstructAndCompleteAsync"/>'s
    /// own intact-run branch, the only caller whose candidate set is not node-scoped and can
    /// therefore race a sibling sweep to this same run. <see cref="InspectAndActAsync"/> and
    /// <see cref="InspectOrphanAsync"/> pass <c>null</c>: each already watches only runs this
    /// node itself dispatched, so no other sweep on this node's own watch set can reach the same
    /// run concurrently.
    /// </para>
    /// </summary>
    private async Task CompleteCloseoutAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        TaskAggregate task,
        DateTimeOffset? mergedAt,
        DateTimeOffset now,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        RunHandoffRecorded handoff = await ComposeHandoffAsync(session, run, now, cancellationToken);
        PullRequestMerged merged = new(run.Id, mergedAt, now);
        RunCompleted completed = new(run.Id, now);
        if (expectedVersion is { } version)
        {
            session.Events.Append(run.Id, expectedVersion: version + 3, merged, handoff, completed);
        }
        else
        {
            session.Events.Append(run.Id, merged, handoff, completed);
        }

        // A Blocked task reaches here only through the one case InspectAndActAsync and
        // InspectOrphanAsync both admit it for: Apply(TaskReopened) kept CurrentRunId pointing at
        // this run behind a still-open dependency (h9k pr resolve landing Blocked, Decisions Log
        // #125's own reopen path), so this merge/close watch kept running with no follow-up ever
        // dispatched. Leaving the task Blocked here would let a later TaskDependencyCompleted flip
        // it straight to Queued once that dependency finally clears (Apply(TaskDependencyCompleted)
        // does that unconditionally); DispatchEngine would then claim it and RunLauncher's own
        // already-merged guard would decline the dispatch but call this same method a second time
        // for a brand-new run id, doubling the merge comment, the handoff, and the dependents-unblock
        // (independent pre-PR review, cycle 3, adversarial lens). Finalizing the task to Done here,
        // in the same transaction as the run's own completion, closes that door — Apply
        // (TaskDependencyCompleted) only ever acts on a task still reading Blocked, so a task
        // already Done from this point on can never be re-queued by a dependency clearing late.
        if (task.State == TaskState.Blocked)
        {
            StreamState? taskFence = await session.Events.FetchStreamStateAsync(task.Id, cancellationToken);
            if (taskFence is not null)
            {
                session.Events.Append(
                    task.Id, expectedVersion: taskFence.Version + 1,
                    TaskDecider.Complete(task, run.Id, task.PullRequestUrl, now));
            }
        }

        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: pull request {Url} merged — closeout complete", run.Id, run.PullRequestUrl);

        await UnblockDependentsAsync(run.TaskId, now, cancellationToken);
        await TellTheCardAsync(run.TaskId, project, task, cancellationToken);
        await RemoveWorktreeBestEffortAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);

        // Blank on a reconstructed run (ReconstructAndCompleteAsync): it never actually
        // dispatched, so there is no branch this run itself ever checked out to delete.
        if (run.Branch.IsBlank())
        {
            return;
        }

        try
        {
            await worktrees.DeleteBranchEverywhereAsync(project.RepositoryPath, run.Branch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Branch cleanup failed for {Branch} (safe to delete by hand)", run.Branch);
        }
    }

    /// <summary>
    /// Tell the external work item that the work landed (backlog 18; and, for GitHub, the
    /// backlog-tracking feature that gave every provider this same closeout comment): one
    /// comment on the card or issue carrying the pull request, at the moment the merge is
    /// observed.
    /// <para>
    /// The comment is unconditional and the same fixed template (<see cref="MergeComment"/>) for
    /// both providers, naming only the pull request's URL, the task id, and the project name —
    /// none of them free text a closing keyword could hide in — so it mentions the pull request
    /// without ever asking GitHub to act on it via its own closing keywords (<c>Fixes #42</c>).
    /// Whether the linked item is ALSO closed is a separate, opt-in decision from there: Jira's
    /// workflow transition is still a guess this repo refuses to make, so a Jira card is only ever
    /// commented; a GitHub issue can additionally be closed, under the configurable rule
    /// <see cref="ShouldCloseGitHubIssueAsync"/> decides (task: a task's linked GitHub issue is
    /// closed at true closeout under a configurable rule, Decisions Log #154).
    /// </para>
    /// <para>
    /// Best-effort, and loudly so. The merge is already recorded and the dependents are already
    /// unblocked; an outage on either side must not undo any of that, and it must not be retried
    /// blindly either — a retry loop around an unwatched write is how one item ends up with four
    /// identical comments. So a failure is logged with everything needed to do it by hand and
    /// the closeout carries on.
    /// </para>
    /// </summary>
    private async Task TellTheCardAsync(
        Guid taskId, ProjectDetails project, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.ExternalReference is not { } reference || task.PullRequestUrl.IsBlank())
        {
            return;
        }

        if (reference.Provider == WorkItemProvider.Jira)
        {
            await TellJiraAsync(taskId, project, task, reference, cancellationToken);
        }
        else if (reference.Provider == WorkItemProvider.GitHub)
        {
            await TellGitHubAsync(taskId, project, task, reference, cancellationToken);
        }
    }

    /// <summary>
    /// Comment the merge onto the card through the same write surface an operator or an agent
    /// uses (Brian's design, 2026-08-28): hall9k is the sole executor of every Jira write, closeout
    /// included, so a merge comment is recorded, executed against the Jira Cloud REST API
    /// (Decisions Log #114), and verified by read-back exactly like any other write — and a
    /// rejected credential is handled the same way too, leaving the comment pending for the
    /// daemon's own retry sweep rather than lost.
    /// <para>
    /// A write already outstanding on the task (an operator's own <c>write-jira</c>, or a create
    /// still resolving) is the one case worth telling apart from an ordinary failure: two writes
    /// in flight could race against each other, so <see cref="JiraWriteCoordinator.SubmitAsync"/>
    /// refuses it with <see cref="DomainConflictException"/> rather than attempting it. That is not
    /// a reason to give up on the merge comment — it is queued instead (<see
    /// cref="TaskDecider.QueueJiraMergeNotice"/>), and <see cref="JiraWrites.JiraWriteRetryEngine"/>
    /// drains the queue once the blocking write clears.
    /// </para>
    /// <para>
    /// <see cref="jiraRequester"/> is injected the same way <see cref="processRunner"/> is for
    /// GitHub's own <c>gh</c> writes just below — registered once, generically, precisely so a
    /// write like this one is testable against a fake HTTP response instead of the real,
    /// machine-authenticated tenant.
    /// </para>
    /// <para>
    /// The site is resolved with the strict <see cref="WorkItemConnections.FindJiraConnectionAsync"/>.
    /// No connection, or one recorded before the site field existed, is skipped with a logged
    /// reason, since there is nothing here to keep retrying. Two connections registered at once
    /// throws <see cref="DomainConflictException"/> from
    /// <see cref="WorkItemConnections.FindJiraConnectionAsync"/> itself, caught by this method's own
    /// site-resolution catch just below (not the generic catch further down, which only ever sees a
    /// failure from the write attempt itself) and skipped the same way.
    /// </para>
    /// </summary>
    private async Task TellJiraAsync(
        Guid taskId, ProjectDetails project, TaskAggregate task, ExternalReference reference, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        JiraWriteExecutor executor;
        try
        {
            ConnectionDetails? connectionDetails = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken);
            if (connectionDetails?.SiteUrl is null)
            {
                logger.LogWarning(
                    "Task {TaskId} is linked to {Reference} but this node has no usable Jira connection, "
                    + "so the merge was not commented on the card", taskId, reference);
                return;
            }

            executor = new JiraWriteExecutor(WorkItemConnections.Account(connectionDetails), jiraRequester);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not resolve this node's Jira connection for {Reference}. Nothing is retried "
                + "automatically; add the note by hand if it matters", reference);
            return;
        }

        try
        {
            JiraWriteAttemptResult result = await JiraWriteCoordinator.SubmitAsync(
                session,
                taskId,
                JiraWriteOperation.Comment,
                reference.Reference,
                new JiraWritePayload(WorkItemType: null, Fields: null, Comment: MergeComment(project, task), Format: "plain"),
                project.JiraProjectKey,
                node.OwnerId,
                executor,
                cancellationToken);

            switch (result.Outcome)
            {
                case JiraWriteOutcome.Succeeded:
                    logger.LogInformation("Task {TaskId}: told {Reference} that {Url} merged", taskId, reference, task.PullRequestUrl);
                    break;
                case JiraWriteOutcome.PendingAuthentication:
                    // result.Message is the recorded reason, not a fixed "Jira rejected the
                    // credential" claim: it may equally be a credential the vault could not even
                    // resolve, which Jira was never asked about (independent pre-PR review,
                    // adversarial lens, cycle 1) — and it already says whether and how this retries
                    // (JiraWriteExecutor.AuthorizeAsync carries the same retry reassurance Explain's
                    // own 401 message does, independent pre-PR review, adversarial lens, cycle 2, so
                    // this holds for a vault-resolution failure too, not only a real 401), so nothing
                    // generic is appended after it.
                    logger.LogWarning(
                        "Task {TaskId}: the merge comment for {Reference} is pending — {Reason}",
                        taskId, reference, result.Message);
                    break;
                default:
                    logger.LogWarning(
                        "Could not comment the merge of {Url} on {Reference}: {Reason}",
                        task.PullRequestUrl, reference, result.Message);
                    break;
            }
        }
        catch (DomainConflictException) when (!cancellationToken.IsCancellationRequested)
        {
            await QueueJiraMergeNoticeAsync(taskId, reference, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not comment the merge of {Url} on {Reference}. Nothing is retried automatically; "
                + "add the note by hand if it matters",
                task.PullRequestUrl, reference);
        }
    }

    /// <summary>
    /// Records the merge notice as queued rather than let a write already in flight on this task
    /// (<see cref="DomainConflictException"/> from <see cref="TellJiraAsync"/>'s own attempt) drop
    /// it silently. Best-effort like the write attempt itself: if even the queue append fails —
    /// the task changed concurrently, say — the same "log it and move on" fallback applies, since
    /// the merge is already recorded and dependents already unblocked regardless.
    /// </summary>
    private async Task QueueJiraMergeNoticeAsync(Guid taskId, ExternalReference reference, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                    taskId, version: fence.Version, token: cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");

            JiraMergeNoticeQueued queued = TaskDecider.QueueJiraMergeNotice(task, DateTimeOffset.UtcNow);
            session.Events.Append(taskId, expectedVersion: fence.Version + 1, queued);
            await session.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Task {TaskId}: the merge comment for {Reference} is queued behind another outstanding "
                + "Jira write; the retry sweep will tell it once that clears", taskId, reference);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not queue the merge comment for {Reference}. Nothing is retried automatically; "
                + "add the note by hand if it matters", reference);
        }
    }

    /// <summary>
    /// GitHub already cross-references the merge on the issue's own timeline the moment the pull
    /// request body mentions it (PLAN.md #60) — no platform write required. This comment exists
    /// anyway, for parity with Jira: a timeline cross-reference is not the same as a completion
    /// notice a human reads, and a project tracking its backlog in GitHub deserves the same
    /// explicit word Jira gets rather than a quieter closeout because the mention happened to be
    /// free.
    /// <para>
    /// Built on <see cref="processRunner"/> rather than a bare <c>new GitHubWorkItemProvider()</c>,
    /// the same reason <see cref="TellJiraAsync"/> builds its <see cref="JiraWriteExecutor"/> on the
    /// injected <see cref="jiraRequester"/> instead of reaching Jira statically: it is what lets
    /// this write be exercised in the test suite against a recorded process instead of a live,
    /// machine-authenticated one (independent pre-PR review, cycle 4).
    /// </para>
    /// </summary>
    private async Task TellGitHubAsync(
        Guid taskId, ProjectDetails project, TaskAggregate task, ExternalReference reference, CancellationToken cancellationToken)
    {
        GitHubWorkItemProvider provider = new(processRunner);

        // Decided before anything is written, not after: the note's own wording says whether the
        // issue was actually closed alongside it (MergeComment), and a decision that fails must
        // never abort the comment — the merge is already recorded and dependents already
        // unblocked, so the note is the one thing this method must still say regardless.
        bool shouldClose;
        try
        {
            shouldClose = await ShouldCloseGitHubIssueAsync(provider, taskId, project, task, reference, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Task {TaskId}: could not decide whether to close {Reference}; leaving it open. "
                + "Close it by hand if it should close", taskId, reference);
            shouldClose = false;
        }

        // The close is attempted before the comment is posted, not after, so the comment's own
        // wording can say what actually happened rather than what was merely intended — a close
        // that later fails must not leave a permanent note claiming it succeeded.
        bool closed = false;
        if (shouldClose)
        {
            try
            {
                await provider.CloseAsync(reference, project.RepositoryPath, cancellationToken);
                closed = true;
                logger.LogInformation("Task {TaskId}: closed {Reference} per the close-linked-issue rule", taskId, reference);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Could not close {Reference} after {Url} merged. Nothing is retried automatically; "
                    + "close it by hand if it matters",
                    reference, task.PullRequestUrl);
            }
        }

        try
        {
            await provider.CommentAsync(
                reference, MergeComment(project, task, closed), project.RepositoryPath, cancellationToken);
            logger.LogInformation("Task {TaskId}: told {Reference} that {Url} merged", taskId, reference, task.PullRequestUrl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not comment the merge of {Url} on {Reference}. Nothing is retried automatically; "
                + "add the note by hand if it matters",
                task.PullRequestUrl, reference);
        }
    }

    /// <summary>
    /// Whether closeout should close this task's linked GitHub issue right now (task: a task's
    /// linked GitHub issue is closed at true closeout under a configurable rule). Reads the
    /// issue's own live state once — its open/closed status and its current labels — because a
    /// task's explicit override wins over a never-close label, and a never-close label wins over
    /// the project's own default, and both the label check and the final "is it still open" gate
    /// need the same fresh read. An issue GitHub reports missing, unreadable, or already closed is
    /// left alone: this returns false rather than let a later close attempt fail or retry on
    /// account of it.
    /// </summary>
    private async Task<bool> ShouldCloseGitHubIssueAsync(
        GitHubWorkItemProvider provider,
        Guid taskId,
        ProjectDetails project,
        TaskAggregate task,
        ExternalReference reference,
        CancellationToken cancellationToken)
    {
        CloseLinkedIssueRule? taskOverride = task.CloseLinkedIssue;
        if (taskOverride == CloseLinkedIssueRule.Never)
        {
            return false;
        }

        if (taskOverride is null && project.NeverCloseLabels.Count == 0 && project.CloseLinkedIssue == CloseLinkedIssueRule.Never)
        {
            // An inherited never can still be beaten by a sibling's own explicit override once
            // every linked task has closed (Decisions Log #154), so this fast path is safe only
            // once it is confirmed no sibling exists to ever supply one — not merely because this
            // task's own rule already resolves to never. A sibling that exists but has not closed
            // yet still lets this return false correctly further down, once the read below feeds
            // the ordinary cross-task gate; this check exists only to skip the read when there is
            // truly nothing else linked that could ever change the answer.
            await using IQuerySession noSiblingSession = store.QuerySession();
            if (!await AnyOtherLinkedTaskAsync(noSiblingSession, taskId, reference, cancellationToken))
            {
                return false;
            }
        }

        GitHubIssueCloseoutRead read = await provider.ReadCloseoutStateAsync(reference, project.RepositoryPath, cancellationToken);
        if (read.Failed)
        {
            logger.LogWarning(
                "Task {TaskId}: could not read {Reference} to decide whether to close it — {Error}. "
                + "Left alone; the merge note is still posted", taskId, reference, read.Error);
            return false;
        }

        if (!read.IsOpen)
        {
            logger.LogWarning(
                "Task {TaskId}: {Reference} is already closed; left alone rather than re-closed or retried",
                taskId, reference);
            return false;
        }

        CloseLinkedIssueRule rule = taskOverride
            ?? (project.NeverCloseLabels.Count > 0
                && read.Labels.Any(label => project.NeverCloseLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                ? CloseLinkedIssueRule.Never
                : project.CloseLinkedIssue);

        // Every outcome defers to the same cross-task resolution, because a sibling's explicit
        // override — most of all an explicit never — can still beat this task's own resolved rule
        // regardless of which task happens to close last (Decisions Log #154). on-closeout is the
        // one case that does not WAIT for that resolution: it has no sibling to wait for, so it
        // skips the "has everyone else closed" gate below and goes straight to the scan, which
        // still lets an explicit never recorded on any linked task keep the issue open.
        await using IQuerySession session = store.QuerySession();
        if (rule != CloseLinkedIssueRule.OnCloseout
            && !await AllOtherLinkedTasksClosedOutAsync(session, taskId, reference, cancellationToken))
        {
            return false;
        }

        return await ResolveFinalClosureAcrossLinkedTasksAsync(session, project, reference, read.Labels, cancellationToken);
    }

    /// <summary>
    /// Whether every OTHER task linked to this same external reference has itself reached true
    /// closeout or been abandoned — the gate <see cref="CloseLinkedIssueRule.WhenAllTasksClose"/>
    /// waits behind. This task's own Done/RunCompleted state is already committed by the time this
    /// runs (<see cref="CompleteCloseoutAsync"/> saves before calling <see cref="TellTheCardAsync"/>),
    /// so a fresh read here sees this task correctly without needing to special-case it — it is
    /// simply excluded, since the question is about every task besides it.
    /// </summary>
    private static async Task<bool> AllOtherLinkedTasksClosedOutAsync(
        IQuerySession session, Guid currentTaskId, ExternalReference reference, CancellationToken cancellationToken)
    {
        string canonical = reference.ToString();
        IReadOnlyList<TaskListItem> linked = await session.Query<TaskListItem>()
            .Where(candidate => candidate.ExternalReference == canonical)
            .ToListAsync(cancellationToken);

        List<TaskListItem> others = [.. linked.Where(candidate => candidate.Id != currentTaskId)];
        if (others.Count == 0)
        {
            return true;
        }

        Guid[] runIds = [.. others.Select(candidate => candidate.CurrentRunId).OfType<Guid>()];
        Dictionary<Guid, RunState> runStates = [];
        if (runIds.Length > 0)
        {
            foreach (RunDetails run in await session.Query<RunDetails>()
                .Where(run => run.Id.IsOneOf(runIds))
                .ToListAsync(cancellationToken))
            {
                runStates[run.Id] = run.State;
            }
        }

        return others.All(candidate =>
            candidate.State == TaskState.Abandoned
            || (candidate.State == TaskState.Done
                && candidate.CurrentRunId is { } runId
                && runStates.TryGetValue(runId, out RunState? runState)
                && runState == RunState.Completed));
    }

    /// <summary>
    /// Whether any task besides this one is linked to the same external reference at all,
    /// regardless of that task's own state — the narrow question <see cref="ShouldCloseGitHubIssueAsync"/>
    /// needs answered before it can safely skip its own GitHub read for an inherited never: that
    /// skip is only safe when no sibling could ever supply an explicit override for the cross-task
    /// resolution to find later (Decisions Log #154). Unlike <see cref="AllOtherLinkedTasksClosedOutAsync"/>,
    /// this says nothing about closure — a sibling that exists but has not closed yet still counts.
    /// </summary>
    private static Task<bool> AnyOtherLinkedTaskAsync(
        IQuerySession session, Guid currentTaskId, ExternalReference reference, CancellationToken cancellationToken)
    {
        string canonical = reference.ToString();
        return session.Query<TaskListItem>()
            .Where(candidate => candidate.ExternalReference == canonical && candidate.Id != currentTaskId)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// The rule decided across every task linked to this issue, at the moment the last one of
    /// them reaches true closeout or is abandoned: an explicit override recorded on ANY linked
    /// task settles it — <see cref="CloseLinkedIssueRule.Never"/> anywhere keeps the issue open,
    /// and so does anything else this build cannot place as a close-flavoured rule (not just the
    /// canonical <see cref="CloseLinkedIssueRule.Unknown"/> sentinel: a stream can carry literally
    /// any string, and a rule a future build invented reads exactly the same as a hand-edited one
    /// once it lands here), since an unreadable rule is read the same as an explicit never rather
    /// than let it fall through to the project default — otherwise <see cref="CloseLinkedIssueRule.OnCloseout"/>
    /// or <see cref="CloseLinkedIssueRule.WhenAllTasksClose"/> anywhere closes it — and only when
    /// NO linked task carries an explicit override at all does the never-close label list and then
    /// the project's own default apply. Recency plays no part: every linked task's own recorded
    /// override is read fresh, not whichever was set most recently.
    /// </summary>
    private static async Task<bool> ResolveFinalClosureAcrossLinkedTasksAsync(
        IQuerySession session,
        ProjectDetails project,
        ExternalReference reference,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        string canonical = reference.ToString();
        List<CloseLinkedIssueRule> explicitOverrides = [
            .. (await session.Query<TaskListItem>()
                    .Where(candidate => candidate.ExternalReference == canonical)
                    .ToListAsync(cancellationToken))
                .Select(candidate => candidate.CloseLinkedIssue)
                .OfType<CloseLinkedIssueRule>(),
        ];

        if (explicitOverrides.Count > 0)
        {
            // Membership in the "closes" set, not the "opens" set: anything recorded that is not
            // itself close-flavoured — an explicit never, or a rule this build cannot place at
            // all — keeps the issue open, the same fail-toward-open direction the project-level
            // default check just below already gives an unrecognized project rule.
            return explicitOverrides.All(overrideRule =>
                overrideRule == CloseLinkedIssueRule.OnCloseout || overrideRule == CloseLinkedIssueRule.WhenAllTasksClose);
        }

        bool labelForcesNever = project.NeverCloseLabels.Count > 0
            && labels.Any(label => project.NeverCloseLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
        if (labelForcesNever)
        {
            return false;
        }

        return project.CloseLinkedIssue == CloseLinkedIssueRule.OnCloseout
            || project.CloseLinkedIssue == CloseLinkedIssueRule.WhenAllTasksClose;
    }

    /// <summary>
    /// What the card is told. Short, factual, and explicit about whether anything else has
    /// happened to it — a card that silently gains a comment and never moves reads like an
    /// integration that half worked, and saying so costs one sentence; a card that was actually
    /// closed alongside this note deserves the same explicitness rather than the platform's older,
    /// now only sometimes true, promise never to touch its status at all. <paramref name="closed"/>
    /// reports what actually happened (the caller only passes true once <c>CloseAsync</c> has
    /// already succeeded), not merely what the close-linked-issue rule decided, since the two can
    /// differ when the write itself fails — and the wording names no source (project setting vs.
    /// task override), since either can be the one that decided it.
    /// </summary>
    internal static string MergeComment(ProjectDetails project, TaskAggregate task, bool closed = false) =>
        $"""
         The pull request for this work has merged: {task.PullRequestUrl}

         Recorded by Hall9k as task {task.Id} in project {project.Name}. This is a one-off note at
         merge{(closed
             ? " — the issue was closed alongside it, per the close-linked-issue rule."
             : ". Hall9k does not change this item's status or close it here, because which status a "
               + "merge means is this project's workflow to decide.")}
         """;

    /// <summary>
    /// The handoff the task hands down, composed from every run that carried this pull
    /// request rather than from the run that happened to observe the merge (Decisions Log
    /// #36).
    /// <para>
    /// The completing run is almost never the run that did the work. Decision #22 makes
    /// review follow-ups automatic, so a merged pull request is normally an original run
    /// retired with RunSuperseded plus a follow-up that resolved the review threads and
    /// reached Completed. Reading only the completing run would hand a dependent the thread
    /// resolution and leave the description of the feature itself unread in a superseded
    /// run's directory. Origin incident: the first cut of this method did exactly that, and
    /// every task on main that had reached true closeout showed the shape (two runs, one
    /// superseded).
    /// </para>
    /// <para>
    /// Failed and killed runs are excluded, and that exclusion is the retry case: a run that
    /// died left work which never merged, so its summary must not travel. A superseded run is
    /// the opposite situation — it is the run whose work is in this merge.
    /// </para>
    /// </summary>
    private async Task<RunHandoffRecorded> ComposeHandoffAsync(
        IDocumentSession session, RunDetails completing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<HandoffParser.RunHandoff> authored = [];
        HandoffOutcome absence = HandoffOutcome.NotCaptured;
        foreach (RunDetails run in await MergedRunsAsync(session, completing, cancellationToken))
        {
            (HandoffOutcome outcome, string? text, string runDirectory) = await ReadHandoffAsync(run, cancellationToken);
            if (text.IsNotBlank())
            {
                // The RESOLVED directory travels into the record, not run.RunDirectory as
                // recorded at dispatch: BoundForEvent below can name this path in a truncation
                // note a human reads, and a stale path there would send them somewhere the
                // render sweep already moved the files away from (backlog 51 cycle 6). But the
                // directory ReadHandoffAsync resolved is where the files sit RIGHT NOW, before
                // RunCompleted below has even committed — this method is the closeout that makes
                // this task archived, so the render sweep moves this exact directory into
                // tasks/_archive/ within one sweep of this transaction landing (adversarial
                // review, backlog 51 cycle 10). The note has to name where the sweep is about to
                // put it, not where it happened to be a moment before that was true.
                authored.Add(new HandoffParser.RunHandoff(
                    run.Id, RunPaths.AnticipateDirectoryAfterSweep(runDirectory, willArchive: true), text));
                continue;
            }

            absence = LessCertainOf(absence, outcome);
        }

        return authored.Count == 0
            ? new RunHandoffRecorded(completing.Id, absence, null, now)
            : new RunHandoffRecorded(
                completing.Id,
                HandoffOutcome.Captured,
                HandoffParser.BoundForEvent(
                    HandoffParser.Compose(authored), [.. authored.Select(handoff => handoff.RunDirectory)]),
                now);
    }

    /// <summary>
    /// The runs whose work is in this merge, oldest dispatch first, so the run that opened the
    /// work leads the composed handoff. The completing run is appended if the projection did
    /// not return it, because the run being closed out is a fact this method already holds.
    /// </summary>
    private static async Task<IReadOnlyList<RunDetails>> MergedRunsAsync(
        IQuerySession session, RunDetails completing, CancellationToken cancellationToken)
    {
        Guid taskId = completing.TaskId;
        IReadOnlyList<RunDetails> runs = await session.Query<RunDetails>()
            .Where(run => run.TaskId == taskId)
            .ToListAsync(cancellationToken);

        List<RunDetails> merged =
        [
            .. runs
                .Where(run => run.State != RunState.Failed && run.State != RunState.Killed)
                .OrderBy(run => run.DispatchedAt)
                .ThenBy(run => run.Id),
        ];

        return merged.Any(run => run.Id == completing.Id) ? merged : [.. merged, completing];
    }

    /// <summary>
    /// The absence the composed handoff reports when no run authored one. Certainty only ever
    /// decreases: a file that could not be read (<see cref="HandoffOutcome.Unknown"/>) outranks
    /// an empty one, because it might have held the very text the dependent wanted, and an
    /// empty one outranks a missing one, because at least one session's result was read and
    /// observed to carry nothing. Guessing a stronger absence than the reads support is exactly
    /// what the never-guess rule forbids.
    /// </summary>
    private static HandoffOutcome LessCertainOf(HandoffOutcome absence, HandoffOutcome observed) =>
        absence == HandoffOutcome.Unknown || observed == HandoffOutcome.Unknown
            ? HandoffOutcome.Unknown
            : absence == HandoffOutcome.NotAuthored || observed == HandoffOutcome.NotAuthored
                ? HandoffOutcome.NotAuthored
                : HandoffOutcome.NotCaptured;

    /// <summary>
    /// One run's handoff, read from the artifact its own session end wrote (Decisions Log
    /// #36). The file's three states are three observations and each maps to its own outcome,
    /// so the absence of a handoff is always a recorded answer rather than an empty string
    /// nobody can interpret: non-blank means the agent authored one, empty means its result
    /// was read and carried none, and absent means there was no session-end capture at all —
    /// a run parked and resolved by hand, or a stream from before handoffs existed. A run
    /// closing out without a usable handoff is perfectly valid; what is not valid is
    /// pretending to know why, which is why a file that exists but cannot be read records
    /// <see cref="HandoffOutcome.Unknown"/> rather than any of the three.
    /// </summary>
    private async Task<(HandoffOutcome Outcome, string? Handoff, string RunDirectory)> ReadHandoffAsync(
        RunDetails run, CancellationToken cancellationToken)
    {
        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        string path = RunPaths.HandoffFile(runDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return (HandoffOutcome.NotCaptured, null, runDirectory);
            }

            string handoff = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            return handoff.IsBlank()
                ? (HandoffOutcome.NotAuthored, null, runDirectory)
                : (HandoffOutcome.Captured, handoff, runDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The artifact exists but could not be read, which says nothing about whether a
            // handoff was authored — so the ledger says it does not know, rather than
            // asserting the absence NotCaptured would claim. An unread file is not an
            // observed one (the never-guess rule).
            logger.LogWarning(exception, "Could not read the handoff artifact for run {RunId} at {Path}", run.Id, path);
            return (HandoffOutcome.Unknown, null, runDirectory);
        }
    }

    /// <summary>
    /// True closeout is the only completion signal a dependency chain accepts (Decisions Log
    /// #34), so this is where dependents re-evaluate: whichever node observed the merge is the
    /// node that unblocks them, and the doorbell tells every other node's dispatch loop to
    /// look. A failure here is logged rather than propagated — the merge is recorded either
    /// way, and the dispatch loop's own sweep re-evaluates blocked tasks each cycle.
    /// </summary>
    private async Task UnblockDependentsAsync(Guid taskId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            DependencyReevaluation reevaluation = await TaskDependencyResolver.ForDependencyAsync(
                session, taskId, now, cancellationToken);
            if (reevaluation.Unblocked.Count == 0)
            {
                // The pass may still have parked or recovered a dependent on one of its other
                // blockers; nothing there is claimable, so there is no doorbell to ring and no
                // count worth reporting as an unblocking.
                return;
            }

            logger.LogInformation(
                "Task {TaskId} closed out — {Unblocked} dependent(s) moved Blocked → Queued",
                taskId, reevaluation.Unblocked.Count);
            await Doorbell.RingAsync(connection.ConnectionString, $"dependencies-met:{taskId}", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Re-evaluating the dependents of task {TaskId} failed; the dispatch loop's sweep retries it", taskId);
        }
    }

    private async Task RecordClosedAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        DateTimeOffset? closedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        session.Events.Append(run.Id, new PullRequestClosed(run.Id, closedAt, now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Run {RunId}: pull request {Url} was closed without merge — worktree removed, branch kept (it holds unmerged work)",
            run.Id, run.PullRequestUrl);

        await RemoveWorktreeBestEffortAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
    }

    /// <summary>
    /// An obstruction's mechanical identity (Decisions Log #80, backlog 45): what the next
    /// automatic decision compares against to tell "still stuck on the same thing" from
    /// "something changed", by identity alone — never by judging severity or content. Checks
    /// key on the failing check's own name(s); review feedback keys on the exact set of
    /// unresolved thread ids present at dispatch, so a thread resolved or a new one opened is,
    /// mechanically, a different obstruction (the backlog card's own example: two CI failures
    /// of different checks are different obstructions, and the same rule applies to threads).
    /// A conflict keys on the branch's head commit at the moment it was observed conflicting:
    /// every rebase attempt pushes a new head, so a conflict against the commit a prior lap
    /// already failed to clear is the same obstruction, and a conflict discovered again after
    /// that push landed (main having moved again in the meantime, backlog 44's own scenario)
    /// is mechanically a new one, exactly as a resolved-then-reopened thread is. This is the
    /// honest application of #80's progress-based counting, not a special case for conflicts: a
    /// rebase lap that moves the head made progress, so a re-conflict on that new head is main
    /// moving again — a fresh obstruction with its own lap count — while the lifetime ceiling
    /// (never the progress cap) is what backstops a branch that keeps re-conflicting lap after
    /// lap. A fixed, binary identity (e.g. a literal "conflict") would instead park a busy
    /// repository for the crime of staying alive, exactly what #80's two-counter split exists to
    /// prevent.
    /// <para>
    /// A changes-requested review keys on the review's own url(s), which is the same rule read
    /// against the unit that matters there: a reviewer submitting a fresh review is a new url and
    /// so a new obstruction with its own lap count, while a lap that pushed nothing the reviewer
    /// accepted re-reads the identical url and spends the progress cap. Keying on the reviewer's
    /// login would collapse those two, and keying on the findings' text would make an edited
    /// comment look like a different review.
    /// </para>
    /// </summary>
    private static string ObstructionKey(FollowUpKind kind, IReadOnlyList<string> identity) =>
        $"{kind.Value}:{string.Join('␟', identity.OrderBy(id => id, StringComparer.Ordinal))}";

    /// <summary>
    /// The human-readable side of <see cref="ObstructionKey"/> — what a park message reads back as
    /// the obstruction that repeated, and what every reopen records as
    /// <c>TaskReopened.ObstructionSummary</c>. A kind with no arm of its own would describe itself
    /// as unresolved review threads over an identity that is nothing of the sort, which is a guess
    /// written into an audit field (AGENTS.md's never-guess rule) — so every kind names its own
    /// identity here: a replay's is the boundary commit the parent's work is dropped at
    /// (conformance review, cycle 4).
    /// </summary>
    private static string DescribeObstruction(FollowUpKind kind, IReadOnlyList<string> identity) =>
        kind switch
        {
            _ when kind == FollowUpKind.FailingChecks =>
                $"the failing check(s) {string.Join(", ", identity.OrderBy(id => id, StringComparer.Ordinal))}",
            _ when kind == FollowUpKind.Rebase => "the pull request conflicting with its base branch",
            _ when kind == FollowUpKind.StackReplay =>
                "the parent branch having moved past what this branch was built on "
                + $"(boundary {string.Join(", ", identity)})",
            // The identity here is the review url(s) themselves, so the summary names them: what a
            // park reads back has to be the thing the human opens, and a count of reviews would
            // send them hunting for which one.
            _ when kind == FollowUpKind.ReviewRequestedChanges =>
                $"the same changes-requested review(s) {string.Join(", ", identity.OrderBy(id => id, StringComparer.Ordinal))} "
                + "still unanswered",
            _ => $"the same {identity.Count} unresolved review thread(s)",
        };

    /// <summary>
    /// The sentence a changes-requested reopen records, and — when the lifetime budget is already
    /// spent — the opening of the park message the implementer reads (task: a changes-requested
    /// pull-request review from a human becomes a fix lap). It names each reviewer, links each
    /// review, and says how many findings it carried, which is what
    /// <see cref="DecideFollowUpAsync"/>'s budget park needs in front of its own clause so the
    /// park names and links the last review rather than only counting it.
    /// </summary>
    private static string DescribeChangesRequested(IReadOnlyList<ChangesRequestedReview> reviews)
    {
        string described = string.Join("; ", reviews.Select(review =>
        {
            string findings = review.Findings.Count == 1 ? "1 finding" : $"{review.Findings.Count} findings";
            // The submission time is stated only where the provider reported one — a review whose
            // timestamp went unread says so rather than borrowing this sweep's own clock.
            string when = review.SubmittedAt is { } submitted ? $", submitted {submitted:u}" : "";
            return $"@{review.Reviewer} ({findings}{when}) {review.ReviewUrl}";
        }));
        return reviews.Count == 1
            ? $"A human reviewer requested changes on the pull request: {described}."
            : $"{reviews.Count} human reviewers requested changes on the pull request: {described}.";
    }

    /// <summary>
    /// Whether something a human did on the pull request since the task's last automatic
    /// decision is proof this loop is not running away (Decisions Log #80, backlog 45 — origin
    /// incident: Brian re-requesting a Copilot review on PR 26 while an unrelated flat budget
    /// was already spent on two other obstructions). Two mechanical signals, each a set grown
    /// since the comparison point TaskReopened recorded: a review thread neither this nor any
    /// earlier automatic decision has seen, started by a person; and a pending review request
    /// for a reviewer neither this task nor this run's own STILL-OUTSTANDING requests already
    /// account for — the second exclusion is what keeps the platform's own errored-review or
    /// countersign re-requests (RunDetails.RequestedReviewerLogins) from reading back as a
    /// human's. Any one grants the lap; none of them bypasses the lifetime ceiling, which is
    /// checked before this is ever consulted.
    /// <para>
    /// "Still outstanding" is judged fresh every call, via <see cref="StillAwaitingOwnRequest"/>,
    /// rather than by the login ever having appeared in RequestedReviewerLogins: a reviewer the
    /// platform itself asked for answers eventually, and once they have (a fresh review at the
    /// current head, or an errored review that is no longer the active one), that request is
    /// spent. Reading the login as permanently ours would let a LATER, genuinely human
    /// re-request for the same reviewer go unrecognized for the rest of the run's life
    /// (independent pre-PR review, 2026-08-24).
    /// </para>
    /// <para>
    /// A third candidate signal, a new top-level pull-request comment, was cut before merge
    /// (independent pre-PR review, 2026-08-23): agents here post top-level comments too
    /// (answering a review body with `gh pr comment`), authored under the same login as a
    /// human's, so a follow-up's own comment was granting the very lap the cap exists to
    /// refuse — there is no discriminator for a top-level comment the way a review thread's
    /// starter has one (AGENTS.md).
    /// </para>
    /// </summary>
    private static bool HasHumanEngagement(TaskAggregate task, RunDetails run, PullRequestSnapshot snapshot, out string reason)
    {
        List<string> newHumanThreads = [.. snapshot.HumanThreadIds.Except(task.KnownHumanReviewThreadIds)];
        if (newHumanThreads.Count > 0)
        {
            reason = $"{newHumanThreads.Count} new review thread(s) opened by a human";
            return true;
        }

        List<string> stillOwnRequests = [.. run.RequestedReviewerLogins
            .Where(login => StillAwaitingOwnRequest(login, snapshot))];
        List<string> newRequests = [.. snapshot.PendingReviewers
            .Except(task.KnownPendingReviewRequestLogins)
            .Except(stillOwnRequests)];
        if (newRequests.Count > 0)
        {
            reason = $"a review re-request for {string.Join(", ", newRequests)}";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// Whether a reviewer the platform itself asked for a review has not yet answered that
    /// specific request — the condition under which their pending-request login is still ours
    /// to explain rather than a signal a human could have produced. Erroring is unanswered by
    /// definition: they are excluded only while they are the pull request's CURRENT errored
    /// review, since a later, different error is itself a fresh answer that needs its own
    /// re-request (RerequestReviewOrParkAsync). Anyone else is judged the same way the
    /// countersign already decides who still owes an answer (<see cref="ReviewersBehindTheHead"/>):
    /// no recorded review at all, or a recorded review that predates the current head, is still
    /// outstanding; a review sitting on the head is this reviewer answering.
    /// </summary>
    private static bool StillAwaitingOwnRequest(string login, PullRequestSnapshot snapshot)
    {
        if (snapshot.ErroredReview is { } erroredReview && erroredReview.Reviewer == login)
        {
            return true;
        }

        PullRequestReviewer? reviewer = snapshot.Reviewers.FirstOrDefault(candidate => candidate.Login == login);
        return reviewer is null || ReviewersBehindTheHead(snapshot).Any(candidate => candidate.Login == login);
    }

    /// <summary>
    /// <paramref name="stackReplayUpstreamCommit"/> and <paramref name="stackReplayOntoCommit"/> are
    /// set only for <see cref="FollowUpKind.StackReplay"/>, where they are the two commits the
    /// replay rebases between (<c>TaskReopened.StackReplayUpstreamCommit</c> and its own doc's
    /// twin); every other kind passes null for both, explicitly rather than by default, because
    /// <c>CancellationToken</c> comes last (AGENTS.md) and an optional parameter cannot sit in
    /// front of it. <paramref name="predecided"/> is the same for the same reason: null asks this
    /// method to decide, and only <see cref="TryReplayStackedChildAsync"/> passes one, because it
    /// has to know whether this call would park BEFORE it makes an external write it cannot roll
    /// back (see <see cref="DecideFollowUpAsync"/>).
    /// </summary>
    private async Task DispatchFollowUpOrParkAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        long fenceVersion,
        FollowUpKind kind,
        IReadOnlyList<string> obstructionIdentity,
        PullRequestSnapshot snapshot,
        string reason,
        DateTimeOffset now,
        string? pullRequestHeadSha,
        string? stackReplayUpstreamCommit,
        string? stackReplayOntoCommit,
        FollowUpDecision? predecided,
        IReadOnlyList<ChangesRequestedReview>? changesRequestedReviews,
        CancellationToken cancellationToken)
    {
        // Every park verdict is decided before anything is appended or written — including by a
        // caller that already asked (predecided), which is how the stacked retarget knows not to
        // move a pull request's base it would then park with the replay undone.
        FollowUpDecision decision = predecided ?? await DecideFollowUpAsync(
            session, task, run, kind, obstructionIdentity, snapshot, reason, cancellationToken);
        if (decision.ParkReason is { } parkReason)
        {
            await ParkAsync(session, run, parkReason, now, cancellationToken);
            return;
        }

        // The decision's own reason, not the caller's: a human-granted extra lap appends its
        // grant to the sentence the reopen records (DecideFollowUpAsync).
        reason = decision.Reason;
        string obstructionKey = decision.ObstructionKey;
        string obstructionSummary = decision.ObstructionSummary;
        int automaticActionsSpent = decision.AutomaticActionsSpent;
        int lapsIfDispatched = decision.LapsIfDispatched;
        bool humanGranted = decision.HumanGranted;

        // The reopen races the CLI's h9k pr resolve on the fence version captured before
        // the aggregate was read; losing just means someone else already dispatched.
        session.Events.Append(task.Id, expectedVersion: fenceVersion + 1, TaskDecider.Reopen(
            task, run.Id, run.Branch, reason, kind, automatic: true, now, node.OwnerId,
            obstructionKey: obstructionKey,
            obstructionSummary: obstructionSummary,
            knownHumanReviewThreadIds: snapshot.HumanThreadIds,
            knownPendingReviewRequestLogins: snapshot.PendingReviewers,
            pullRequestHeadSha: pullRequestHeadSha,
            stackReplayUpstreamCommit: stackReplayUpstreamCommit,
            stackReplayOntoCommit: stackReplayOntoCommit,
            changesRequestedReviews: changesRequestedReviews,
            // Derived here rather than passed by each caller, because every caller has the same
            // two facts to derive it from and there is one right answer: the snapshot this dispatch
            // acted on. run.ExternalReviewChecksPendingSince is the anchor a previous sweep set;
            // `now` covers the sweep that observes the pending picture for the first time, whose own
            // ExternalReviewObserved append has not been projected back into `run` yet.
            checksPendingSince: snapshot.HasPendingChecks
                ? run.ExternalReviewChecksPendingSince ?? now
                : null));

        // The reopen hands the pull request to a successor, so this run's watch ends
        // with it — retire it in the same transaction (TASK-MODEL.md §2.2). A lost race
        // rolls back both appends and leaves the run watched for the next sweep.
        // Generation + 1 is the generation this reopen grants: Claim always increments,
        // so the successor's claim lands there — recorded now to keep the field's
        // "superseded BY" meaning even though the claim itself commits later.
        session.Events.Append(run.Id, new RunSuperseded(run.Id, task.LeaseGeneration + 1, now));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} was reopened concurrently; skipping this dispatch", task.Id);
            return;
        }

        logger.LogInformation(
            "Task {TaskId} reopened automatically ({Kind}, lifetime {Attempt}/{Max}, obstruction lap {Lap}/{LapMax}{Grant}): {Reason}",
            task.Id, kind.Value, automaticActionsSpent + 1, _options.MaxAutomaticCloseoutRuns,
            lapsIfDispatched, _options.MaxCloseoutLapsPerObstruction, humanGranted ? " human-granted" : "", reason);
    }

    /// <summary>
    /// What one automatic follow-up decision came to, decided over nothing but reads — no append,
    /// no provider write — so a caller can ask before it does something it cannot undo.
    /// <see cref="ParkReason"/> non-null means this decision parks; every other field describes the
    /// dispatch it would otherwise make.
    /// </summary>
    /// <param name="Reason">
    /// The sentence the reopen records — the caller's own, plus the human-grant clause when one was
    /// granted, which is why the dispatch reads this rather than what it passed in.
    /// </param>
    private readonly record struct FollowUpDecision(
        string? ParkReason,
        string Reason,
        string ObstructionKey,
        string ObstructionSummary,
        int AutomaticActionsSpent,
        int LapsIfDispatched,
        bool HumanGranted);

    /// <summary>
    /// The three park verdicts every automatic follow-up is subject to — an unmet dependency, the
    /// lifetime automatic-closeout ceiling, the per-obstruction progress cap — asked without
    /// changing anything, in the order they are asked in.
    /// <para>
    /// Split out of <see cref="DispatchFollowUpOrParkAsync"/> so
    /// <see cref="TryReplayStackedChildAsync"/> can ask the question BEFORE it retargets a pull
    /// request on GitHub (independent pre-PR review, cycle 1, adversarial lens). That retarget is
    /// an external write with no rollback, and a park landing after it leaves exactly the
    /// incoherent state the rebase-budget check ahead of it already refuses to create: a pull
    /// request aimed at the project's base still carrying the parent's duplicated commits, with no
    /// dispatch coming. Sharing the decision rather than duplicating it is what keeps the answer
    /// asked before the write identical to the one enforced after it.
    /// </para>
    /// </summary>
    private async Task<FollowUpDecision> DecideFollowUpAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        FollowUpKind kind,
        IReadOnlyList<string> obstructionIdentity,
        PullRequestSnapshot snapshot,
        string reason,
        CancellationToken cancellationToken)
    {
        string obstructionKey = ObstructionKey(kind, obstructionIdentity);
        string obstructionSummary = DescribeObstruction(kind, obstructionIdentity);

        // TaskAggregate.Apply(TaskReopened) never clears _unmetDependencies (only Assign does),
        // so a task claimed with h9k task start --acknowledge-unmet-dependencies that reached
        // Done while a dependency was still open would land back on Blocked, not Queued, on the
        // very same TaskReopened the dispatch is about to append — the same fact
        // PullRequestResolveCommand already reconciles for the human lever (its own comment at
        // TaskDecider.Reopen's call site). Appending it anyway would supersede this run —
        // retiring the only thing watching the pull request — while nothing ever claims the
        // now-Blocked task to dispatch a follow-up: DispatchEngine.ClaimEligibleAsync filters on
        // state = 'Queued', so the PR goes unwatched until the dependency closes out, and a merge
        // inside that window is never observed (independent pre-PR review, cycle 1, conformance
        // lens). Park instead: CloseoutParked stays in the watched query above, so merge/close
        // detection keeps running every sweep (InspectAndActAsync's own "gets merge/close
        // detection only" branch). Nothing else resumes the park — InspectAndActAsync returns
        // early for a CloseoutParked run before it ever re-reads task.UnmetDependencies, so
        // clearing the dependency alone does not dispatch a follow-up; only a human running
        // h9k pr resolve does, which already tolerates the same Blocked landing.
        if (task.UnmetDependencies.Count > 0)
        {
            string dependencyNoun = task.UnmetDependencies.Count == 1 ? "dependency" : "dependencies";
            return Park(
                $"{reason} Task {task.Id} still has {task.UnmetDependencies.Count} unmet {dependencyNoun} — " +
                "an automatic follow-up would land it Blocked rather than dispatch, so the pull request stays " +
                "parked here, watched, instead. Nothing resumes this park on its own — clearing the dependency " +
                "alone will not; h9k pr resolve is what forces a follow-up.");
        }

        // The lifetime ceiling is checked next, ahead of everything below it: it is the true
        // runaway backstop (Decisions Log #80, backlog 45), and no human engagement bypasses
        // it — only h9k pr resolve does. (The unmet-dependency short-circuit above it runs
        // first, since a task with a dependency this run cannot clear needs to park before the
        // ceiling is ever consulted.)
        int automaticActionsSpent = await AutomaticActionsSpentAsync(session, task, cancellationToken);
        if (automaticActionsSpent >= _options.MaxAutomaticCloseoutRuns)
        {
            return Park(
                $"{reason} The lifetime automatic closeout budget spent ({automaticActionsSpent}/{_options.MaxAutomaticCloseoutRuns} action(s)) — " +
                $"{DescribeAutomaticLapHistory(task, automaticActionsSpent)}. " +
                "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve.");
        }

        bool sameObstruction = obstructionKey == task.LastAutomaticObstructionKey;
        int lapsIfDispatched = sameObstruction ? task.ConsecutiveObstructionLaps + 1 : 1;
        bool exceedsProgressCap = lapsIfDispatched > _options.MaxCloseoutLapsPerObstruction;

        bool humanGranted = false;
        if (exceedsProgressCap && HasHumanEngagement(task, run, snapshot, out string engagement))
        {
            humanGranted = true;
            reason =
                $"{reason} A human engaged with the pull request since the last automatic decision " +
                $"({engagement}) — granting one more automatic lap despite the per-obstruction cap.";
        }

        if (exceedsProgressCap && !humanGranted)
        {
            // sameObstruction is false only when the cap itself is below 1: lapsIfDispatched
            // is always at least 1, so a brand-new obstruction only ever exceeds the cap when
            // there is no room for even a first lap. task.ConsecutiveObstructionLaps counts a
            // DIFFERENT, earlier obstruction in that case, so asserting it "survived" that many
            // laps would report an unobserved fact about an obstruction this park never saw
            // (AGENTS.md: never guess at unobserved facts).
            return Park(sameObstruction
                ? $"{reason} The same obstruction — {obstructionSummary} — survived {task.ConsecutiveObstructionLaps} " +
                  $"automatic lap(s) without clearing (cap {_options.MaxCloseoutLapsPerObstruction} per obstruction). " +
                  "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve."
                : $"{reason} This is a new obstruction — {obstructionSummary} — but the cap " +
                  $"{_options.MaxCloseoutLapsPerObstruction} per obstruction leaves no room for even one automatic lap on it. " +
                  "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve.");
        }

        return new FollowUpDecision(
            ParkReason: null, reason, obstructionKey, obstructionSummary, automaticActionsSpent,
            lapsIfDispatched, humanGranted);

        // A park carries the obstruction's identity too: nothing dispatches, but a caller that
        // asked ahead of a write still has the obstruction this decision was about in hand. The
        // two counters are zero rather than half-read — a parked decision never dispatched a lap,
        // and reporting one it did not take would state an unobserved fact.
        FollowUpDecision Park(string parkReason) => new(
            parkReason, reason, obstructionKey, obstructionSummary,
            AutomaticActionsSpent: 0, LapsIfDispatched: 0, HumanGranted: false);
    }

    /// <summary>
    /// One sweep's decision about a stacked child whose pull request still targets its parent's
    /// branch (task: a stacked pull-request edge exists as an explicit opt-in dependency). Returns
    /// true when this sweep acted — retargeted, dispatched a replay, or parked — which ends the
    /// inspection here: everything below this call site (the conflict read, CI, review threads)
    /// describes a diff a replay is about to supersede. Returns false when the child is still built
    /// on its parent's current head, or when nothing could be observed, and the ordinary inspection
    /// carries on unchanged.
    /// <para>
    /// Two triggers, one action. The parent's pull request merged: the child's own pull request is
    /// retargeted onto the project's base branch, and a replay drops the parent's now-duplicated
    /// commits (the project rebase-merges, so the parent's work is on the base under new SHAs). Or
    /// the parent's branch was force-pushed: the base stays where it is and only the replay is
    /// owed, onto the parent's new head. Both are the same mechanical operation with a different
    /// <c>--onto</c>, which is why they share one follow-up kind and one budget.
    /// </para>
    /// <para>
    /// Two budgets bound this path, deliberately asymmetrically. The rebase budget below is the
    /// one a replay <em>spends</em> (<c>TaskAggregate.StackReplaysDispatched</c>), and a replay
    /// spends nothing else — see that field's own doc for why charging the child's review budget
    /// for its parent's activity would be wrong. But the lifetime automatic-closeout ceiling
    /// <see cref="DispatchFollowUpOrParkAsync"/> checks still <em>applies</em>: it is the true
    /// runaway backstop that only <c>h9k pr resolve</c> lifts (Decisions Log #80), and a child that
    /// has already spent six automatic laps of its own AND whose parent will not stop moving is
    /// exactly the case that wants a human, whichever counter names it. Free to spend, still
    /// subject to the backstop — and subject to it BEFORE the retarget, not after: every park
    /// verdict the dispatch is capable of reaching is asked through
    /// <see cref="DecideFollowUpAsync"/> ahead of the provider write, so no park can land on a
    /// pull request whose base has already been moved with the replay undone.
    /// </para>
    /// <para>
    /// A retarget that fails dispatches nothing. The record says so (<see cref="StackedPullRequestRetargeted"/>
    /// carries <c>Succeeded: false</c>) and the next sweep tries again, which is right: replaying
    /// onto the project's base while the pull request is still aimed at a deleted branch would
    /// leave a pull request nobody can merge and nothing left to explain it. The reverse order — a
    /// successful retarget whose reopen then loses the fence race — is safe by construction: the
    /// rollback takes the retarget record with it, so the next sweep reads the base as unchanged and
    /// simply retargets again, and <c>gh pr edit --base</c> on an already-retargeted pull request
    /// succeeds.
    /// </para>
    /// </summary>
    private async Task<bool> TryReplayStackedChildAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        ProjectDetails project,
        long fenceVersion,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StackedParentObservation observation = await stackedParents.ObserveAsync(
            session, project, StackedParentDeclaration.From(task), run, cancellationToken);

        if (observation.Verdict == StackedParentVerdict.Aligned)
        {
            return false;
        }

        // Unobservable and ParentUnresolvable are answered together here, and only here: the first
        // is a read that failed and the second is a parent head that does not exist to be read
        // (their own docs draw the line), but from an already-open pull request both come to the
        // same thing — no replay is owed on what was not observed, and the next sweep asks again.
        // The review loop's own checkpoints part company on exactly this pair, because a checkpoint
        // has no next sweep before the final pass runs: it parks on the second and proceeds on the
        // first.
        if (observation.Verdict is StackedParentVerdict.Unobservable or StackedParentVerdict.ParentUnresolvable)
        {
            // Nothing learned, so nothing claimed and nothing spent. Returning false — letting the
            // ordinary inspection carry on — is deliberate rather than a bail-out: a stacked child
            // with failing checks or unresolved threads still owes those answers, and refusing to
            // look at them because one git call failed would strand the pull request on a transient.
            logger.LogInformation(
                "Task {TaskId}: stacked child not evaluated this sweep — {Detail}", task.Id, observation.Detail);
            return false;
        }

        if (observation.Verdict == StackedParentVerdict.ParentDead)
        {
            // The parent's branch is never going anywhere (task: a stacked child absorbs its
            // parent's post-delivery churn safely), so this child's pull request cannot be
            // retargeted mechanically and must not keep being replayed onto a base with no future.
            // Parked for the same reason ParentMergedElsewhere is: nothing about it changes on its
            // own, and a park with the situation named leaves a human a coherent stack rather than
            // a child quietly following a dead parent.
            // Which levers this names is load-bearing, and "hand it back with h9k pr resolve" on
            // its own would not be one of them (independent pre-PR review, cycle 1, adversarial
            // lens, by its own class sweep — the checkpoint's twin park carried the same defect):
            // a follow-up run carries the previous run's recorded base forward rather than
            // re-resolving it (StackedBaseResolver.ResumedBaseAsync, deliberately), so a hand
            // retarget on GitHub does not change what the next sweep observes and this park comes
            // straight back. What does change it is the parent itself reaching Delivered again.
            await ParkAsync(
                session, run,
                $"This is a stacked pull request and {observation.Detail}. If that parent should live after "
                + "all, put it back on its feet first — h9k task retry for one that ended Failed, h9k pr resolve "
                + "for one whose own pull request closed unmerged, nothing for an abandoned one — and then hand "
                + "this one back with h9k pr resolve. Otherwise this pull request is yours to land or close: "
                + "handing it back does not move it onto another base, because a run carries the base it was "
                + "dispatched against and a claimed task's stacked edge cannot be revised, so work that belongs "
                + "on the project's base continues as a fresh, unstacked task.",
                now, cancellationToken);
            return true;
        }

        if (observation.Verdict == StackedParentVerdict.ParentMergedElsewhere)
        {
            // The one verdict that is neither "nothing to do" nor "a replay is owed": the parent
            // merged somewhere other than the project's base, so there is no base this child can be
            // moved onto mechanically without losing work (the verdict's own doc). Parked rather
            // than left to the next sweep, because nothing about it will change on its own — and
            // parked BEFORE any retarget, like every other park on this path.
            await ParkAsync(
                session, run,
                $"This is a stacked pull request and {observation.Detail}. Retarget and rebase it by hand — "
                + "onto the branch its parent merged into, or onto the project's base once that branch's own "
                + "pull request has merged — then hand it back with h9k pr resolve.",
                now, cancellationToken);
            return true;
        }

        // Everything past here is ParentMerged or ParentMoved: the parent's branch moved out from
        // under this child, and a replay is owed. Written as early returns above rather than a
        // switch precisely so that reading is explicit — a switch statement's silent fall-through
        // would route a verdict added later into the replay path by default.

        // The rebase budget (DaemonOptions.MaxStackReplayRuns). Checked before the retarget, not
        // after: a child already past its cap must not have its pull request moved and then be
        // parked with the replay undone — the human would inherit a branch aimed at the project's
        // base still carrying the parent's duplicated commits, with no dispatch coming to fix it.
        // Parking with the base untouched leaves a coherent stack for them to finish by hand.
        if (task.StackReplaysDispatched >= _options.MaxStackReplayRuns)
        {
            await ParkAsync(
                session, run,
                $"This is a stacked pull request and {observation.Detail}. Its rebase budget is spent "
                // "rebase(s)", not "replay(s)": this counter is spent by the checkpoint rebases a
                // child takes in-run as well as by the replays dispatched here, so naming only one
                // of the two would misreport what the number counted (task: a stacked child absorbs
                // its parent's post-delivery churn safely).
                + $"({task.StackReplaysDispatched}/{_options.MaxStackReplayRuns} rebase(s)) — the parent branch has "
                + "kept moving faster than this branch can follow it. Rebase and retarget this pull request by "
                + "hand, or grant another attempt with h9k pr resolve.",
                now, cancellationToken);
            return true;
        }

        string replayReason =
            $"This is a stacked pull request and {observation.Detail}. The replay is mechanical — the "
            + "same commits onto a new base, no new intent — so it runs the gates and no review cycle.";

        // Asked here, ahead of the retarget, for the same reason the rebase budget above is: the
        // dispatch below is subject to three more park verdicts — an unmet dependency, the lifetime
        // automatic-closeout ceiling, the per-obstruction cap — and every one of them would
        // otherwise land AFTER `gh pr edit --base` has already moved this pull request, leaving a
        // human a pull request aimed at the project's base still carrying the parent's duplicated
        // commits with no dispatch coming (independent pre-PR review, cycle 1, adversarial lens).
        // Parking with the base untouched leaves a coherent stack to finish by hand instead. The
        // same decision is handed to the dispatch below rather than re-asked, so the verdict that
        // permitted the retarget is exactly the one enforced after it.
        FollowUpDecision decision = await DecideFollowUpAsync(
            session, task, run, FollowUpKind.StackReplay,
            [observation.BoundaryCommit], snapshot, replayReason, cancellationToken);
        if (decision.ParkReason is { } parkReason)
        {
            await ParkAsync(session, run, parkReason, now, cancellationToken);
            return true;
        }

        if (observation.Verdict == StackedParentVerdict.ParentMerged)
        {
            StackedRetargetOutcome retarget = await TryRetargetStackedChildAsync(
                run, project, observation.ParentBranch, cancellationToken);
            session.Events.Append(run.Id, new StackedPullRequestRetargeted(
                run.Id, observation.ParentBranch, project.BaseBranch, observation.BoundaryCommit,
                retarget.Succeeded, retarget.Detail, now));

            if (!retarget.Succeeded)
            {
                await session.SaveChangesAsync(cancellationToken);
                logger.LogWarning(
                    "Task {TaskId}: could not retarget stacked pull request {Url} from {ParentBranch} onto "
                    + "{BaseBranch} — {Detail}; no replay dispatched, the next sweep tries again",
                    task.Id, task.PullRequestUrl, observation.ParentBranch, project.BaseBranch, retarget.Detail);
                return true;
            }

            logger.LogInformation(
                "Task {TaskId}: stacked pull request {Url} retargeted from {ParentBranch} onto {BaseBranch}",
                task.Id, task.PullRequestUrl, observation.ParentBranch, project.BaseBranch);
        }

        await DispatchFollowUpOrParkAsync(
            session, task, run, fenceVersion,
            FollowUpKind.StackReplay,
            // The boundary is the obstruction's identity: a parent head this child has already been
            // replayed off is the same obstruction, and a parent that moved again is mechanically a
            // new one — the identical reading ObstructionKey's own doc gives a conflict's head
            // commit. The per-obstruction cap is not what bounds this path (the rebase budget above
            // is), but recording an honest identity keeps a park message truthful about what
            // repeated.
            [observation.BoundaryCommit],
            snapshot,
            replayReason,
            // No opening-review scope seed: a replay dispatches with ReviewStageComposition.None, so
            // there is no Discovery cycle for a since-sha to scope (RunLauncher forces that
            // composition for this kind).
            now, pullRequestHeadSha: null,
            stackReplayUpstreamCommit: observation.BoundaryCommit,
            stackReplayOntoCommit: observation.OntoCommit,
            predecided: decision, changesRequestedReviews: null,
            cancellationToken);
        return true;
    }

    /// <summary>What one retarget attempt did — the same shape as <see cref="MechanicalRebaseOutcome"/>, for the same reason.</summary>
    private readonly record struct StackedRetargetOutcome(bool Succeeded, string Detail);

    /// <summary>
    /// Moves the child pull request's base from the parent's branch onto the project's own, through
    /// the provider seam every other write in this engine goes through
    /// (<see cref="IPullRequestInspector.RetargetAsync"/>) — the one provider write this whole
    /// feature makes. Runs against the project's repository rather than the run's retained
    /// worktree, which can be gone by now. A failure of any kind comes back as a plain, checkable
    /// reason rather than an exception: the caller records it and the next sweep tries again.
    /// </summary>
    private async Task<StackedRetargetOutcome> TryRetargetStackedChildAsync(
        RunDetails run, ProjectDetails project, string parentBranch, CancellationToken cancellationToken)
    {
        // Both halves of the pull request's identity, checked here rather than assumed from the
        // caller's own guard: InspectAndActAsync verified the TASK's url and this RUN's number,
        // which come from the same PullRequestOpened event but are not the same field — and a
        // null-forgiving `!` on the url would turn a missing one into an NRE the catch below
        // records as a failure, which is a retarget that never progresses instead of a refusal
        // that says why (AGENTS.md's own rule about `!` where nothing guarantees non-null).
        if (run.PullRequestNumber is not > 0 || run.PullRequestUrl is not { } pullRequestUrl)
        {
            return new StackedRetargetOutcome(
                false, "this run records no pull request of its own, so there is nothing to retarget");
        }

        try
        {
            await inspector.RetargetAsync(
                project.RepositoryPath, pullRequestUrl, run.PullRequestNumber.Value, project.BaseBranch,
                cancellationToken);
            return new StackedRetargetOutcome(true, $"retargeted from {parentBranch} onto {project.BaseBranch}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Every provider write on this seam throws on failure (IPullRequestInspector's own
            // convention), and this one is caught rather than left to escape for the same reason
            // the mechanical rebase catches its own: an escape would skip the record this outcome
            // exists to produce and abandon the rest of this run's inspection.
            return new StackedRetargetOutcome(
                false, $"retargeting onto {project.BaseBranch} failed: {FirstLine(exception.Message)}");
        }
    }

    /// <summary>What the mechanical rebase attempt actually did, for the caller to record and act on.</summary>
    private readonly record struct MechanicalRebaseOutcome(bool Succeeded, string Detail, string? PushedCommit);

    /// <summary>
    /// The same reasoning as <c>PullRequestOpener.PushDeadline</c>: <see cref="ExternalProcess.Deadline"/>
    /// (120 seconds) is sized for a short metadata read, not a fetch or a push that transfers real
    /// data, and this path's own fetch/rebase/push runs the identical risk on a large repository or
    /// a slow uplink that the plain runner would kill mid-transfer (independent pre-PR review,
    /// cycle 1, adversarial lens — sibling site swept from the PullRequestOpener.cs:285 finding).
    /// </summary>
    private static readonly TimeSpan GitDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Recommendation 3 (idea fc85f609, amended by Brian 2026-09-04): before the pull request is
    /// reopened for a full agent review lap, try the mechanical fix first — <c>git fetch</c> then
    /// <c>git rebase</c> onto <c>origin/&lt;base&gt;</c> in the run's own retained worktree, no
    /// model session and no local build/test gates. GitHub's own CI on the push is the
    /// authoritative gate at the merge bar (Brian's ruling), so a local gate here would only
    /// duplicate it; validation is left entirely to the push that follows a clean apply.
    /// <para>
    /// Real git, never the injected <see cref="ProcessRunner"/> this class otherwise uses for
    /// <c>gh</c>/Jira: that seam exists to be faked in tests, and GitWorktreeManager's and
    /// PullRequestOpener's own git calls are never faked either — this mechanical path follows
    /// the identical convention so a genuinely conflicting (or genuinely clean) git history is
    /// what decides the outcome.
    /// </para>
    /// <para>
    /// The fetch, rebase and push are taken under <see cref="IWorktreeManager.AcquireRepositoryLockAsync"/>
    /// on <paramref name="project"/>'s own repository — the same lock every worktree-add/fetch
    /// operation on that repository already serializes behind (Decisions Log #4, adversarial
    /// review cycle 4), so this path cannot lose a ref update to, or clobber, a dispatch or an
    /// operator's <c>h9k task work</c> racing the same repository (independent pre-PR review,
    /// cycle 1, both lenses).
    /// </para>
    /// <para>
    /// That lock's own wait is unbounded by design, so the fence captured before this method was
    /// called can go stale while this attempt merely waits its turn — a human's <c>h9k pr resolve</c>
    /// racing the same task's own reopen, landing and dispatching a follow-up agent into this exact
    /// worktree before the lock is ever granted here. <paramref name="fenceVersion"/> is
    /// re-validated immediately after the lock is acquired and before anything in the worktree is
    /// touched; a stale fence bails out as a fallback with no git call made at all, rather than
    /// risking a rebase and force-push out from under a follow-up session that may already be
    /// live in this worktree (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// <para>
    /// A rebase that lands exactly where it started (HEAD unchanged) made no progress against
    /// whatever GitHub actually thinks conflicts — the pull request's base was retargeted away
    /// from <paramref name="project"/>'s own base branch being the recurring case — so it is
    /// reported as a fallback rather than a success, the same as a rebase that fails outright:
    /// otherwise nothing would ever stop this path from fetching, rebasing and force-pushing the
    /// identical no-op every sweep, forever, with no budget spent and no park (independent pre-PR
    /// review, cycle 1, both lenses).
    /// </para>
    /// <para>
    /// Every fallback is a plain, checkable reason and touches nothing beyond the worktree's own
    /// git state: a missing retained worktree, one not checked out on the run's own branch, one
    /// with uncommitted changes, a fetch failure, a rebase that did not apply cleanly (aborted
    /// before returning, leaving the worktree exactly as it was), a rebase that made no progress,
    /// or a push the ancestor-or-reflog guard refused. Whichever it is, the ordinary
    /// reopen-and-review lap picks the branch back up exactly as it always has — nothing here
    /// changes what that path does when it runs.
    /// </para>
    /// <para>
    /// A <see cref="TimeoutException"/> from any of the fetch, rebase or push calls (the
    /// <see cref="GitDeadline"/> expiring, or a wedged credential helper's own
    /// <see cref="ProcessOutputStuckException"/>, itself a <see cref="TimeoutException"/>) is
    /// caught here rather than left to escape into the sweep's own outer handler: an escape skips
    /// the outcome event this method's caller appends, skips the worktree restore below, and skips
    /// the fallback reopen-and-review lap for this sweep entirely, leaving the worktree silently
    /// rebased (or, if the rebase itself was killed mid-apply, mid-rebase and detached) with
    /// nothing pushed (independent pre-PR review, cycle 1, both lenses — the sibling site,
    /// <c>PullRequestOpener.PushBranchAsync</c>, already catches this for exactly this reason).
    /// Recovery is best-effort and always attempted in the same order regardless of which call
    /// timed out: <c>git rebase --abort</c> restores an interrupted rebase's original branch and
    /// tip (a no-op, harmless failure when no rebase is in progress), then a hard reset to
    /// <c>preRebaseHead</c> undoes a rebase that had already completed by the time a later call —
    /// the push — timed out.
    /// </para>
    /// </summary>
    private async Task<MechanicalRebaseOutcome> TryMechanicalRebaseAsync(
        IDocumentSession session, long fenceVersion, ProjectDetails project, RunDetails run,
        CancellationToken cancellationToken)
    {
        ProcessRunner git = ExternalProcess.RunnerWithDeadline(GitDeadline);
        string worktreePath = run.WorktreePath;

        if (worktreePath.IsBlank() || !Directory.Exists(worktreePath))
        {
            return new MechanicalRebaseOutcome(false, "the run's retained worktree is missing", null);
        }

        ProcessResult branchCheck = await git(
            "git", ["rev-parse", "--abbrev-ref", "HEAD"], worktreePath, cancellationToken);
        if (branchCheck.ExitCode != 0 || branchCheck.StandardOutput.Trim() != run.Branch)
        {
            return new MechanicalRebaseOutcome(
                false, "the retained worktree is not usable — it is not checked out on the run's own branch", null);
        }

        ProcessResult statusCheck = await git("git", ["status", "--porcelain"], worktreePath, cancellationToken);
        if (statusCheck.ExitCode != 0 || statusCheck.StandardOutput.Trim().Length > 0)
        {
            return new MechanicalRebaseOutcome(
                false, "the retained worktree is not usable — it has uncommitted changes", null);
        }

        await using IAsyncDisposable repositoryLock =
            await worktrees.AcquireRepositoryLockAsync(project.RepositoryPath, cancellationToken);

        // The lock wait above is unbounded, so re-check the fence this attempt was handed before
        // touching the worktree at all: a reopen that landed while this attempt waited may already
        // have a follow-up agent working here (this method's own doc comment; independent pre-PR
        // review, cycle 1, adversarial lens). Bailing out here makes no git call and leaves the
        // worktree exactly as found, so the caller's own fallback append (whose expectedVersion is
        // fenced identically) is the one place this race is actually resolved.
        StreamState? fenceAfterLock = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (fenceAfterLock is null || fenceAfterLock.Version != fenceVersion)
        {
            return new MechanicalRebaseOutcome(
                false,
                "the task advanced while this attempt waited for the repository lock — a concurrent "
                + "reopen may already have a follow-up agent working in this worktree, so nothing here touched it",
                null);
        }

        ProcessResult preRebaseHeadResult = await git("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
        string? preRebaseHead = preRebaseHeadResult.ExitCode == 0 ? preRebaseHeadResult.StandardOutput.Trim() : null;
        bool rebaseApplied = false;

        try
        {
            ProcessResult fetch = await git("git", ["fetch", "origin", project.BaseBranch], worktreePath, cancellationToken);
            if (fetch.ExitCode != 0)
            {
                return new MechanicalRebaseOutcome(
                    false, $"git fetch origin {project.BaseBranch} failed: {FirstLine(fetch.StandardError)}", null);
            }

            ProcessResult rebase = await git(
                "git", ["rebase", $"origin/{project.BaseBranch}"], worktreePath, cancellationToken);
            if (rebase.ExitCode != 0)
            {
                // No hard reset here: git rebase --abort already restores the worktree fully when
                // a rebase actually started, and when it didn't (rebase refused to start against a
                // dirty tree it did not create — see RestoreWorktreeBestEffortAsync's own doc
                // comment) a hard reset would discard whatever the working tree already held
                // instead of anything this attempt made (independent pre-PR review, cycle 1,
                // adversarial lens).
                await RestoreWorktreeBestEffortAsync(git, worktreePath, preRebaseHead, rebaseApplied: false, cancellationToken);
                return new MechanicalRebaseOutcome(
                    false,
                    $"git rebase onto origin/{project.BaseBranch} did not apply cleanly: {FirstLine(rebase.StandardError)}",
                    null);
            }

            rebaseApplied = true;

            ProcessResult headCommit = await git("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
            string pushedCommit = headCommit.ExitCode == 0 ? headCommit.StandardOutput.Trim() : "unknown";

            if (preRebaseHead is not null && preRebaseHead == pushedCommit)
            {
                return new MechanicalRebaseOutcome(
                    false,
                    $"git rebase onto origin/{project.BaseBranch} made no change (already at {pushedCommit}) — "
                    + "GitHub's own conflict read is against something this rebase cannot address",
                    null);
            }

            try
            {
                await ForceWithLeasePusher.PushAsync(git, worktreePath, run.Branch, cancellationToken);
            }
            catch (ProcessOutputStuckException exception) when (exception.ExitCode == 0)
            {
                // git itself exited 0 — the read or the push it was running genuinely completed —
                // but a spawned credential helper held the output pipe open past
                // ExternalProcess.DrainGrace (ProcessOutputStuckException's own doc comment: exit
                // code 0 here can tell a genuine success from a genuine failure). Exit 0 alone does
                // not say *which* of ForceWithLeasePusher's own git calls (the read-only
                // ls-remote/merge-base/reflog, or the push itself) is the one that got stuck, so
                // origin's actual tip for the branch is read back rather than assumed — never guess
                // at unobserved facts — before deciding whether to roll back a rebase that may
                // already be live on origin (independent pre-PR review, cycle 1, both lenses).
                if (await OriginHeadMatchesAsync(git, worktreePath, run.Branch, pushedCommit, cancellationToken))
                {
                    return new MechanicalRebaseOutcome(
                        true,
                        $"Rebased onto origin/{project.BaseBranch} and force-pushed (new head {pushedCommit}) — "
                        + $"the push itself exited 0 before a credential helper's stuck output pipe timed the "
                        + "call out.",
                        pushedCommit);
                }

                await RestoreWorktreeBestEffortAsync(git, worktreePath, preRebaseHead, rebaseApplied, cancellationToken);
                return new MechanicalRebaseOutcome(
                    false,
                    $"the force-push after the mechanical rebase timed out ({exception.Message}) and origin "
                    + "does not yet hold the rebased tip",
                    null);
            }
            catch (InvalidOperationException exception)
            {
                // Restores the worktree to the tip it held before this attempt's rebase, rather than
                // leaving the branch rewritten onto the new base with nothing pushed: every other
                // fallback above already leaves the worktree exactly as it was, and the ordinary
                // reopen-and-review lap this fallback triggers hands the follow-up agent a prompt
                // telling it the branch "now conflicts with main" and to rebase it — true only if
                // this rebase is undone first (independent pre-PR review, cycle 1, adversarial lens).
                await RestoreWorktreeBestEffortAsync(git, worktreePath, preRebaseHead, rebaseApplied, cancellationToken);
                return new MechanicalRebaseOutcome(
                    false, $"the force-push after the mechanical rebase was refused: {exception.Message}", null);
            }

            return new MechanicalRebaseOutcome(
                true,
                $"Rebased onto origin/{project.BaseBranch} and force-pushed cleanly (new head {pushedCommit}).",
                pushedCommit);
        }
        catch (TimeoutException exception)
        {
            // A git call exceeded GitDeadline, or a credential helper wedged the output pipe past
            // the drain grace (ProcessOutputStuckException, also a TimeoutException) — see this
            // method's own doc comment for why this must not be left to escape uncaught the way it
            // did before (independent pre-PR review, cycle 1, both lenses). The push's own exit-0
            // stuck-output case is handled above, before it ever reaches here.
            await RestoreWorktreeBestEffortAsync(git, worktreePath, preRebaseHead, rebaseApplied, cancellationToken);
            return new MechanicalRebaseOutcome(
                false, $"a git call exceeded its deadline during the mechanical rebase: {exception.Message}", null);
        }
    }

    /// <summary>
    /// Reads origin's actual current tip for <paramref name="branch"/> and compares it against
    /// <paramref name="expectedCommit"/>, rather than trusting a git call's own exit code for
    /// whether a push actually landed — the one fact this class's mechanical-rebase fast path
    /// cannot afford to guess at when a push call's own answer was lost to a stuck output pipe.
    /// </summary>
    private static async Task<bool> OriginHeadMatchesAsync(
        ProcessRunner git, string worktreePath, string branch, string expectedCommit, CancellationToken cancellationToken)
    {
        ProcessResult originTip = await git(
            "git", ["ls-remote", "--exit-code", "origin", $"refs/heads/{branch}"], worktreePath, cancellationToken);
        string? originHead = originTip.ExitCode == 0
            ? originTip.StandardOutput.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;
        return originHead is not null && originHead == expectedCommit;
    }

    /// <summary>
    /// Best-effort recovery shared by every mechanical-rebase failure past the rebase itself:
    /// restores the worktree to the tip it held before this attempt started, rather than leaving it
    /// rewritten onto the new base with nothing pushed. <c>git rebase --abort</c> covers a rebase
    /// interrupted mid-apply — a killed process leaves a detached HEAD and rebase-merge state, and
    /// <c>--abort</c> restores the original branch and tip — and fails harmlessly when no rebase is
    /// in progress.
    /// <para>
    /// The hard reset runs only when <paramref name="rebaseApplied"/> says the rebase itself had
    /// already completed cleanly by the time a later call (the push) failed instead — the one case
    /// <c>git rebase --abort</c> does not already cover. When the rebase never got that far (it
    /// refused to start, or failed mid-apply and <c>--abort</c> above already restored it), a hard
    /// reset would not be undoing anything this attempt did: it would instead discard whatever the
    /// working tree already held before this sweep ever touched it, including uncommitted changes
    /// an operator made in this same worktree while this attempt sat on the repository lock's own
    /// unbounded wait (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// Recovery failures are logged, never thrown: a failed recovery must not mask the original
    /// failure the caller is already reporting.
    /// </summary>
    private async Task RestoreWorktreeBestEffortAsync(
        ProcessRunner git, string worktreePath, string? preRebaseHead, bool rebaseApplied, CancellationToken cancellationToken)
    {
        try
        {
            await git("git", ["rebase", "--abort"], worktreePath, cancellationToken);

            if (rebaseApplied && preRebaseHead is not null)
            {
                await git("git", ["reset", "--hard", preRebaseHead], worktreePath, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Mechanical rebase recovery failed to restore {Path} to {Head}",
                worktreePath, preRebaseHead ?? "(unknown)");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [string first, ..]
            ? first
            : text.Trim();

    /// <summary>Appends CloseoutParked and logs it — shared by both DispatchFollowUpOrParkAsync park branches.</summary>
    private async Task ParkAsync(
        IDocumentSession session, RunDetails run, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        session.Events.Append(run.Id, new CloseoutParked(run.Id, reason, now));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Run {RunId}: closeout parked for the human — {Reason}", run.Id, reason);
    }

    private async Task RemoveWorktreeBestEffortAsync(
        string repositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(worktreePath))
            {
                await worktrees.RemoveAsync(repositoryPath, worktreePath, cancellationToken);
            }
            else
            {
                // Gone out-of-band (crash, manual rm): collect the stale registration
                // now rather than leaving it for the startup prune.
                await worktrees.PruneAsync(repositoryPath, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", worktreePath);
        }
    }
}
