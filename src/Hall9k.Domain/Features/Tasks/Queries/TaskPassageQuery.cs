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
    /// no session id — <c>PeriodSpend</c>'s own doc).
    /// </summary>
    private static bool IsSessionDispatch(object data) => data is RunDispatched
        or RunResumed
        or InteractiveSessionStarted
        or ReviewDispatched
        or ReviewFixDispatched
        or PrReviewConformanceDispatched
        or PreFinalPassRebaseRecoveryDispatched
        or SettlingGateRepairDispatched
        or RunSessionErrorRetried;

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
        PassagePhase mergeWait = FoldMergeWait(completedWithPr, folds, taskConcluded, now);
        PassagePhase claimToMerge = FoldClaimToMerge(taskEvents, folds, taskConcluded, now);

        List<HumanWaitPassage> humanWaits = [];
        AddIfPresent(humanWaits, HumanWaitKind.ReviewPark, SumOpenIntervals(
            folds.SelectMany(fold => fold.ReviewParkIntervals),
            folds.Where(fold => fold.IsLast).Select(fold => fold.OpenReviewParkSince).OfType<DateTimeOffset>(),
            lastRunStillLive, now));
        AddIfPresent(humanWaits, HumanWaitKind.CloseoutPark, SumOpenIntervals(
            folds.SelectMany(fold => fold.CloseoutParkIntervals),
            folds.Where(fold => fold.IsLast).Select(fold => fold.OpenCloseoutParkSince).OfType<DateTimeOffset>(),
            lastRunStillLive, now));
        AddIfPresent(humanWaits, HumanWaitKind.Question, FoldQuestions(taskEvents, lastRunStillLive, now));
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

        foreach (IEvent recorded in taskEvents)
        {
            switch (recorded.Data)
            {
                case TaskAssigned assigned:
                    everStarted = true;
                    segmentStart = assigned.AssignedAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = assigned.UnmetDependencies.Count > 0 ? assigned.AssignedAt : null;
                    break;
                case TaskRequeued requeued:
                    everStarted = true;
                    segmentStart = requeued.RequeuedAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = null;
                    break;
                case TaskReopened reopened:
                    everStarted = true;
                    segmentStart = reopened.ReopenedAt;
                    blockedDeduction = TimeSpan.Zero;
                    blockedSince = null;
                    break;
                case TaskDependencyCompleted completed when blockedSince is { } since && completed.RemainingDependencies.Count == 0:
                    blockedDeduction += completed.CompletedAt - since;
                    blockedSince = null;
                    break;
                case TaskClaimed claimed when segmentStart is { } start:
                    TimeSpan segment = claimed.ClaimedAt - start;
                    if (blockedSince is { } stillBlockedSince)
                    {
                        blockedDeduction += claimed.ClaimedAt - stillBlockedSince;
                        blockedSince = null;
                    }

                    total += Clamp(segment - blockedDeduction);
                    segmentStart = null;
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
        public int SessionCount { get; set; }
    }

    private static RunFold FoldRun(RunEventSet run, bool isLast, DateTimeOffset now)
    {
        RunFold fold = new() { RunId = run.RunId, DispatchedAt = run.DispatchedAt, IsLast = isLast };
        Dictionary<int, DateTimeOffset> reviewDispatchedAt = [];
        Dictionary<int, DateTimeOffset> fixDispatchedAt = [];
        DateTimeOffset? reviewParkedSince = null;
        DateTimeOffset? closeoutParkedSince = null;

        foreach (IEvent recorded in run.Events)
        {
            object data = recorded.Data;
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
                    reviewParkedSince ??= parked.ParkedAt;
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
                    break;
                case PullRequestMerged merged:
                    fold.PullRequestMergedAt = merged.MergedAt ?? merged.ObservedAt;
                    break;
            }
        }

        // Only the newest run, and only while it has not itself ended, can plausibly still have
        // a cycle, fix session, or park genuinely in flight — a run that finished (superseded,
        // failed, or closed) with a dangling dispatch never completed left it superseded along
        // with everything else about that run, not "still running" (independent of whether it
        // happens to be the newest run in this task's list, which every terminal run except the
        // very last one already is by construction).
        if (isLast && run.FinishedAt is null)
        {
            if (reviewDispatchedAt.Count > 0)
            {
                // Only the newest still-dispatched cycle can plausibly still be running; an
                // earlier one left un-completed while a later cycle already dispatched is a
                // stream oddity this fold does not try to explain, so only the max is surfaced.
                DateTimeOffset latestStart = reviewDispatchedAt.Values.Max();
                fold.ReviewElapsed += now - latestStart;
                fold.ReviewStillOpen = true;
            }

            if (fixDispatchedAt.Count > 0)
            {
                DateTimeOffset latestStart = fixDispatchedAt.Values.Max();
                fold.FixElapsed += now - latestStart;
                fold.FixStillOpen = true;
            }

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
        foreach (RunFold fold in folds)
        {
            if (fold.BuildEnd is { } end)
            {
                total += Clamp(end - fold.DispatchedAt);
                continue;
            }

            // Only the newest run can still be building with no BuildEnd recorded yet — an
            // older run with none is impossible by construction (FoldRun's own fallback sets
            // BuildEnd to the run's FinishedAt the moment the run ends).
            return PassagePhase.Open(total + Clamp(now - fold.DispatchedAt));
        }

        return PassagePhase.Closed(total);
    }

    // --- Delivery: the final review verdict before a push, to that push's own TaskCompleted ---

    private static IEnumerable<(DateTimeOffset CompletedAt, Guid RunId, string? PullRequestUrl)> TaskCompletions(
        IReadOnlyList<IEvent> taskEvents) => taskEvents
        .Select(recorded => recorded.Data)
        .OfType<TaskCompleted>()
        .Select(completed => (completed.CompletedAt, completed.RunId, completed.PullRequestUrl));

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
            return PassagePhase.Closed(mergedAt - anchor);
        }

        return taskConcluded ? PassagePhase.Unknown() : PassagePhase.Open(now - anchor);
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

        TimeSpan total = closed.Aggregate(TimeSpan.Zero, (sum, interval) => sum + (interval.End - interval.Start));
        if (open.Count > 0 && leaveOpen)
        {
            DateTimeOffset since = open.Min();
            return PassagePhase.Open(total + (now - since));
        }

        return PassagePhase.Closed(total);
    }

    private static PassagePhase FoldQuestions(IReadOnlyList<IEvent> taskEvents, bool leaveOpen, DateTimeOffset now)
    {
        Dictionary<Guid, DateTimeOffset> asked = [];
        List<(DateTimeOffset Start, DateTimeOffset End)> answered = [];

        foreach (IEvent recorded in taskEvents)
        {
            switch (recorded.Data)
            {
                case QuestionAsked question:
                    asked[question.QuestionId] = question.AskedAt;
                    break;
                case AnswerProvided answer when asked.Remove(answer.QuestionId, out DateTimeOffset askedAt):
                    answered.Add((askedAt, answer.AnsweredAt));
                    break;
            }
        }

        return SumOpenIntervals(answered, asked.Values, leaveOpen, now);
    }

    private static PassagePhase FoldPendingExternalReview(
        IReadOnlyList<IEvent> taskEvents, IReadOnlyList<RunFold> folds, bool leaveOpen, DateTimeOffset now)
    {
        List<DateTimeOffset> requested = [.. taskEvents
            .Select(recorded => recorded.Data)
            .OfType<PullRequestReviewAssignmentObserved>()
            .Select(observed => observed.RequestedAt ?? observed.ObservedAt)
            .OrderBy(at => at)];
        List<DateTimeOffset> delivered = [.. folds.SelectMany(fold => fold.PrReviewDeliveredTimes).OrderBy(at => at)];

        List<(DateTimeOffset Start, DateTimeOffset End)> closed = [];
        int used = 0;
        foreach (DateTimeOffset deliveredAt in delivered)
        {
            if (used >= requested.Count)
            {
                break;
            }

            closed.Add((requested[used], deliveredAt));
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
