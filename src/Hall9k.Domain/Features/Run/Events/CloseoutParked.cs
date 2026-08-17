namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor stopped dispatching automatic follow-up runs for this pull
/// request: the bounded retry budget is spent (PLAN.md log #11 spirit — never loop on
/// judgment). The task stays Done and the monitor keeps watching for the merge, but the
/// human owns the next move: fix or merge the PR by hand, close it, or grant another
/// automatic attempt with h9k pr resolve. Surfaces as NeedsHuman in h9k status.
/// </summary>
public sealed record CloseoutParked(
    Guid Id,
    string Reason,
    DateTimeOffset ParkedAt);
