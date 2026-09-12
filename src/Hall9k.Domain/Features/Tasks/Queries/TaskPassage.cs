namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// Which side of the platform a recorded wait belongs to (idea cc9b7aec's KPI proposal, "one
/// clock, split by who was waiting"). Unpersisted and computed fresh on every read, so a plain
/// enum is the right vocabulary (AGENTS.md §"Value objects over primitives and enums") rather
/// than a closed-vocabulary value object.
/// </summary>
public enum HumanWaitKind
{
    /// <summary>ReviewParked to whichever of ReviewParkResolved/ReviewBoundaryApproved/ReviewHumanFixApplied closed it first.</summary>
    ReviewPark,
    /// <summary>CloseoutParked to CloseoutBudgetGranted.</summary>
    CloseoutPark,
    /// <summary>QuestionAsked to AnswerProvided, paired by QuestionId.</summary>
    Question,
    /// <summary>
    /// A pr-review task's own external ask and answer: PullRequestReviewAssignmentObserved (the
    /// GitHub reviewer request this task exists to answer) to PrReviewDelivered (the verdict
    /// handed back). Only ever populated for <see cref="TaskType.PrReview"/> — an ordinary task's
    /// own review-request wait is already the review-cycle and merge-wait rows above it.
    /// </summary>
    PendingExternalReview,
}

/// <summary>
/// One phase's elapsed time, honest about three different reasons a number might be missing
/// (task: h9k task show tells a task's passage in time). <see cref="Applicable"/> false means
/// the phase's own precondition never happened at all (a task never assigned has no queued
/// phase) — the caller omits the row entirely rather than printing a zero. <see cref="Applicable"/>
/// true with a null <see cref="Elapsed"/> means the phase's own boundary event could not be
/// found even though the phase plainly happened — an older stream missing a field, or a
/// genuinely unresolved wait on a run that will never resolve it — rendered as "unknown", never
/// as zero (task 5243fbbb's own rule, <c>TaskShowCommand.FormatGateDurations</c>'s null-vs-empty
/// distinction, <c>TaskStatusComposer.ChecksPendingClause</c>'s doc). <see cref="StillOpen"/>
/// true means the phase has not closed yet — <see cref="Elapsed"/> is what has accumulated so
/// far and will keep growing the next time this is read.
/// </summary>
public readonly record struct PassagePhase(bool Applicable, TimeSpan? Elapsed, bool StillOpen)
{
    public static readonly PassagePhase NotApplicable = new(false, null, false);

    public static PassagePhase Unknown() => new(true, null, false);

    public static PassagePhase Closed(TimeSpan elapsed) => new(true, elapsed, false);

    public static PassagePhase Open(TimeSpan elapsedSoFar) => new(true, elapsedSoFar, true);

    /// <summary>True only when the boundary that would close this phase could not be found at all — never true for a phase that simply never applied.</summary>
    public bool IsUnknown => Applicable && Elapsed is null;
}

/// <summary>One <see cref="FollowUpKind"/>'s share of this task's lifetime <c>TaskReopened</c> count.</summary>
public sealed record LapKindCount(FollowUpKind Kind, int Count);

/// <summary>
/// The review loop's own totals across every run this task has had (task: h9k task show tells a
/// task's passage in time): every cycle from <c>ReviewDispatched</c> to <c>ReviewCompleted</c>,
/// and every fix session from <c>ReviewFixDispatched</c> to <c>ReviewFixCompleted</c>, summed
/// rather than shown per cycle — the runs table already names which run each lap happened on.
/// <see cref="StillOpen"/>/<see cref="FixStillOpen"/> mark whichever of the two is presently
/// mid-flight on the task's newest run; both are never true at once.
/// </summary>
public sealed record ReviewCyclePassage(
    int Cycles, TimeSpan Elapsed, bool StillOpen, int FixSessions, TimeSpan FixElapsed, bool FixStillOpen);

/// <summary>One kind of human wait, summed across every occurrence this task's streams recorded.</summary>
public sealed record HumanWaitPassage(HumanWaitKind Kind, PassagePhase Elapsed);

/// <summary>
/// A task's whole passage in time, computed once from its own stream and every run it has ever
/// dispatched (task: h9k task show tells a task's passage in time) — how long it queued, built,
/// sat in gates, cycled through review, waited on a human, and waited for its merge, plus the
/// lap, cycle, and session counts. Every figure whose own boundary event can go missing — every
/// field here except <see cref="Gates"/> and <see cref="Review"/>'s own — is
/// <see cref="PassagePhase"/> rather than a bare <see cref="TimeSpan"/>, so a reader can never
/// mistake "never happened", "still running", and "happened, but this stream does not say how
/// long it took" for the same zero. <see cref="Gates"/> and <see cref="ReviewCyclePassage"/>'s own
/// fields stay bare <see cref="TimeSpan"/>s because their own fold never has an unobserved case to
/// report: <c>TaskPassageQuery.SumGateDurations</c> maps a null <c>VerificationPassed.GateDurations</c>
/// to zero honestly, the same "unobserved reads as zero" a run predating that field already
/// carries on <c>RunListItem.GateDurations</c> itself, and a review cycle or fix session either
/// completed (its own duration is exact) or is still running (folded into
/// <see cref="ReviewCyclePassage.StillOpen"/>/<see cref="ReviewCyclePassage.FixStillOpen"/>) —
/// there is no third, silently-missing case for either to hide behind a bare zero.
/// </summary>
public sealed record TaskPassage(
    PassagePhase Queued,
    PassagePhase Building,
    TimeSpan Gates,
    ReviewCyclePassage Review,
    PassagePhase Delivery,
    PassagePhase MergeWait,
    IReadOnlyList<HumanWaitPassage> HumanWaits,
    PassagePhase ClaimToMerge,
    IReadOnlyList<LapKindCount> Laps,
    int Sessions);
