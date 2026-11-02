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

    /// <summary>
    /// Whether <see cref="LatestSessionId"/> is a challenge's session (its report is <c>challenge-report.md</c>) rather than a
    /// refinement session's (<c>refinement-report.md</c>). Tracked beside the id because the latest outcome and the latest
    /// session fall out of step: a refinement session that parks leaves the outcome at the verdict it was answering.
    /// </summary>
    public bool LatestReportIsChallenge { get; set; }

    /// <summary>Rounds a person has granted beyond the daemon's cap, by resuming after rounds exhausted.</summary>
    public int ExtraRounds { get; set; }

    /// <summary>How many challenges the plan has requested for this draft, ever. A feedback step's plan allows exactly one.</summary>
    public int ChallengesRequested { get; set; }

    public bool Published { get; set; }

    /// <summary>Whether a person published the draft by hand (the plan's own publish did not), which a feedback step reads as a person getting there first.</summary>
    public bool PublishedByHand { get; set; }

    public bool Abandoned { get; set; }

    /// <summary>Published or abandoned: nothing more is done for it.</summary>
    public bool IsClosed => Published || Abandoned;

    public bool IsParked => ParkReason.IsParked;

    /// <summary>
    /// Whether a person resumed a park on this draft and nothing has happened to it since. A step that would park the draft
    /// again for what that person already saw (a revision the park was about) moves it on instead: the resume was the answer.
    /// </summary>
    public bool ResumedFromPark { get; set; }

    /// <summary>The draft as a plan starts it.</summary>
    public static PlanDraftState Start(Guid draftId, PlanPhase startPhase, RefinementPurpose? purpose = null) => startPhase == PlanPhase.Challenge
        ? new PlanDraftState
        {
            DraftId = draftId,
            Phase = PlanPhase.Challenge,
            Round = 1,
            Purpose = RefinementPurpose.Unknown,
        }
        : new PlanDraftState { DraftId = draftId, Purpose = purpose ?? RefinementPurpose.Refine };

    public void Apply(PlanRefinementDispatched @event)
    {
        RunningSessionId = @event.SessionId;
        LatestSessionId = @event.SessionId;
        LatestReportIsChallenge = false;
        ResumedFromPark = false;
    }

    public void Apply(PlanChallengeRequested @event)
    {
        ChallengeRequestedAt = @event.RequestedAt;
        ChallengesRequested++;
        ResumedFromPark = false;
    }

    public void Apply(PlanDraftMoved @event)
    {
        Phase = @event.Phase;
        Round = @event.Round;
        Outcome = @event.Outcome;
        Purpose = @event.Purpose;
        Ready = @event.Ready;
        RunningSessionId = null;
        ChallengeRequestedAt = null;
        ResumedFromPark = false;
        if (@event.SessionId is { } movedBy)
        {
            LatestSessionId = movedBy;
            LatestReportIsChallenge = @event.Outcome.IsChallenge;
        }
    }

    public void Apply(PlanParked @event)
    {
        ParkReason = @event.Reason;
        RunningSessionId = null;
        ChallengeRequestedAt = null;
        ResumedFromPark = false;

        // A challenge or a refinement dispatch that was refused before any session ran has no session, so nothing it wrote
        // is anywhere to be read: the earlier session's report is not this park's report, and is not offered as if it were.
        bool challengePark = @event.Reason == PlanParkReason.ChallengeFailed || @event.Reason == PlanParkReason.RoundsExhausted;
        if (@event.SessionId is { } parkedBy)
        {
            LatestSessionId = parkedBy;
            LatestReportIsChallenge = challengePark;
        }
        else if (@event.Reason == PlanParkReason.ChallengeFailed || @event.Reason == PlanParkReason.SessionFailed)
        {
            LatestSessionId = null;
            LatestReportIsChallenge = false;
        }

        Outcome = @event.Reason switch
        {
            _ when @event.Reason == PlanParkReason.SessionFailed => PlanOutcome.SessionFailed,
            _ when @event.Reason == PlanParkReason.ChallengeFailed => PlanOutcome.ChallengeFailed,
            _ when @event.Reason == PlanParkReason.Question => PlanOutcome.Question,
            _ => Outcome,
        };
    }

    /// <summary>A resume answers the park; a draft that parked on rounds exhausted is granted one more round.</summary>
    public void ApplyResume()
    {
        ExtraRounds += ParkReason == PlanParkReason.RoundsExhausted
            ? 1
            : 0;
        ResumedFromPark |= ParkReason.IsParked;
        ParkReason = PlanParkReason.Unknown;
    }

    /// <summary>Publishing or abandoning by hand, like the plan's own publish, answers a park on the draft.</summary>
    public void Apply(PlanDraftPublished @event)
    {
        Published = true;
        PublishedByHand = @event.ByHand;
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
