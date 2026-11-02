using Hall9k.Domain.Features.Plan.Events;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// A plan's discovery, folded from its events: the idea it runs on, the session dispatched for it and not yet ended, and the
/// park it waits in. The one place the transitions live, so the aggregate the daemon decides over and the document
/// <c>h9k plan show</c> reads cannot drift apart. It holds ids and closed reasons, and nothing the session wrote.
/// </summary>
public sealed class PlanDiscoveryState
{
    public Guid IdeaId { get; set; }

    /// <summary>The discovery session dispatched and not yet ended.</summary>
    public Guid? RunningSessionId { get; set; }

    /// <summary>The latest session, whose report <c>h9k plan show</c> derives a path from; null when the latest park had no session.</summary>
    public Guid? LatestSessionId { get; set; }

    /// <summary>Gate or session failed while parked, else <see cref="PlanParkReason.Unknown"/>.</summary>
    public PlanParkReason ParkReason { get; set; } = PlanParkReason.Unknown;

    public bool IsParked => ParkReason.IsParked;

    /// <summary>Whether the report landed and waits for a person to pursue or stop.</summary>
    public bool AtGate => ParkReason == PlanParkReason.Gate;

    public void Apply(PlanDiscoveryDispatched @event)
    {
        RunningSessionId = @event.SessionId;
        LatestSessionId = @event.SessionId;
    }

    public void Apply(PlanDiscoveryParked @event)
    {
        ParkReason = @event.Reason;
        RunningSessionId = null;
        LatestSessionId = @event.SessionId;
    }

    /// <summary>A resume answers a failed session; the gate cannot be resumed, so this clears only what a resume may.</summary>
    public void ApplyResume()
    {
        if (ParkReason == PlanParkReason.SessionFailed)
        {
            ParkReason = PlanParkReason.Unknown;
        }
    }

    /// <summary>The gate was answered by pursuing: nothing is parked or running in discovery any more.</summary>
    public void ApplyCut()
    {
        ParkReason = PlanParkReason.Unknown;
        RunningSessionId = null;
    }
}
