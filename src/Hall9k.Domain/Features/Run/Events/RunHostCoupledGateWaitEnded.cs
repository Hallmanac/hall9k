namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The wait <see cref="RunHostCoupledGateWaitStarted"/> began has ended: the node-wide
/// host-coupled-gate permit was granted, so nothing is waited on for the display to observe until
/// the next <see cref="RunHostCoupledGateWaitStarted"/> (PLACEHOLDER-609bd344).
/// </summary>
public sealed record RunHostCoupledGateWaitEnded(Guid Id, DateTimeOffset EndedAt);
