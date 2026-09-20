namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A follow-on pr-review session finished; its findings are on disk. Pairs with
/// <see cref="PrReviewConformanceDispatched"/>, and carries the same <paramref name="Slug"/> for
/// the same reason — see that event's own doc for why the pair keeps its conformance-era name.
/// </summary>
/// <param name="Slug">Which session finished; blank reads as the engineer's conformance lens, the only one an older stream ever recorded.</param>
public sealed record PrReviewConformanceCompleted(
    Guid Id,
    Guid SessionId,
    DateTimeOffset CompletedAt,
    string Slug = "");
