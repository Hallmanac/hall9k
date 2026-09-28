namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The wait <see cref="RunGateSetAcceptanceWaitStarted"/> began has ended: an operator ran
/// <c>h9k project accept-gates</c> and this node now accepts the fingerprint the run captured at
/// entry, so nothing is waited on for the display to observe until the next
/// <see cref="RunGateSetAcceptanceWaitStarted"/>.
/// </summary>
public sealed record RunGateSetAcceptanceWaitEnded(Guid Id, DateTimeOffset EndedAt);
