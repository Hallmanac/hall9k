using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// One run's own raw stream, paired with the two document facts (dispatch time and whether it
/// has finished) that <see cref="TaskPassageQuery.Compute"/> needs but the stream's own
/// <see cref="RunDispatched"/> and terminal events would otherwise force a second replay to
/// re-derive — the same lean-projection-plus-raw-stream split <c>RunListItem</c>'s own doc
/// already draws.
/// </summary>
public sealed record RunEventSet(Guid RunId, DateTimeOffset DispatchedAt, DateTimeOffset? FinishedAt, IReadOnlyList<IEvent> Events);

/// <summary>
/// Computes <see cref="TaskPassage"/> from a task's own event stream and every run it has
/// dispatched (task: h9k task show tells a task's passage in time). Deliberately not a
/// projection of its own — the house pattern for "replay a stream into a read model nothing else
/// needs to keep current" is a live static query
/// (<see cref="Run.PeriodSpend"/>, <see cref="Node.NodeLaunchHoldEpisodes"/>,
/// <see cref="Project.Queries.ProjectSettingsHistory"/>), never a persisted multi-stream
/// projection: there is no precedent for one anywhere in this codebase, and a task's own stream
/// plus its handful of runs is cheap enough to replay whole on every read, the same argument
/// <see cref="Node.NodeLaunchHoldEpisodes"/>'s own doc already makes for a node stream.
/// <para>
/// <see cref="ReadAsync"/> is the only half that touches a database; <see cref="Compute"/> is a
/// pure fold over already-fetched events, which is what makes it unit-testable with
/// <c>FakeEvent&lt;T&gt;</c> and nothing else — and what <c>h9k status</c> and <c>h9k project
/// show</c> would call directly once they have their own event lists, without paying for a
/// second network round trip through <see cref="ReadAsync"/>.
/// </para>
/// </summary>
public static class TaskPassageQuery
{
    /// <summary>
    /// Every dispatch that spawns a Claude Code process, across a run's whole life (task: h9k
    /// task show tells a task's passage in time — the session count in the closing summary line).
    /// A resumed process (<see cref="RunResumed"/>) and a session-error retry
    /// (<see cref="RunSessionErrorRetried"/>) both count, alongside every ordinary dispatch this
    /// query already reads for the phases above: each is a distinct process the platform paid to
    /// launch, which is the plain, defensible reading of "how many sessions did this task take"
    /// absent any recorded session identity to count instead (<see cref="TokensRecorded"/> carries
    /// no session id — <c>PeriodSpend</c>'s own doc). <see cref="RunUncommittedWorkRecoveryAttempted"/>
    /// (<c>VerificationRunner</c>'s bounded commit-only agent for meaningful uncommitted files),
    /// <see cref="ReviewVerdictReprompted"/> (<c>claude -p --resume</c> for an unparseable
    /// verdict), and <see cref="ContextSynthesisDispatched"/> (<c>BlockerContextAssembler</c>'s
    /// synthesis over the blocker-review threshold) each spawn a real process too and are counted
    /// alongside the rest (independent pre-PR review, cycle 6, conformance finding — the
    /// enumeration above omitted all three). <see cref="InteractiveSessionStarted"/> is excluded
    /// here on its own, and folded into the loop below instead, because it does not always name a
    /// distinct process: <c>h9k task work</c>/<c>h9k task start</c>'s own interactive claim
    /// appends it for the identical process <see cref="RunDispatched"/> already named
    /// (<see cref="RunDispatched.SessionId"/>), and only a genuine re-attach carries a different
    /// session id (independent pre-PR review, cycle 6, adversarial finding).
    /// </summary>
    private static bool IsSessionDispatch(object data) => data is RunDispatched
        or RunResumed
        or ReviewDispatched
        or ReviewFixDispatched
        or PrReviewConformanceDispatched
        or PreFinalPassRebaseRecoveryDispatched
        or SettlingGateRepairDispatched
        or RunSessionErrorRetried
        or RunUncommittedWorkRecoveryAttempted
        or ReviewVerdictReprompted
        or ContextSynthesisDispatched;

    public static async Task<TaskPassage> ReadAsync(
        IQuerySession session, Guid taskId, TaskType taskType, bool taskConcluded, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(run => run.TaskId == taskId)
            .OrderBy(run => run.DispatchedAt)
            .ToListAsync(cancellationToken);

        IReadOnlyList<IEvent> taskEvents = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);

        List<RunEventSet> runEventSets = [];
        foreach (RunListItem run in runs)
        {
            IReadOnlyList<IEvent> runEvents = await session.Events.FetchStreamAsync(run.Id, token: cancellationToken);
            runEventSets.Add(new RunEventSet(run.Id, run.DispatchedAt, run.FinishedAt, runEvents));
        }

        return Compute(taskEvents, runEventSets, taskType, taskConcluded, now);
    }

    /// <summary>
    /// The pure fold — every timestamp arithmetic rule the passage section documents, worked
    /// once here rather than at each of the several call sites a rendering layer would otherwise
    /// need. See each private helper for the phase it owns. <paramref name="runs"/> is expected
    /// in dispatch order (<see cref="ReadAsync"/>'s own query already orders them), since only the
    /// last one is ever treated as still live — an earlier run was superseded by whichever
    /// follow-up dispatched after it, so a cycle or park it never closed is dropped rather than
    /// reported open or guessed shut (a deliberate simplification: the alternative is inventing a
    /// close time nobody recorded, which AGENTS.md's never-guess rule forbids more firmly than it
    /// forbids an honest undercount).
    /// </summary>
    public static TaskPassage Compute(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunEventSet> runs, TaskType taskType, bool taskConcluded,
        DateTimeOffset now)
    {
        bool lastRunStillLive = runs.Count > 0 && runs[^1].FinishedAt is null;
        List<RunFold> folds = [.. runs.Select((run, index) => FoldRun(run, index == runs.Count - 1, now))];
        Dictionary<Guid, RunFold> foldsByRun = folds.ToDictionary(fold => fold.RunId);

        PassagePhase queued = FoldQueued(taskEvents, now);
        PassagePhase building = FoldBuilding(folds, now);
        TimeSpan gates = folds.Aggregate(TimeSpan.Zero, (total, fold) => total + fold.GatesSum);
        ReviewCyclePassage review = new(
            folds.Sum(fold => fold.ReviewCycleCount),
            folds.Aggregate(TimeSpan.Zero, (total, fold) => total + fold.ReviewElapsed),
            folds.Any(fold => fold.ReviewStillOpen),
            folds.Sum(fold => fold.FixSessionCount),
            folds.Aggregate(TimeSpan.Zero, (total, fold) => total + fold.FixElapsed),
            folds.Any(fold => fold.FixStillOpen));

        List<(DateTimeOffset CompletedAt, Guid RunId, string? PullRequestUrl)> completions = [.. TaskCompletions(taskEvents)];
        List<DateTimeOffset> completedWithPr = [.. completions.Where(c => c.PullRequestUrl.IsNotBlank()).Select(c => c.CompletedAt)];
        PassagePhase delivery = FoldDelivery(completions, foldsByRun);
        // A pr-review task carries no pull request of its own to merge — PrReviewFollowThroughEngine
        // completes it with the reviewed pull request's own URL (a task: h9k task show tells a
        // task's passage in time discrepancy an independent pre-PR review, cycle 1, both lenses,
        // caught), which otherwise reads as an applicable-but-unobserved merge to both folds below
        // and renders "unknown" for a boundary that was never this task's to watch in the first
        // place. NotApplicable says so honestly; the pending-external-review wait a few lines down
        // is this task type's real equivalent of "waiting on the other side".
        PassagePhase mergeWait = taskType == TaskType.PrReview
            ? PassagePhase.NotApplicable
            : FoldMergeWait(completedWithPr, folds, taskConcluded, now);
        PassagePhase claimToMerge = taskType == TaskType.PrReview
            ? PassagePhase.NotApplicable
            : FoldClaimToMerge(taskEvents, folds, taskConcluded, now);

        List<HumanWaitPassage> humanWaits = [];
        AddIfPresent(humanWaits, HumanWaitKind.ReviewPark, SumOpenIntervals(
            folds.SelectMany(fold => fold.ReviewParkIntervals),
            folds.Where(fold => fold.IsLast).Select(fold => fold.OpenReviewParkSince).OfType<DateTimeOffset>(),
            lastRunStillLive, now));
        AddIfPresent(humanWaits, HumanWaitKind.CloseoutPark, SumOpenIntervals(
            folds.SelectMany(fold => fold.CloseoutParkIntervals),
            folds.Where(fold => fold.IsLast).Select(fold => fold.OpenCloseoutParkSince).OfType<DateTimeOffset>(),
            lastRunStillLive, now));
        Guid? lastRunId = folds.Count > 0 ? folds[^1].RunId : null;
        AddIfPresent(humanWaits, HumanWaitKind.Question, FoldQuestions(taskEvents, lastRunId, lastRunStillLive, now));
        if (taskType == TaskType.PrReview)
        {
            AddIfPresent(humanWaits, HumanWaitKind.PendingExternalReview,
                FoldPendingExternalReview(taskEvents, folds, lastRunStillLive, now));
        }

        List<LapKindCount> laps = [.. FoldLaps(taskEvents)];
        int sessions = folds.Aggregate(0, (total, fold) => total + fold.SessionCount);

        return new TaskPassage(queued, building, gates, review, delivery, mergeWait, humanWaits, claimToMerge, laps, sessions);
    }

    private static void AddIfPresent(List<HumanWaitPassage> waits, HumanWaitKind kind, PassagePhase phase)
    {
        if (phase.Applicable)
        {
            waits.Add(new HumanWaitPassage(kind, phase));
        }
    }

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    // --- Queued: TaskAssigned/TaskRequeued/TaskReopened to the next TaskClaimed, excluding time Blocked ---
    //
    // "Blocked" here means only a dependency hold: TaskAssigned's own UnmetDependencies, cleared
    // by TaskDependencyCompleted once RemainingDependencies is empty — the only pair that can ever
    // move TaskDetails between Blocked and Queued for that reason (TaskDetailsProjection's own
    // Apply(TaskDependencyCompleted) refuses to touch state that is not already Blocked, and
    // TaskDependencyFailed/Recovered never change state at all, only the reason shown while
    // Blocked). A stacked task can also hold at Blocked -> Queued on RemoteStackedParentObserved
    // (AwaitsRemoteStackedParent) — deliberately not modelled here: that hold is a fact about the
    // parent pull request's own progress, not a dependency in this task's own BlockedBy list, and
    // reproducing TaskAggregate's full AwaitsRemoteStackedParent logic here would duplicate a rule
    // that already lives in one place. A stacked task's queued figure can therefore include a
    // stacked-parent wait that a plain dependency edge would have excluded.

    private static PassagePhase FoldQueued(IReadOnlyList<IEvent> taskEvents, DateTimeOffset now)
    {
        TimeSpan total = TimeSpan.Zero;
        DateTimeOffset? segmentStart = null;
        DateTimeOffset? blockedSince = null;
        TimeSpan blockedDeduction = TimeSpan.Zero;
        bool everStarted = false;
        // The last-known answer to "does this task still have an unmet dependency", frozen
        // exactly the way TaskDetailsProjection.Apply(TaskDependencyCompleted) freezes
        // UnmetDependencies while the task is not currently Blocked: a dependency completion
        // landing while the task is Claimed (a deliberate --acknowledge-unmet-dependencies
        // claim) or Done never clears it, so a later TaskRequeued/TaskReopened/TaskRetried/
        // TaskHandedBack that reads it can still land Blocked (independent pre-PR review,
        // cycle 3, conformance finding).
        bool dependenciesUnresolved = false;

        // Closes whatever queued/blocked segment is currently open, deducting any still-open
        // blocked window along the way — shared by every event that can end that segment
        // without the task ever being claimed: TaskClaimed, and every terminal-or-departing
        // event below it (independent pre-PR review, cycle 3, adversarial finding: a task
        // unassigned, abandoned, or resolved while still queued left segmentStart open forever).
        void Close(DateTimeOffset at)
        {
            if (segmentStart is not { } start)
            {
                return;
            }

            TimeSpan segment = at - start;
            if (blockedSince is { } stillBlockedSince)
            {
                blockedDeduction += at - stillBlockedSince;
                blockedSince = null;
            }

            total += Clamp(segment - blockedDeduction);
            segmentStart = null;
            blockedDeduction = TimeSpan.Zero;
        }

        foreach (IEvent recorded in taskEvents)
        {
            switch (recorded.Data)
            {
                case TaskAssigned assigned:
                    everStarted = true;
                    segmentStart = assigned.AssignedAt;
                    blockedDeduction = TimeSpan.Zero;
                    dependenciesUnresolved = assigned.UnmetDependencies.Count > 0;
                    blockedSince = dependenciesUnresolved ? assigned.AssignedAt : null;
                    break;
                case TaskRequeued requeued:
                    everStarted = true;
                    segmentStart = requeued.RequeuedAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = dependenciesUnresolved ? requeued.RequeuedAt : null;
                    break;
                case TaskReopened reopened:
                    everStarted = true;
                    segmentStart = reopened.ReopenedAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = dependenciesUnresolved ? reopened.ReopenedAt : null;
                    break;
                case TaskRetried retried:
                    // h9k task retry appends only TaskRetried — no TaskRequeued alongside it — so
                    // a failed task's own wait for the next dispatcher pass would otherwise be
                    // dropped from the queued total entirely (independent pre-PR review, cycle 1,
                    // adversarial lens): both this and TaskHandedBack below carry their own
                    // timestamp for exactly the segment TaskRequeued/TaskReopened already open.
                    everStarted = true;
                    segmentStart = retried.RetriedAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = dependenciesUnresolved ? retried.RetriedAt : null;
                    break;
                case TaskHandedBack handedBack:
                    everStarted = true;
                    segmentStart = handedBack.HandedBackAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = dependenciesUnresolved ? handedBack.HandedBackAt : null;
                    break;
                case TaskDependencyCompleted completed when blockedSince is { } since && completed.RemainingDependencies.Count == 0:
                    blockedDeduction += completed.CompletedAt - since;
                    blockedSince = null;
                    dependenciesUnresolved = false;
                    break;
                case TaskClaimed claimed:
                    Close(claimed.ClaimedAt);
                    break;
                case TaskUnassigned unassigned:
                    Close(unassigned.UnassignedAt);
                    break;
                case TaskInteractiveClaimUnassigned interactiveUnassigned:
                    Close(interactiveUnassigned.UnassignedAt);
                    break;
                case TaskAbandoned abandoned:
                    Close(abandoned.AbandonedAt);
                    break;
                case TaskResolved resolved:
                    Close(resolved.ResolvedAt);
                    break;
                case TaskReturnedToDraft returned:
                    Close(returned.ReturnedAt);
                    break;
            }
        }

        if (!everStarted)
        {
            return PassagePhase.NotApplicable;
        }

        if (segmentStart is not { } openStart)
        {
            return PassagePhase.Closed(total);
        }

        TimeSpan openSegment = now - openStart;
        if (blockedSince is { } openBlockedSince)
        {
            blockedDeduction += now - openBlockedSince;
        }

        return PassagePhase.Open(total + Clamp(openSegment - blockedDeduction));
    }

    // --- Per-run fold: build end, gates, review cycles, fix sessions, review/closeout parks, sessions, PR merged ---

    private sealed class RunFold
    {
        public required Guid RunId { get; init; }
        public required DateTimeOffset DispatchedAt { get; init; }
        public required bool IsLast { get; init; }
        public DateTimeOffset? BuildEnd { get; set; }
        public TimeSpan GatesSum { get; set; }
        public int ReviewCycleCount { get; set; }
        public TimeSpan ReviewElapsed { get; set; }
        public bool ReviewStillOpen { get; set; }
        public int FixSessionCount { get; set; }
        public TimeSpan FixElapsed { get; set; }
        public bool FixStillOpen { get; set; }
        public List<(DateTimeOffset Start, DateTimeOffset End)> ReviewParkIntervals { get; } = [];
        public DateTimeOffset? OpenReviewParkSince { get; set; }
        public List<(DateTimeOffset Start, DateTimeOffset End)> CloseoutParkIntervals { get; } = [];
        public DateTimeOffset? OpenCloseoutParkSince { get; set; }
        public List<DateTimeOffset> ReviewCompletedTimes { get; } = [];
        public List<DateTimeOffset> PrReviewDeliveredTimes { get; } = [];
        public DateTimeOffset? PullRequestMergedAt { get; set; }
        public bool PullRequestClosedWithoutMerge { get; set; }
        public int SessionCount { get; set; }
    }

    private static RunFold FoldRun(RunEventSet run, bool isLast, DateTimeOffset now)
    {
        RunFold fold = new() { RunId = run.RunId, DispatchedAt = run.DispatchedAt, IsLast = isLast };
        Dictionary<int, DateTimeOffset> reviewDispatchedAt = [];
        Dictionary<int, DateTimeOffset> fixDispatchedAt = [];
        DateTimeOffset? reviewParkedSince = null;
        DateTimeOffset? closeoutParkedSince = null;
        Guid? dispatchedSessionId = null;

        foreach (IEvent recorded in run.Events)
        {
            object data = recorded.Data;
            bool sameProcessAsDispatch = data is InteractiveSessionStarted started
                && started.ClaudeSessionId == dispatchedSessionId;
            if (data is RunDispatched runDispatched)
            {
                dispatchedSessionId = runDispatched.SessionId;
            }
            else if (sameProcessAsDispatch)
            {
                // Only the first InteractiveSessionStarted for this run can be the same process
                // RunDispatched already named; a later one (a genuine re-attach) always carries a
                // different session id, so clearing here never hides a real re-attach.
                dispatchedSessionId = null;
            }
            else if (data is InteractiveSessionStarted)
            {
                fold.SessionCount++;
            }

            if (IsSessionDispatch(data))
            {
                fold.SessionCount++;
            }

            switch (data)
            {
                case VerificationPassed passed:
                    fold.GatesSum += SumGateDurations(passed.GateDurations);
                    fold.BuildEnd ??= passed.PassedAt;
                    break;
                case VerificationFailed failed:
                    fold.GatesSum += SumGateDurations(failed.GateDurations);
                    fold.BuildEnd ??= failed.FailedAt;
                    break;
                case ReviewDispatched dispatched:
                    fold.BuildEnd ??= dispatched.DispatchedAt;
                    reviewDispatchedAt.TryAdd(dispatched.Cycle, dispatched.DispatchedAt);
                    break;
                case ReviewCompleted completed:
                    fold.ReviewCompletedTimes.Add(completed.CompletedAt);
                    if (reviewDispatchedAt.Remove(completed.Cycle, out DateTimeOffset dispatchedAt))
                    {
                        fold.ReviewCycleCount++;
                        fold.ReviewElapsed += completed.CompletedAt - dispatchedAt;
                    }

                    break;
                case ReviewFixDispatched fixDispatched:
                    fixDispatchedAt.TryAdd(fixDispatched.Cycle, fixDispatched.DispatchedAt);
                    break;
                case ReviewFixCompleted fixCompleted:
                    if (fixDispatchedAt.Remove(fixCompleted.Cycle, out DateTimeOffset fixStart))
                    {
                        fold.FixSessionCount++;
                        fold.FixElapsed += fixCompleted.CompletedAt - fixStart;
                    }

                    break;
                case ReviewParked parked:
                    // A pr-review task's own run never appends VerificationPassed/Failed or
                    // ReviewDispatched — RunSupervisor.HandleResultAsync routes it to
                    // PrReviewEngine entirely, whether the run is the initial conformance-lens
                    // dispatch or a bare mention follow-up that dispatches no lens session at
                    // all — so ReviewParked is the only boundary either shape ever records
                    // before parking. Closing BuildEnd here too (harmless no-op via ??= on an
                    // ordinary FullPipeline run, whose BuildEnd is already closed well before any
                    // review park) stops that whole session-plus-park window from reading as
                    // "build", which double-counted it against the ReviewPark human wait below
                    // and kept growing "so far" while the run sat parked (independent pre-PR
                    // review, cycle 3, both lenses).
                    fold.BuildEnd ??= parked.ParkedAt;
                    reviewParkedSince ??= parked.ParkedAt;
                    // ReviewPhase.VerdictMissing's own re-prompt-exhausted arm (ReviewEngine.cs)
                    // parks the run without ever appending ReviewCompleted for the cycle it just
                    // dispatched, and never will: nothing further re-dispatches that same cycle.
                    // Left in the dictionary, that dispatch read as "still running" long past this
                    // park and past the eventual merge (isLast && FinishedAt is null stays true
                    // for the whole window), growing "N cycles so far" forever and double-counting
                    // the identical window the ReviewPark human-wait row below already reports in
                    // full (independent pre-PR review, cycle 6, adversarial finding). Clearing here
                    // is a no-op for every other park reason, since a verdict is required before
                    // FixNeeded/MergeReady/Disputed can be decided at all, and ReviewCompleted
                    // already removed that cycle's entry by the time any of those park.
                    reviewDispatchedAt.Clear();
                    // Defensive, matching fixDispatchedAt to the same rule: every reachable park
                    // ahead of a fix dispatch (CappedTrack's own check in the FixNeeded phase)
                    // fires before DispatchFixSessionAsync ever runs, so fixDispatchedAt is always
                    // empty here today — but clearing it is a no-op in that case and the one
                    // guard against the same dangling-dispatch shape ReviewEngine's own fix leg
                    // would otherwise leave this fold to explain incorrectly as still running.
                    fixDispatchedAt.Clear();
                    break;
                case ReviewParkResolved resolved when reviewParkedSince is { } parkedSince:
                    fold.ReviewParkIntervals.Add((parkedSince, resolved.ResolvedAt));
                    reviewParkedSince = null;
                    break;
                case ReviewBoundaryApproved approved when reviewParkedSince is { } parkedSince:
                    fold.ReviewParkIntervals.Add((parkedSince, approved.ApprovedAt));
                    reviewParkedSince = null;
                    break;
                case ReviewHumanFixApplied applied when reviewParkedSince is { } parkedSince:
                    fold.ReviewParkIntervals.Add((parkedSince, applied.AppliedAt));
                    reviewParkedSince = null;
                    break;
                case CloseoutParked closeoutParked:
                    closeoutParkedSince ??= closeoutParked.ParkedAt;
                    break;
                case CloseoutBudgetGranted granted when closeoutParkedSince is { } parkedSince:
                    fold.CloseoutParkIntervals.Add((parkedSince, granted.GrantedAt));
                    closeoutParkedSince = null;
                    break;
                case PrReviewDelivered delivered:
                    fold.PrReviewDeliveredTimes.Add(delivered.ResolvedAt);
                    // ResolvePrReviewAsync's own verdict shape appends only this event — never
                    // ReviewParkResolved/ReviewBoundaryApproved/ReviewHumanFixApplied, which a
                    // pr-review run's own RunSupervisor routing (ReviewParked's own doc, above)
                    // never gives it a path to append either. This wait genuinely ended here, with
                    // a known close time — left unhandled, it would instead fall to the
                    // OpenReviewParkSince capture below, which (correctly, for a wait that really
                    // never closed) reports it as an unresolved "unknown" rather than the exact
                    // duration this event actually records, so the multi-day owner wait a completed
                    // pr-review task actually sat through would print honest-but-wrong instead of
                    // exact (independent pre-PR review, cycle 4, conformance finding).
                    if (reviewParkedSince is { } openSince)
                    {
                        fold.ReviewParkIntervals.Add((openSince, delivered.ResolvedAt));
                        reviewParkedSince = null;
                    }

                    break;
                case PullRequestMerged merged:
                    fold.PullRequestMergedAt = merged.MergedAt ?? merged.ObservedAt;
                    break;
                case PullRequestClosed:
                    // CloseoutEngine.PollOnceAsync's own orphaned-run query permanently excludes
                    // a run once it carries this fact ("that run already recorded the one thing
                    // an inspection here could tell it") — nothing will ever watch this pull
                    // request again, so FoldUntilMerged below treats it the same as a concluded
                    // task rather than counting up a merge wait nothing will ever close.
                    fold.PullRequestClosedWithoutMerge = true;
                    break;
            }
        }

        // Only the newest run, and only while it has not itself ended, can plausibly still have
        // a cycle or fix session genuinely in flight — a run that finished (superseded, failed,
        // or closed) with a dangling dispatch never completed left it superseded along with
        // everything else about that run, not "still running" (independent of whether it happens
        // to be the newest run in this task's list, which every terminal run except the very last
        // one already is by construction).
        if (isLast && run.FinishedAt is null)
        {
            if (reviewDispatchedAt.Count > 0)
            {
                // Only the newest still-dispatched cycle can plausibly still be running; an
                // earlier one left un-completed while a later cycle already dispatched is a
                // stream oddity this fold does not try to explain, so only the max is surfaced.
                DateTimeOffset latestStart = reviewDispatchedAt.Values.Max();
                fold.ReviewElapsed += Clamp(now - latestStart);
                fold.ReviewStillOpen = true;
            }

            if (fixDispatchedAt.Count > 0)
            {
                DateTimeOffset latestStart = fixDispatchedAt.Values.Max();
                fold.FixElapsed += Clamp(now - latestStart);
                fold.FixStillOpen = true;
            }
        }

        // Unlike the still-running cycle/fix check above, a dangling review or closeout park is
        // captured for the last run whether or not that run has itself finished: CompleteMergeAsync
        // appends RunCompleted alongside PullRequestMerged, and RunSuperseded closes a run the
        // owner released/abandoned/handed back out from under an open park, so a genuinely
        // unresolved wait on the last run can have FinishedAt set well before anything ever closed
        // the park. Reading this only under "FinishedAt is null" (as the block above still
        // correctly does for a cycle that could still be running) discarded that wait entirely
        // instead of surfacing it as unknown, the same class the cycle-4 fix already corrected for
        // a pr-review run's PrReviewDelivered path above (independent pre-PR review, cycle 6,
        // conformance finding). SumOpenIntervals below reads run liveness separately (lastRunStillLive)
        // to decide Open (run still going) vs Unknown (run ended, wait never closed).
        if (isLast)
        {
            fold.OpenReviewParkSince = reviewParkedSince;
            fold.OpenCloseoutParkSince = closeoutParkedSince;
        }

        if (fold.BuildEnd is null && run.FinishedAt is { } finishedAt)
        {
            // The run ended (superseded, failed outright, or closed) before it ever reached a
            // verification or a review dispatch — the whole run was spent building, so that is
            // the honest close for this run's own building segment rather than leaving it open
            // on a run nothing further will ever happen to.
            fold.BuildEnd = finishedAt;
        }

        return fold;
    }

    private static TimeSpan SumGateDurations(IReadOnlyList<GateDuration>? gates) =>
        gates is null ? TimeSpan.Zero : gates.Aggregate(TimeSpan.Zero, (total, gate) => total + gate.Duration);

    // --- Building: RunDispatched to the run's own BuildEnd, summed across every run ---

    private static PassagePhase FoldBuilding(IReadOnlyList<RunFold> folds, DateTimeOffset now)
    {
        if (folds.Count == 0)
        {
            return PassagePhase.NotApplicable;
        }

        TimeSpan total = TimeSpan.Zero;
        bool open = false;
        for (int i = 0; i < folds.Count; i++)
        {
            RunFold fold = folds[i];
            if (fold.BuildEnd is { } end)
            {
                total += Clamp(end - fold.DispatchedAt);
                continue;
            }

            if (i == folds.Count - 1)
            {
                // Only the newest run can still be building with no BuildEnd recorded yet.
                total += Clamp(now - fold.DispatchedAt);
                open = true;
                continue;
            }

            // An older run can also have no BuildEnd: DispatchEngine.RequeueExpiredLeasesAsync
            // reclaims a task whose lease expired on another node and deliberately leaves that
            // node's own run alone — no RunSuperseded, no RunFailed — so its FinishedAt (and
            // therefore FoldRun's own BuildEnd fallback) never fires (independent pre-PR review,
            // cycle 1, both lenses). That run's build segment was never observed to close and
            // nothing will ever close it now that a later run has superseded it, so it is dropped
            // here — an honest undercount, the same trade-off Compute's own doc already accepts
            // for a dangling review cycle or park — rather than marking the whole phase open on a
            // run nothing further will ever happen to.
        }

        return open ? PassagePhase.Open(total) : PassagePhase.Closed(total);
    }

    // --- Delivery: the final review verdict before a push, to that push's own TaskCompleted ---

    /// <summary>
    /// One <see cref="TaskCompleted"/> per run, even though a Blocked task's own merge-observation
    /// closeout can append a second one on the identical run id: the daemon's closeout engine
    /// re-appends <c>TaskDecider.Complete</c> for a task a reopen left Blocked behind a still-open
    /// dependency with <c>CurrentRunId</c> still pointing at the run whose pull request it kept
    /// watching ("finalizing the task to Done here... closes that door", that method's own doc),
    /// dated to the moment the merge was <em>observed</em> rather than to the earlier push that
    /// already recorded a <see cref="TaskCompleted"/> for the same run when the pull request first
    /// opened. Left undeduplicated, that second entry made <see cref="FoldDelivery"/> count the
    /// same run's own delivery window twice and made <see cref="FoldMergeWait"/>/
    /// <see cref="FoldClaimToMerge"/> anchor on the observation time — which can trail the actual
    /// merge by as long as the orphan sweep's own poll interval — rather than the push that
    /// actually opened the window being measured, occasionally inverting the sign of both
    /// (independent pre-PR review, cycle 1, both lenses). Keeping the earliest completion per run
    /// id is the honest fix: it is the one that actually recorded the push, and every ordinary
    /// run — one push, one <see cref="TaskCompleted"/> — is untouched by the dedup.
    /// </summary>
    private static IEnumerable<(DateTimeOffset CompletedAt, Guid RunId, string? PullRequestUrl)> TaskCompletions(
        IReadOnlyList<IEvent> taskEvents) => taskEvents
        .Select(recorded => recorded.Data)
        .OfType<TaskCompleted>()
        .Select(completed => (completed.CompletedAt, completed.RunId, completed.PullRequestUrl))
        .GroupBy(completion => completion.RunId)
        .Select(group => group.OrderBy(completion => completion.CompletedAt).First());

    private static PassagePhase FoldDelivery(
        IReadOnlyList<(DateTimeOffset CompletedAt, Guid RunId, string? PullRequestUrl)> completions,
        IReadOnlyDictionary<Guid, RunFold> foldsByRun)
    {
        if (completions.Count == 0)
        {
            return PassagePhase.NotApplicable;
        }

        TimeSpan total = TimeSpan.Zero;
        bool anyContribution = false;

        foreach ((DateTimeOffset completedAt, Guid runId, _) in completions)
        {
            if (!foldsByRun.TryGetValue(runId, out RunFold? fold) || fold.ReviewCompletedTimes.Count == 0)
            {
                continue;
            }

            DateTimeOffset lastVerdict = fold.ReviewCompletedTimes.Max();
            if (lastVerdict <= completedAt)
            {
                total += completedAt - lastVerdict;
                anyContribution = true;
            }
        }

        return anyContribution ? PassagePhase.Closed(total) : PassagePhase.NotApplicable;
    }

    // --- Merge wait: the last TaskCompleted (with a pull request) to PullRequestMerged ---

    private static PassagePhase FoldMergeWait(
        IReadOnlyList<DateTimeOffset> taskCompletedWithPr, IReadOnlyList<RunFold> folds, bool taskConcluded,
        DateTimeOffset now)
    {
        if (taskCompletedWithPr.Count == 0)
        {
            return PassagePhase.NotApplicable;
        }

        return FoldUntilMerged(taskCompletedWithPr.Max(), folds, taskConcluded, now);
    }

    // --- Claim to merge: the first TaskClaimed to PullRequestMerged ---

    private static PassagePhase FoldClaimToMerge(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunFold> folds, bool taskConcluded, DateTimeOffset now)
    {
        List<DateTimeOffset> claims = [.. taskEvents.Select(recorded => recorded.Data).OfType<TaskClaimed>().Select(claimed => claimed.ClaimedAt)];
        if (claims.Count == 0)
        {
            return PassagePhase.NotApplicable;
        }

        return FoldUntilMerged(claims.Min(), folds, taskConcluded, now);
    }

    /// <summary>
    /// The shared close for both <see cref="FoldMergeWait"/> and <see cref="FoldClaimToMerge"/>:
    /// an anchor to whichever run's stream recorded <see cref="PullRequestMerged"/> — there is at
    /// most one across a task's whole run history, since the branch and its one pull request
    /// persist across every follow-up lap (<c>TaskReopened</c>'s own doc). Unknown rather than
    /// open once the task has concluded with no merge ever recorded: nothing further will ever
    /// close this interval, so leaving it "open" would grow forever on a task that is done.
    /// </summary>
    private static PassagePhase FoldUntilMerged(
        DateTimeOffset anchor, IReadOnlyList<RunFold> folds, bool taskConcluded, DateTimeOffset now)
    {
        DateTimeOffset? merged = folds.Select(fold => fold.PullRequestMergedAt).FirstOrDefault(value => value is not null);
        if (merged is { } mergedAt)
        {
            // Clamped like every other closed phase: mergedAt is GitHub's own merge timestamp,
            // not this node's observation time, and the orphan sweep can observe a merge days
            // after it happened (that method's own doc says so); ordinary clock skew can invert
            // the sign even on a normally-watched pre-approved auto-merge. A negative span here
            // would otherwise render as a raw negative-seconds count (independent pre-PR review,
            // cycle 1, both lenses).
            return PassagePhase.Closed(Clamp(mergedAt - anchor));
        }

        // A pull request the closeout monitor observed closed without a merge is permanently
        // outside every sweep that could otherwise still complete this closeout — treated as
        // concluded regardless of the caller's own taskConcluded reading (independent pre-PR
        // review, cycle 3, conformance finding: a Delivered-composed row with a closed,
        // unmerged pull request read taskConcluded false and grew this figure forever).
        bool neverWillMerge = taskConcluded || folds.Any(fold => fold.PullRequestClosedWithoutMerge);
        return neverWillMerge ? PassagePhase.Unknown() : PassagePhase.Open(Clamp(now - anchor));
    }

    // --- Human waits: pairs of start/end markers, FIFO, with the newest only ever open on the newest run ---

    private static PassagePhase SumOpenIntervals(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> closedIntervals,
        IEnumerable<DateTimeOffset> openStarts, bool leaveOpen, DateTimeOffset now)
    {
        List<(DateTimeOffset Start, DateTimeOffset End)> closed = [.. closedIntervals];
        List<DateTimeOffset> open = [.. openStarts];
        if (closed.Count == 0 && open.Count == 0)
        {
            return PassagePhase.NotApplicable;
        }

        // Each interval clamped on its own, not just the running sum: a start and its own close
        // can be recorded by two different clocks (the agent's own node appends the start, the
        // owner's CLI on a different machine appends the close), so one skewed pair going negative
        // must not silently cancel out a real positive one recorded elsewhere in the same list
        // (independent pre-PR review, cycle 6, conformance finding).
        TimeSpan total = closed.Aggregate(TimeSpan.Zero, (sum, interval) => sum + Clamp(interval.End - interval.Start));
        if (open.Count > 0)
        {
            if (leaveOpen)
            {
                DateTimeOffset since = open.Min();
                return PassagePhase.Open(total + Clamp(now - since));
            }

            // A start with nothing left to close it, and nothing further that ever will (the run
            // that raised it has ended): a genuinely unresolved wait. Unknown regardless of
            // whether anything else already closed — an earlier, unrelated wait that did close
            // does not make this one any less unresolved, and reporting Closed(total) here would
            // print a confident, exact figure for a task whose record actually says a later wait
            // was never answered (independent pre-PR review, cycle 6, adversarial finding;
            // PassagePhase's own doc names exactly this case for Unknown()).
            return PassagePhase.Unknown();
        }

        return PassagePhase.Closed(total);
    }

    private static PassagePhase FoldQuestions(
        IReadOnlyList<IEvent> taskEvents, Guid? lastRunId, bool lastRunStillLive, DateTimeOffset now)
    {
        Dictionary<Guid, (DateTimeOffset AskedAt, Guid RunId)> asked = [];
        List<(DateTimeOffset Start, DateTimeOffset End)> answered = [];

        foreach (IEvent recorded in taskEvents)
        {
            switch (recorded.Data)
            {
                case QuestionAsked question:
                    asked[question.QuestionId] = (question.AskedAt, question.RunId);
                    break;
                case AnswerProvided answer
                    when asked.Remove(answer.QuestionId, out (DateTimeOffset AskedAt, Guid RunId) askedEntry):
                    answered.Add((askedEntry.AskedAt, answer.AnsweredAt));
                    break;
            }
        }

        // Only a question the task's own newest run itself asked, and only while that run has
        // not itself ended, can plausibly still be waiting on an answer — the same "newest run,
        // still live" rule FoldRun already applies to a review or closeout park (above). A
        // question a superseded run asked and never got answered (the owner released or retried
        // past it) is dropped here rather than reported as still growing against a later,
        // unrelated live run (independent pre-PR review, cycle 1, adversarial lens).
        IEnumerable<DateTimeOffset> openStarts = lastRunId is { } id
            ? asked.Values.Where(entry => entry.RunId == id).Select(entry => entry.AskedAt)
            : [];

        return SumOpenIntervals(answered, openStarts, lastRunStillLive, now);
    }

    private static PassagePhase FoldPendingExternalReview(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunFold> folds, bool leaveOpen, DateTimeOffset now)
    {
        List<DateTimeOffset> requested = [.. taskEvents
            .Select(recorded => recorded.Data)
            .OfType<PullRequestReviewAssignmentObserved>()
            .Select(observed => observed.RequestedAt ?? observed.ObservedAt)
            .OrderBy(at => at)];
        List<DateTimeOffset> delivered = [.. folds.SelectMany(fold => fold.PrReviewDeliveredTimes)];
        // A withdrawn review request ends the wait exactly as a delivered review does — but
        // only when the recall itself concluded the task (AutoPrReviewEngine.ConcludeOneAsync's
        // own Concluded flag): a recall observed after the run already dispatched is recorded as
        // "the work continues", so the wait's real close is still the PrReviewDelivered that
        // follows it, not this recall (independent pre-PR review, cycle 3, adversarial finding:
        // the recalled-before-dispatch shape read Unknown() for a boundary the stream had
        // actually recorded).
        List<DateTimeOffset> ended = [.. delivered.Concat(taskEvents
            .Select(recorded => recorded.Data)
            .OfType<PullRequestReviewAssignmentRecalled>()
            .Where(recalled => recalled.Concluded)
            .Select(recalled => recalled.ObservedAt))
            .OrderBy(at => at)];

        List<(DateTimeOffset Start, DateTimeOffset End)> closed = [];
        int used = 0;
        foreach (DateTimeOffset endedAt in ended)
        {
            if (used >= requested.Count)
            {
                break;
            }

            closed.Add((requested[used], endedAt));
            used++;
        }

        IEnumerable<DateTimeOffset> openStarts = requested.Skip(used);
        return SumOpenIntervals(closed, openStarts, leaveOpen, now);
    }

    // --- Laps: TaskReopened, grouped by kind (Unknown normalized to ReviewFeedback per FollowUpKind's own doc) ---

    private static IEnumerable<LapKindCount> FoldLaps(IReadOnlyList<IEvent> taskEvents) => taskEvents
        .Select(recorded => recorded.Data)
        .OfType<TaskReopened>()
        .Select(reopened => reopened.Kind ?? FollowUpKind.Unknown)
        .Select(kind => kind == FollowUpKind.Unknown ? FollowUpKind.ReviewFeedback : kind)
        .GroupBy(kind => kind)
        .Select(group => new LapKindCount(group.Key, group.Count()))
        .OrderByDescending(lap => lap.Count);
}
