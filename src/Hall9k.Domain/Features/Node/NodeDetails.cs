using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Node;

public sealed class NodeDetails
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>
    /// Mirrors <see cref="NodeAggregate.LaunchHoldActive"/> (task: a session that exits at once
    /// with no work done is treated as the node failing to launch sessions) — read by the
    /// dispatcher's claim gate, the in-place session-error retries, and both CLI surfaces, all of
    /// which read this doc rather than replaying the node stream.
    /// </summary>
    public bool LaunchHoldActive { get; set; }

    public string LaunchHoldCauseText { get; set; } = string.Empty;
    public DateTimeOffset? LaunchHoldRaisedAt { get; set; }
    public DateTimeOffset LaunchHoldLastEventAt { get; set; }
    public int LaunchHoldRunCount { get; set; }
    public int LaunchHoldProbeCount { get; set; }
}

public sealed class NodeDetailsProjection : SingleStreamProjection<NodeDetails, Guid>
{
    public NodeDetails Create(IEvent<NodeRegistered> @event) => new()
    {
        Id = @event.Data.Id,
        OwnerId = @event.Data.OwnerId,
        MachineName = @event.Data.MachineName,
        OperatingSystem = @event.Data.OperatingSystem,
        RegisteredAt = @event.Data.RegisteredAt,
    };

    public void Apply(IEvent<NodeLaunchHoldRaised> @event, NodeDetails view)
    {
        view.LaunchHoldActive = true;
        view.LaunchHoldCauseText = @event.Data.CauseText;
        view.LaunchHoldRaisedAt = @event.Data.RaisedAt;
        view.LaunchHoldLastEventAt = @event.Data.RaisedAt;
        view.LaunchHoldRunCount = 0;
        view.LaunchHoldProbeCount = 0;
    }

    public void Apply(IEvent<NodeLaunchHoldRunHeld> @event, NodeDetails view) => view.LaunchHoldRunCount++;

    public void Apply(IEvent<NodeLaunchHoldProbed> @event, NodeDetails view)
    {
        view.LaunchHoldProbeCount++;
        view.LaunchHoldLastEventAt = @event.Data.ProbedAt;
    }

    public void Apply(IEvent<NodeLaunchHoldCleared> @event, NodeDetails view)
    {
        view.LaunchHoldActive = false;
        view.LaunchHoldCauseText = string.Empty;
        view.LaunchHoldRaisedAt = null;
    }
}
