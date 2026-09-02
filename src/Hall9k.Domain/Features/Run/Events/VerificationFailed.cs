namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// <paramref name="GateDurations"/> (task: gate wall-clock duration is recorded and surfaced) is
/// every gate this pass actually ran, in order, including the one named in
/// <paramref name="FailedGates"/> that stopped the line — gates configured after it never ran and
/// so never appear. Null on any stream written before this field existed — an unobserved
/// duration, never a claimed zero.
/// </summary>
public sealed record VerificationFailed(
    Guid Id,
    IReadOnlyList<string> FailedGates,
    DateTimeOffset FailedAt,
    IReadOnlyList<GateDuration>? GateDurations = null);
