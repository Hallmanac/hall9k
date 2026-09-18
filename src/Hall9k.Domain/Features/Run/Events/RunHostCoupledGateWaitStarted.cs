namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// This run started waiting on the node-wide host-coupled-gate permit: another run's own
/// host-coupled gate already holds it (task: at most one host-coupled gate runs on a node at a
/// time — PLACEHOLDER-609bd344). Appended only when the wait was real — a permit acquired on the
/// first try never appends this — so a wait reads as this run's own phase in <c>h9k task show</c>
/// rather than being counted against it as a failure. Cleared by
/// <see cref="RunHostCoupledGateWaitEnded"/> once the permit is actually granted.
/// </summary>
public sealed record RunHostCoupledGateWaitStarted(Guid Id, DateTimeOffset StartedAt);
