namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A node took responsibility. LeaseGeneration is the fencing token (Decisions Log #7);
/// RunId is minted before the claim so the Task stream carries its run linkage.
/// </summary>
public sealed record TaskClaimed(
    Guid Id,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,
    Guid RunId,
    DateTimeOffset ClaimedAt);
