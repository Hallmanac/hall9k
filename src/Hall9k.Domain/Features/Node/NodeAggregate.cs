namespace Hall9k.Domain.Features.Node;

public sealed class NodeAggregate
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string MachineName { get; private set; } = string.Empty;
    public string OperatingSystem { get; private set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; private set; }

    /// <summary>
    /// Whether this node currently cannot launch working agent sessions (task: a session that
    /// exits at once with no work done is treated as the node failing to launch sessions) — the
    /// dispatcher's own claim gate and every in-place session-error retry read this instead of
    /// their own timer while it stands.
    /// </summary>
    public bool LaunchHoldActive { get; private set; }

    /// <summary>The triggering session's own result text, as <see cref="NodeLaunchHoldRaised"/> recorded it — unchanged for the whole episode.</summary>
    public string LaunchHoldCauseText { get; private set; } = string.Empty;

    public DateTimeOffset? LaunchHoldRaisedAt { get; private set; }

    /// <summary>
    /// When this episode last recorded activity — the raise, or the most recent probe — which is
    /// what the probe's own doubling backoff is measured from.
    /// </summary>
    public DateTimeOffset LaunchHoldLastEventAt { get; private set; }

    /// <summary>Distinct runs this episode has held, for the episode query and the CLI's own line.</summary>
    public int LaunchHoldRunCount { get; private set; }

    /// <summary>Relaunch attempts this episode has spent, for the same two readers.</summary>
    public int LaunchHoldProbeCount { get; private set; }

    public void Apply(NodeRegistered @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        MachineName = @event.MachineName;
        OperatingSystem = @event.OperatingSystem;
        RegisteredAt = @event.RegisteredAt;
    }

    public void Apply(NodeLaunchHoldRaised @event)
    {
        LaunchHoldActive = true;
        LaunchHoldCauseText = @event.CauseText;
        LaunchHoldRaisedAt = @event.RaisedAt;
        LaunchHoldLastEventAt = @event.RaisedAt;
        LaunchHoldRunCount = 0;
        LaunchHoldProbeCount = 0;
    }

    public void Apply(NodeLaunchHoldRunHeld @event) => LaunchHoldRunCount++;

    public void Apply(NodeLaunchHoldProbed @event)
    {
        LaunchHoldProbeCount++;
        LaunchHoldLastEventAt = @event.ProbedAt;
    }

    public void Apply(NodeLaunchHoldCleared @event)
    {
        LaunchHoldActive = false;
        LaunchHoldCauseText = string.Empty;
        LaunchHoldRaisedAt = null;
    }
}
