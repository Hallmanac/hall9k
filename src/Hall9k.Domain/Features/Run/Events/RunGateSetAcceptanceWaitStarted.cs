namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// This run started waiting because the fingerprint of the gates it captured at entry does not
/// match what this node has accepted for the project (security review idea 6be68ee2,
/// process-injection finding 1, the local half; <see cref="Hall9k.Domain.Features.Project.GateSetAcceptance"/>
/// is the decision behind it). The same shape <see cref="RunHostCoupledGateWaitStarted"/> already
/// gives the host-coupled-gate permit wait: appended only when the wait is real, so it reads as
/// this run's own phase in <c>h9k task show</c> — and, unlike that wait, also as needs-you, since
/// nothing on this node resolves it but an operator running <c>h9k project accept-gates</c>.
/// Cleared by <see cref="RunGateSetAcceptanceWaitEnded"/> once the set is accepted.
/// </summary>
public sealed record RunGateSetAcceptanceWaitStarted(Guid Id, DateTimeOffset StartedAt);
