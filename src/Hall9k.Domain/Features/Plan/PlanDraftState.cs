using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// One draft's standing inside a plan, folded from the plan's events. The one place the per-draft transitions live, so
/// the aggregate the daemon decides over and the document <c>h9k plan show</c> reads cannot drift apart. It holds ids,
/// phases, rounds, and closed outcomes, and no prose.
/// </summary>
public sealed class PlanDraftState
{
    public Guid DraftId { get; set; }

    public PlanPhase Phase { get; set; } = PlanPhase.Refinement;

    /// <summary>The challenge round the draft is on; zero until it first enters challenge.</summary>
    public int Round { get; set; }

    public PlanOutcome Outcome { get; set; } = PlanOutcome.None;

    /// <summary>What the draft's next refinement session is for; Unknown outside the refinement phase.</summary>
    public RefinementPurpose Purpose { get; set; } = RefinementPurpose.Refine;

    /// <summary>Whether the draft is ready to publish: a verdict of publish as written, or a verdict's changes applied.</summary>
    public bool Ready { get; set; }

    /// <summary>The refinement session dispatched for this draft and not yet ended.</summary>
    public Guid? RunningSessionId { get; set; }

    /// <summary>When the plan requested the challenge it is waiting on, or null when none is outstanding.</summary>
    public DateTimeOffset? ChallengeRequestedAt { get; set; }

    /// <summary>Why the plan is parked on this draft, or <see cref="PlanParkReason.Unknown"/>.</summary>
    public PlanParkReason ParkReason { get; set; } = PlanParkReason.Unknown;

    /// <summary>The session whose report the latest outcome or park came from, which <c>h9k plan show</c> derives a path from.</summary>
    public Guid? LatestSessionId { get; set; }

    /// <summary>Rounds a person has granted beyond the daemon's cap, by resuming after rounds exhausted.</summary>
    public int ExtraRounds { get; set; }

    public bool Published { get; set; }

    public bool Abandoned { get; set; }

    /// <summary>Published or abandoned: nothing more is done for it.</summary>
    public bool IsClosed => Published || Abandoned;

    public bool IsParked => ParkReason.IsParked;

    /// <summary>The draft as a plan starts it.</summary>
    public static PlanDraftState Start(Guid draftId, PlanPhase startPhase) => startPhase == PlanPhase.Challenge
        ? new PlanDraftState
        {
            DraftId = draftId,
            Phase = PlanPhase.Challenge,
            Round = 1,
            Purpose = RefinementPurpose.Unknown,
        }
        : new PlanDraftState { DraftId = draftId };

    public void Apply(PlanRefinementDispatched @event)
    {
        RunningSessionId = @event.SessionId;
        LatestSessionId = @event.SessionId;
    }

    public void Apply(PlanChallengeRequested @event) => ChallengeRequestedAt = @event.RequestedAt;

    public void Apply(PlanDraftMoved @event)
    {
        Phase = @event.Phase;
        Round = @event.Round;
        Outcome = @event.Outcome;
        Purpose = @event.Purpose;
        Ready = @event.Ready;
        RunningSessionId = null;
        ChallengeRequestedAt = null;
        LatestSessionId = @event.SessionId ?? LatestSessionId;
    }

    public void Apply(PlanParked @event)
    {
        ParkReason = @event.Reason;
        RunningSessionId = null;
        ChallengeRequestedAt = null;
        LatestSessionId = @event.SessionId ?? LatestSessionId;
        if (@event.Reason == PlanParkReason.SessionFailed)
        {
            Outcome = PlanOutcome.SessionFailed;
        }
        else if (@event.Reason == PlanParkReason.ChallengeFailed)
        {
            Outcome = PlanOutcome.ChallengeFailed;
        }
    }

    /// <summary>A resume answers the park; a draft that parked on rounds exhausted is granted one more round.</summary>
    public void ApplyResume()
    {
        ExtraRounds += ParkReason == PlanParkReason.RoundsExhausted
            ? 1
            : 0;
        ParkReason = PlanParkReason.Unknown;
    }

    /// <summary>Publishing or abandoning by hand, like the plan's own publish, answers a park on the draft.</summary>
    public void Apply(PlanDraftPublished @event)
    {
        Published = true;
        Phase = PlanPhase.Published;
        Ready = false;
        ParkReason = PlanParkReason.Unknown;
        RunningSessionId = null;
        ChallengeRequestedAt = null;
    }

    public void Apply(PlanDraftAbandoned @event)
    {
        Abandoned = true;
        ParkReason = PlanParkReason.Unknown;
        RunningSessionId = null;
        ChallengeRequestedAt = null;
    }
}
