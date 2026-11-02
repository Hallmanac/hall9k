using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Plan.Handlers;

/// <summary>What the daemon does next for one draft of a plan.</summary>
public enum PlanStepKind
{
    /// <summary>Nothing: the draft is closed, parked, or ready and waiting on its siblings.</summary>
    None,

    /// <summary>A refinement session is running for it.</summary>
    AwaitSession,

    /// <summary>A challenge has been requested and no outcome has landed.</summary>
    AwaitChallenge,

    /// <summary>Dispatch the draft's refinement session.</summary>
    DispatchRefinement,

    /// <summary>Request the draft's challenge.</summary>
    RequestChallenge,

    /// <summary>A person granted a round after rounds exhausted: move the draft to refinement to answer the verdict.</summary>
    StartNextRound,
}

/// <summary>
/// The plan's decisions, in one place and over the aggregate alone, so what a plan does with a session's outcome or a
/// verdict is testable with no database and no process. The daemon decides here and performs the effects (a session,
/// a child <c>h9k</c>, a save); nothing in <see cref="PlanAggregate"/> decides anything.
/// <para>
/// The loop it implements: a draft starts at refinement (or at challenge). A refinement that finishes sends it to
/// challenge, round one. A verdict of publish as written makes it ready. Publish as revised gets one refinement session
/// told to apply the verdict's changes, and the draft is then ready with no further challenge. Do not publish gets a
/// session told to answer the verdict and then a new challenge, up to the daemon's round cap; a do not publish on the
/// last round parks. A park change in a verdict drives nothing.
/// </para>
/// </summary>
public static class PlanDecider
{
    /// <summary>The next step for one draft, from the plan's record of it alone.</summary>
    public static PlanStepKind NextStep(PlanDraftState draft)
    {
        if (draft.IsClosed || draft.IsParked)
        {
            return PlanStepKind.None;
        }

        if (draft.RunningSessionId is not null)
        {
            return PlanStepKind.AwaitSession;
        }

        if (draft.ChallengeRequestedAt is not null)
        {
            return PlanStepKind.AwaitChallenge;
        }

        if (draft.Phase == PlanPhase.Refinement)
        {
            return PlanStepKind.DispatchRefinement;
        }

        if (draft.Ready)
        {
            return PlanStepKind.None;
        }

        return draft.Outcome == PlanOutcome.DoNotPublish
            ? PlanStepKind.StartNextRound
            : PlanStepKind.RequestChallenge;
    }

    /// <summary>
    /// The drafts the plan's publish step publishes now, in the order they were named: none unless every draft that is
    /// still open is ready and nothing is parked, and none while a session or a challenge is still running for one.
    /// </summary>
    public static IReadOnlyList<Guid> DraftsToPublish(PlanAggregate plan)
    {
        if (!plan.IsLive || plan.IsParked)
        {
            return [];
        }

        PlanDraftState[] open = [.. plan.Drafts.Where(draft => !draft.IsClosed)];
        return open.Length > 0 && open.All(draft => draft.Ready && draft.RunningSessionId is null)
            ? [.. open.Select(draft => draft.DraftId)]
            : [];
    }

    /// <summary>
    /// The event that ends a plan whose drafts are all closed: published when at least one was published, cancelled
    /// when every draft was abandoned. Null while any draft is still open. The drafts the tracker never got an item for
    /// (<see cref="TrackerGap"/>) ride the published event, so the end item can say so.
    /// </summary>
    public static object? End(PlanAggregate plan, DateTimeOffset now, IReadOnlyList<Guid>? withoutTrackerItem = null)
    {
        if (!plan.IsLive || plan.Drafts.Count == 0 || plan.Drafts.Any(draft => !draft.IsClosed))
        {
            return null;
        }

        return plan.Drafts.Any(draft => draft.Published)
            ? new PlanPublished(plan.Id, plan.ProjectId, now, withoutTrackerItem ?? [])
            : new PlanCancelled(plan.Id, plan.ProjectId, "every draft was abandoned", null, now);
    }

    /// <summary>
    /// What a refinement session's report led to, once its revision (if any) was applied. A session that asked a
    /// question parks the plan; one told to apply a verdict that changed nothing parks it too. A session told to answer a
    /// do not publish verdict goes on to a new challenge even when it changed nothing, because it may have answered in the
    /// journal that a must is wrong, and a new challenge reads the journal; the round cap bounds the loop.
    /// </summary>
    public static IReadOnlyList<object> AfterRefinement(
        PlanAggregate plan, PlanDraftState draft, RefinementPurpose purpose, RefinementOutcome outcome, Guid sessionId, DateTimeOffset now)
    {
        if (outcome == RefinementOutcome.Question)
        {
            return [Park(plan, draft.DraftId, PlanParkReason.Question, sessionId, now)];
        }

        bool changed = outcome == RefinementOutcome.Revised;
        if (purpose == RefinementPurpose.ApplyVerdict)
        {
            return changed
                ? [Moved(plan, draft, PlanPhase.Challenge, draft.Round, PlanOutcome.Applied, RefinementPurpose.Unknown, ready: true, sessionId, now)]
                : [Park(plan, draft.DraftId, PlanParkReason.VerdictNotApplied, sessionId, now)];
        }

        PlanOutcome outcomeToRecord = changed
            ? PlanOutcome.Revised
            : PlanOutcome.Unchanged;
        return
        [
            Moved(
                plan, draft, PlanPhase.Challenge, draft.Round + 1, outcomeToRecord, RefinementPurpose.Unknown, ready: false,
                sessionId, now),
        ];
    }

    /// <summary>A refinement session's revision was not applied, or the session failed: the plan parks on the draft.</summary>
    public static PlanParked ParkOn(PlanAggregate plan, Guid draftId, PlanParkReason reason, Guid? sessionId, DateTimeOffset now) =>
        Park(plan, draftId, reason, sessionId, now);

    /// <summary>
    /// What a challenge's recorded outcome led to. A failure parks. A verdict of publish as written makes the draft
    /// ready; publish as revised sends it to one session that applies the changes; do not publish sends it to a session
    /// that answers the verdict, or parks the plan when this was the last round the cap allows. A verdict that would end
    /// the loop (publish as written or as revised) on a draft a person revised while the challenge ran, or after it landed,
    /// judged text that is no longer the draft: the plan parks as draft changed, and a resume challenges the draft as it
    /// stands.
    /// </summary>
    public static IReadOnlyList<object> AfterChallenge(
        PlanAggregate plan, PlanDraftState draft, DraftChallengeRecorded recorded, int roundCap, DateTimeOffset now,
        bool draftChangedSinceJudged = false)
    {
        if (!recorded.IsVerdict)
        {
            return [Park(plan, draft.DraftId, PlanParkReason.ChallengeFailed, recorded.SessionId, now)];
        }

        PlanOutcome outcome = PlanOutcome.FromVerdict(recorded.Verdict);
        if (draftChangedSinceJudged && (outcome == PlanOutcome.PublishAsWritten || outcome == PlanOutcome.PublishAsRevised))
        {
            return
            [
                Moved(plan, draft, PlanPhase.Challenge, draft.Round, outcome, RefinementPurpose.Unknown, ready: false, recorded.SessionId, now),
                Park(plan, draft.DraftId, PlanParkReason.DraftChanged, null, now),
            ];
        }

        if (outcome == PlanOutcome.PublishAsWritten)
        {
            return [Moved(plan, draft, PlanPhase.Challenge, draft.Round, outcome, RefinementPurpose.Unknown, ready: true, recorded.SessionId, now)];
        }

        if (outcome == PlanOutcome.PublishAsRevised)
        {
            return [Moved(plan, draft, PlanPhase.Refinement, draft.Round, outcome, RefinementPurpose.ApplyVerdict, ready: false, recorded.SessionId, now)];
        }

        return draft.Round >= roundCap + draft.ExtraRounds
            ?
            [
                Moved(plan, draft, PlanPhase.Challenge, draft.Round, outcome, RefinementPurpose.Unknown, ready: false, recorded.SessionId, now),
                Park(plan, draft.DraftId, PlanParkReason.RoundsExhausted, recorded.SessionId, now),
            ]
            : [Moved(plan, draft, PlanPhase.Refinement, draft.Round, outcome, RefinementPurpose.AnswerVerdict, ready: false, recorded.SessionId, now)];
    }

    /// <summary>
    /// A draft that is ready, or whose verdict is waiting to be applied, was revised after that was decided, which no
    /// challenge or session has read. It is no longer either: the plan parks on it as draft changed, and a resume sends it
    /// back to a challenge of the text as it now stands instead of publishing it unjudged.
    /// </summary>
    public static IReadOnlyList<object> ParkReadyDraftChanged(PlanAggregate plan, PlanDraftState draft, DateTimeOffset now) =>
    [
        BackToChallenge(plan, draft, now),
        Park(plan, draft.DraftId, PlanParkReason.DraftChanged, null, now),
    ];

    /// <summary>
    /// A draft whose verdict is waiting to be applied, and which a person revised after a park they have since resumed,
    /// goes back to a challenge of the text as it now stands. No second park: the resume was the answer to the first.
    /// </summary>
    public static PlanDraftMoved BackToChallenge(PlanAggregate plan, PlanDraftState draft, DateTimeOffset now) =>
        Moved(plan, draft, PlanPhase.Challenge, draft.Round, draft.Outcome, RefinementPurpose.Unknown, ready: false, null, now);

    /// <summary>The move a granted round makes: the draft goes back to refinement to answer the verdict that exhausted the last one.</summary>
    public static PlanDraftMoved StartNextRound(PlanAggregate plan, PlanDraftState draft, DateTimeOffset now) =>
        Moved(plan, draft, PlanPhase.Refinement, draft.Round, draft.Outcome, RefinementPurpose.AnswerVerdict, ready: false, draft.LatestSessionId, now);

    /// <summary><c>h9k plan resume</c>: answers every park on a live plan. Refused for a plan that is over or has no park.</summary>
    public static PlanResumed Resume(PlanAggregate plan, Guid ownerId, DateTimeOffset now)
    {
        if (!plan.IsLive)
        {
            throw new DomainConflictException($"Plan {plan.Id} is {plan.State.Value}, so there is nothing to resume.");
        }

        return plan.IsParked
            ? new PlanResumed(plan.Id, plan.ProjectId, ownerId, now)
            : throw new DomainConflictException(
                $"Plan {plan.Id} is not parked, so there is nothing to resume. h9k plan show {plan.Id} says what it is doing.");
    }

    /// <summary><c>h9k plan cancel</c>: ends a live plan and leaves its drafts as Drafts. The reason is the person's own words.</summary>
    public static PlanCancelled Cancel(PlanAggregate plan, string reason, Guid ownerId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(
                "A plan is cancelled with a reason, in a sentence: h9k plan cancel <plan> --reason \"<why>\". A blank one is an empty shell variable, not a statement.");
        }

        return plan.IsLive
            ? new PlanCancelled(plan.Id, plan.ProjectId, reason.Trim(), ownerId, now)
            : throw new DomainConflictException($"Plan {plan.Id} is already {plan.State.Value}, so there is nothing to cancel.");
    }

    private static PlanParked Park(PlanAggregate plan, Guid draftId, PlanParkReason reason, Guid? sessionId, DateTimeOffset now) =>
        new(plan.Id, plan.ProjectId, draftId, reason, sessionId, now);

    private static PlanDraftMoved Moved(
        PlanAggregate plan, PlanDraftState draft, PlanPhase phase, int round, PlanOutcome outcome, RefinementPurpose purpose,
        bool ready, Guid? sessionId, DateTimeOffset now) =>
        new(plan.Id, plan.ProjectId, draft.DraftId, phase, round, outcome, purpose, ready, sessionId, now);
}
