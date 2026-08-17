namespace Hall9k.Domain.Features.Tasks.Events;

public sealed record TaskRequeued(
    Guid Id,
    RequeueReason Reason,
    DateTimeOffset RequeuedAt);
