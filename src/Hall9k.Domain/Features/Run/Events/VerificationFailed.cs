namespace Hall9k.Domain.Features.Run.Events;

public sealed record VerificationFailed(
    Guid Id,
    IReadOnlyList<string> FailedGates,
    DateTimeOffset FailedAt);
