namespace Hall9k.Domain.Features.Run.Events;

public sealed record VerificationPassed(
    Guid Id,
    DateTimeOffset PassedAt,
    string? Note = null);
