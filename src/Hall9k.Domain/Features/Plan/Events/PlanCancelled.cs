namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The plan ended without publishing: a person cancelled it (<c>h9k plan cancel</c>, with the reason they gave), or
/// every draft was abandoned (<paramref name="CancelledByOwnerId"/> null). Its drafts stay Drafts, free for another plan.
/// </summary>
public sealed record PlanCancelled(
    Guid Id,
    Guid ProjectId,
    string Reason,
    Guid? CancelledByOwnerId,
    DateTimeOffset CancelledAt);
