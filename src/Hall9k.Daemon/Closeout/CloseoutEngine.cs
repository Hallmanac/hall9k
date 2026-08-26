using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
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
/// makes. <see cref="PullRequestMonitor"/> reads this to widen its own poll interval rather than
/// re-hitting a failing <c>gh</c> every tick forever (independent pre-PR review, cycle 3); it is
/// not itself a retry signal here — the run stays in the watch set and the very next sweep tries
/// it again regardless of what this count says.
/// </param>
public sealed record CloseoutSweepResult(int RunsInspected, int MergesObserved, int Failures = 0);

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
            // re-request waits for the reviewer to answer.
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

        return new CloseoutSweepResult(inspected, merges, failures);
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

        if (task.State != TaskState.Done || task.PullRequestUrl.IsBlank() || run.PullRequestNumber is not > 0)
        {
            return InspectionOutcome.Skipped;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
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
            return InspectionOutcome.Skipped;
        }

        if (snapshot.IsMerged)
        {
            await CompleteCloseoutAsync(session, run, project, task, snapshot.MergedAt, DateTimeOffset.UtcNow, cancellationToken);
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

        // Only a Done task is in closeout; a reopened one has a follow-up in flight.
        if (task.State != TaskState.Done || task.PullRequestUrl.IsBlank() || run.PullRequestNumber is not > 0)
        {
            return InspectionOutcome.Skipped;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
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
            return InspectionOutcome.Skipped;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot.IsMerged)
        {
            await CompleteCloseoutAsync(session, run, project, task, snapshot.MergedAt, now, cancellationToken);
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

        // Checked ahead of checks and review threads, deliberately (backlog 44, origin PR
        // 26): a conflicting branch makes both of those readings moot — CI ran against a
        // diff that is about to be superseded by a rebase, and a review thread answers a
        // version of the code the merge will discard. The observation is GitHub's own
        // mergeable read, never inferred from how long the branch has sat open.
        if (snapshot.IsConflicting)
        {
            session.Events.Append(run.Id, new PullRequestConflictObserved(run.Id, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.Rebase,
                [snapshot.HeadCommit ?? "unknown-head"],
                snapshot,
                "The pull request's branch conflicts with its base branch.",
                now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.HasPendingChecks)
        {
            // The CI picture is incomplete; acting now would hand a follow-up run a
            // partial failure list. The next sweep sees the full result.
            return InspectionOutcome.Inspected;
        }

        if (snapshot.FailingChecks.Count > 0)
        {
            session.Events.Append(run.Id, new PullRequestChecksFailed(run.Id, snapshot.FailingChecks, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.FailingChecks,
                snapshot.FailingChecks,
                snapshot,
                $"CI checks failing on the pull request: {string.Join(", ", snapshot.FailingChecks)}.",
                now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.UnresolvedReviewThreadCount > 0)
        {
            session.Events.Append(run.Id, new ReviewFeedbackReceived(
                run.Id, snapshot.UnresolvedReviewThreadCount, now, snapshot.UnresolvedHumanThreadCount));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.ReviewFeedback,
                snapshot.ThreadIds,
                snapshot,
                DescribeUnresolvedThreads(snapshot),
                now, cancellationToken);
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
        await RerequestReviewAfterFixesAsync(
            session, run, project, fence.Version, task.PullRequestUrl, run.PullRequestNumber.Value,
            snapshot, now, cancellationToken);

        return InspectionOutcome.Inspected;
    }

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
            && snapshot.HasPendingChecks == run.ExternalReviewChecksPending)
        {
            return;
        }

        session.Events.Append(run.Id, new ExternalReviewObserved(
            run.Id, snapshot.CopilotReviewState, snapshot.CopilotReviewThreadCount,
            snapshot.HasPendingChecks, now));
        await session.SaveChangesAsync(cancellationToken);
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
    /// </summary>
    private async Task RerequestReviewAfterFixesAsync(
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
            return;
        }

        IReadOnlyList<PullRequestReviewer> outstanding = ReviewersBehindTheHead(snapshot);
        if (outstanding.Count == 0)
        {
            return;
        }

        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(project.OwnerId, cancellationToken);
        ReviewRerequestPolicy policy = ReviewRerequestPolicy.Resolve(
            project.ReviewRerequest, owner?.ReviewRerequest, _options.DefaultReviewRerequest);
        if (policy != ReviewRerequestPolicy.Enabled)
        {
            return;
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
            return;
        }

        StreamState? runFence = await session.Events.FetchStreamStateAsync(run.Id, cancellationToken);
        RunDetails? fresh = await session.LoadAsync<RunDetails>(run.Id, cancellationToken);
        if (runFence is null || fresh is null || fresh.ReviewRerequestsAfterFixes > 0)
        {
            return;
        }

        int passesSpent = await ReviewRerequestPassesAsync(session, run.TaskId, cancellationToken);
        if (passesSpent >= _options.MaxReviewRerequestsAfterFixes)
        {
            logger.LogInformation(
                "Run {RunId}: review re-request cap reached ({Spent}/{Max}) — {Url} settles on the internal "
                + "review, the thread replies, and CI",
                run.Id, passesSpent, _options.MaxReviewRerequestsAfterFixes, pullRequestUrl);
            return;
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
            return;
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
    /// </summary>
    private async Task CompleteCloseoutAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        TaskAggregate task,
        DateTimeOffset? mergedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RunHandoffRecorded handoff = await ComposeHandoffAsync(session, run, now, cancellationToken);
        session.Events.Append(run.Id, new PullRequestMerged(run.Id, mergedAt, now));
        session.Events.Append(run.Id, handoff);
        session.Events.Append(run.Id, new RunCompleted(run.Id, now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: pull request {Url} merged — closeout complete", run.Id, run.PullRequestUrl);

        await UnblockDependentsAsync(run.TaskId, now, cancellationToken);
        await TellTheCardAsync(run.TaskId, project, task, cancellationToken);
        await RemoveWorktreeBestEffortAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
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
    /// Tell the external work item that the work landed (backlog 18): one comment on the card
    /// carrying the pull request, at the moment the merge is observed.
    /// <para>
    /// A comment and not a transition, deliberately. Which status a merge should move a card to
    /// is one team's workflow rather than a fact about software — "Done" on one board is "Ready
    /// for QA" on the next — and moving somebody's card on the platform's opinion is exactly the
    /// kind of guess this repo refuses to make. A comment is the one Jira write that needs to
    /// know nothing about how the project is configured, which is why it is the one the platform
    /// makes itself. Transitions wait until real usage says which ones matter.
    /// </para>
    /// <para>
    /// Best-effort, and loudly so. The merge is already recorded and the dependents are already
    /// unblocked; a Jira outage must not undo any of that, and it must not be retried blindly
    /// either — a retry loop around an unwatched write is how one card ends up with four
    /// identical comments. So a failure is logged with everything needed to do it by hand and
    /// the closeout carries on.
    /// </para>
    /// <para>
    /// GitHub gets nothing here on purpose: the pull request body already mentions an adopted
    /// issue, so GitHub cross-references the merge on the issue's own timeline without the
    /// platform writing a word (PLAN.md #60). Jira has no such link, which is what makes this
    /// comment the thing that closes the loop rather than a duplicate of one.
    /// </para>
    /// </summary>
    private async Task TellTheCardAsync(
        Guid taskId, ProjectDetails project, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.ExternalReference is not { } reference
            || reference.Provider != WorkItemProvider.Jira
            || task.PullRequestUrl.IsBlank())
        {
            return;
        }

        try
        {
            await using IQuerySession session = store.QuerySession();
            JiraWorkItemProvider? jira = await WorkItemConnections.TryJiraProviderAsync(
                session, cancellationToken, requester: jiraRequester);
            if (jira is null || !JiraIssueKey.TryParseBareKey(reference.Reference, out JiraIssueKey key))
            {
                logger.LogWarning(
                    "Task {TaskId} is linked to {Reference} but this node has no usable Jira connection, "
                    + "so the merge was not commented on the card", taskId, reference);
                return;
            }

            await jira.CommentAsync(key, MergeComment(project, task), cancellationToken);
            logger.LogInformation("Task {TaskId}: told {Reference} that {Url} merged", taskId, reference, task.PullRequestUrl);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not comment the merge of {Url} on {Reference}. Nothing is retried automatically; "
                + "add the note by hand if it matters",
                task.PullRequestUrl, task.ExternalReference);
        }
    }

    /// <summary>
    /// What the card is told. Short, factual, and explicit that nothing else is going to happen
    /// to it — a card that silently gains a comment and never moves reads like an integration
    /// that half worked, and saying so costs one sentence.
    /// </summary>
    private static string MergeComment(ProjectDetails project, TaskAggregate task) =>
        $"""
         The pull request for this work has merged: {task.PullRequestUrl}

         Recorded by Hall9k as task {task.Id} in project {project.Name}. This is a one-off note at
         merge — Hall9k does not change the card's status, because which status a merge means is
         this project's workflow to decide.
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
    /// </summary>
    private static string ObstructionKey(FollowUpKind kind, IReadOnlyList<string> identity) =>
        $"{kind.Value}:{string.Join('␟', identity.OrderBy(id => id, StringComparer.Ordinal))}";

    /// <summary>The human-readable side of <see cref="ObstructionKey"/> — what a park message reads back as the obstruction that repeated.</summary>
    private static string DescribeObstruction(FollowUpKind kind, IReadOnlyList<string> identity) =>
        kind == FollowUpKind.FailingChecks
            ? $"the failing check(s) {string.Join(", ", identity.OrderBy(id => id, StringComparer.Ordinal))}"
            : kind == FollowUpKind.Rebase
                ? "the pull request conflicting with its base branch"
                : $"the same {identity.Count} unresolved review thread(s)";

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
        CancellationToken cancellationToken)
    {
        // The lifetime ceiling is checked first and absolutely: it is the true runaway
        // backstop (Decisions Log #80, backlog 45), and no human engagement bypasses it —
        // only h9k pr resolve does.
        int automaticActionsSpent = await AutomaticActionsSpentAsync(session, task, cancellationToken);
        if (automaticActionsSpent >= _options.MaxAutomaticCloseoutRuns)
        {
            string ceilingParkReason =
                $"{reason} The lifetime automatic closeout budget spent ({automaticActionsSpent}/{_options.MaxAutomaticCloseoutRuns} action(s)) — " +
                $"{DescribeAutomaticLapHistory(task, automaticActionsSpent)}. " +
                "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve.";
            await ParkAsync(session, run, ceilingParkReason, now, cancellationToken);
            return;
        }

        string obstructionKey = ObstructionKey(kind, obstructionIdentity);
        string obstructionSummary = DescribeObstruction(kind, obstructionIdentity);
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
            string parkReason = sameObstruction
                ? $"{reason} The same obstruction — {obstructionSummary} — survived {task.ConsecutiveObstructionLaps} " +
                  $"automatic lap(s) without clearing (cap {_options.MaxCloseoutLapsPerObstruction} per obstruction). " +
                  "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve."
                : $"{reason} This is a new obstruction — {obstructionSummary} — but the cap " +
                  $"{_options.MaxCloseoutLapsPerObstruction} per obstruction leaves no room for even one automatic lap on it. " +
                  "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve.";
            await ParkAsync(session, run, parkReason, now, cancellationToken);
            return;
        }

        // The reopen races the CLI's h9k pr resolve on the fence version captured before
        // the aggregate was read; losing just means someone else already dispatched.
        session.Events.Append(task.Id, expectedVersion: fenceVersion + 1, TaskDecider.Reopen(
            task, run.Id, run.Branch, reason, kind, automatic: true, now, node.OwnerId,
            obstructionKey: obstructionKey,
            obstructionSummary: obstructionSummary,
            knownHumanReviewThreadIds: snapshot.HumanThreadIds,
            knownPendingReviewRequestLogins: snapshot.PendingReviewers));

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
