using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Every session one persona's review is made of has finished and its findings are on disk (idea
/// b9b09779, piece 1) — the persona's report is in. Appended once per persona, so
/// <c>h9k task show</c> can say which reports have landed while the rest are still running,
/// rather than only being able to say the whole run is under review.
/// </summary>
public sealed record PrReviewPersonaReported(
    Guid Id,
    ReviewPersona Persona,
    DateTimeOffset ReportedAt);
