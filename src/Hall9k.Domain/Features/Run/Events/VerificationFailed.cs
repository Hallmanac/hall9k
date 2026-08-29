namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// <paramref name="Note"/> mirrors <see cref="VerificationPassed.Note"/>: the free-text "why",
/// for a failure a caller cannot express as a gate name alone — a pre-gate check that failed
/// before any gate ran (<paramref name="FailedGates"/> empty) is the case that needs it most,
/// since there is no gate name to carry the reason instead.
/// </summary>
public sealed record VerificationFailed(
    Guid Id,
    IReadOnlyList<string> FailedGates,
    DateTimeOffset FailedAt,
    string? Note = null);
