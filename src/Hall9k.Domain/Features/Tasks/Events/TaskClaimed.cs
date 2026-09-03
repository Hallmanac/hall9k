namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A node took responsibility. LeaseGeneration is the fencing token (Decisions Log #7);
/// RunId is minted before the claim so the Task stream carries its run linkage.
/// DependencyOverrideAcknowledged is true only for <see cref="Handlers.TaskDecider.ClaimDeliberately"/>'s
/// own Blocked-entry branch (task 8a56af78-h9k, "a deliberate human kick-off"): a human warned
/// about unmet dependency edges and chose to start anyway, and this is the recorded acknowledgment
/// (PLAN.md's "the platform advises, the human overrides" ruling) — false for every other claim,
/// including a plain node <see cref="Handlers.TaskDecider.Claim"/> and an operator's own
/// <see cref="Handlers.TaskDecider.ClaimInteractively"/>, neither of which can ever be claimed with
/// dependencies still open.
/// </summary>
public sealed record TaskClaimed(
    Guid Id,
    Guid NodeId,
    Guid OwnerId,
    int LeaseGeneration,
    Guid RunId,
    DateTimeOffset ClaimedAt,
    bool DependencyOverrideAcknowledged = false);
