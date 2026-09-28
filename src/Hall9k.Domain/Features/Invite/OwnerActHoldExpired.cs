namespace Hall9k.Domain.Features.Invite;

/// <summary>A held owner-act write's own wait lapsed with nobody approving it - the identical
/// 10-minute window an unheld request answers <c>expired</c> against, re-checked on every later
/// sweep tick for as long as a hold stays neither approved nor expired.</summary>
public sealed record OwnerActHoldExpired(Guid Id, DateTimeOffset ExpiredAt);
