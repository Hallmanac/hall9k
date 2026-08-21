namespace Hall9k.Domain.Features.Run;

/// <summary>
/// How one review track ended and at which of its own cycles (Decisions Log #62). Cycle counts
/// are per track: a conformance track that went dormant at cycle 1 stays at 1 however far the
/// adversarial track then runs on its own.
/// </summary>
public sealed record ReviewTrackOutcome(ReviewLens Lens, int Cycle, ReviewSettlement Settlement);
