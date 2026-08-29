namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The agent's claude process emitted its final result event and exited. The run is not
/// done — verification gates run next. Detected from the stream file, never the exit code.
/// <para>
/// <see cref="DeliveredByNodeId"/> is null for this event's ordinary headless meaning (the
/// run already carries its dispatching node's id from <c>RunDispatched</c>, so there is
/// nothing to reassign). <c>h9k task deliver</c> hands it the delivering node's own id
/// instead: an interactive claim's run carries the <c>Guid.Empty</c> sentinel NodeId until
/// this moment (deliberately, so <c>NodeLoad</c> never counts a claim an operator merely
/// holds), and without a real node id from here on the daemon-driven pipeline this event
/// hands the run into — gates, review, fix sessions — stays invisible to every node's own
/// session ceiling forever, which is exactly the double-booking risk the ceiling exists to
/// prevent (adversarial review, cycle 1). Once delivered the run must count like any
/// headless one.
/// </para>
/// </summary>
public sealed record AgentSessionCompleted(
    Guid Id,
    DateTimeOffset CompletedAt,
    Guid? DeliveredByNodeId = null);
