namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A run its Claude Code account's refusal held past its primary session (<see cref="RunAccountHeld"/>) went back
/// to the stage it was held at, <see cref="ResumedState"/> (the gates for <see cref="RunState.Verifying"/>, the
/// review loop for <see cref="RunState.UnderReview"/>), because <see cref="ConnectionId"/>'s check passed again.
/// The hold gave the run's concurrency slot back; this is what takes it again, so the node's live count covers
/// the resumed gates or review from the moment they start rather than once their own first event lands. A held
/// primary session needs none of this: it resumes by starting its session again, through the ordinary launch
/// events.
/// </summary>
public sealed record RunAccountHoldResumed(
    Guid Id,
    Guid ConnectionId,
    RunState ResumedState,
    DateTimeOffset ResumedAt);
