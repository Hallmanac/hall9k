namespace Hall9k.Domain.Features.Invite;

/// <summary>A held owner-act write's own approval (<c>h9k project member approve</c>) - the write
/// already landed by the time this is appended; <see cref="CommitId"/> names it.</summary>
public sealed record OwnerActApproved(Guid Id, string? CommitId, DateTimeOffset ApprovedAt);
