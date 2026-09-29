namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// The pre-flight's own process identity (idea 6be68ee2, finding 1, phase one), the identical
/// shape <c>RunProcessStarted</c> carries for an ordinary run — recorded so a daemon restart mid-
/// pre-flight can tell a genuinely still-running process apart from one that died with the
/// previous process, the same liveness check <c>OrchestratorLiveness</c> already gives a courier.
/// </summary>
public sealed record PrReviewPreflightProcessStarted(
    Guid Id,
    int ProcessId,
    DateTimeOffset ProcessStartedAt);
