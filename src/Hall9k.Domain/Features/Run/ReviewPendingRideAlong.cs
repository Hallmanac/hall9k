namespace Hall9k.Domain.Features.Run;

/// <summary>
/// A ride-along finding (Decisions Log #87) not yet folded into any fix session the run has
/// dispatched — the run's own working list of what a future fix session, on the same track,
/// still owes a look at. <see cref="Count"/> is how many the pass recorded, not which ones:
/// findings are artifacts, not event payload (log #6), so nothing past the classification and
/// the count travels on the stream — the text a fix session needs is re-read from that cycle's
/// own lens findings file when one actually gets folded in.
/// </summary>
public sealed record ReviewPendingRideAlong(ReviewLens Lens, int Cycle, int Count);
