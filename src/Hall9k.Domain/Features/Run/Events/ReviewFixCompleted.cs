namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The fix session for the given review cycle finished (Decisions Log #23). Fixed (and
/// Unknown, when no resolution was declared) re-enters the loop: gates, then a fresh
/// review. Disputed — the fix run judged a finding not-a-defect or human-territory —
/// parks the run with both positions recorded (review-&lt;cycle&gt;-fix-position.md
/// beside the findings artifact) rather than looping on a judgment call.
/// </summary>
public sealed record ReviewFixCompleted(
    Guid Id,
    int Cycle,
    ReviewFixOutcome Outcome,
    DateTimeOffset CompletedAt);
