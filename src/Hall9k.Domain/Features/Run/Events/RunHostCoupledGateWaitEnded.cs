namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The wait <see cref="RunHostCoupledGateWaitStarted"/> began has ended: the node-wide
/// host-coupled-gate permit was granted, so nothing is waited on for the display to observe until
/// the next <see cref="RunHostCoupledGateWaitStarted"/> (#225).
/// </summary>
public sealed record RunHostCoupledGateWaitEnded(Guid Id, DateTimeOffset EndedAt);
