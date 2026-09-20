namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The whole of what a feed read decides (idea 89471598, piece 2), with no database in it: which
/// candidates the project's own band admits, what each one reads as, and how far the read got.
/// <para>
/// Scoping an event to a project means reading documents, which this cannot do, so the caller
/// supplies that as a delegate — and is asked only about events the filter already admitted, so
/// a sweep pays for what is new rather than for everything that happened. Everything else is
/// here, which is what lets the cursor rules and the level rules be unit tests rather than
/// integration ones.
/// </para>
/// </summary>
public static class OrchestratorFeedSelection
{
    /// <summary>
    /// How old an event must be before a drain may move the cursor past it.
    /// <para>
    /// A global sequence is taken from a Postgres sequence inside the writing transaction and
    /// becomes visible only when that transaction commits, so two transactions can commit in the
    /// opposite order to their numbers: a read landing between those two commits sees the higher
    /// number and not the lower one. A cursor parked on the higher number would then never show
    /// the lower event — a park, or a person's message, silently dropped from the feed for good.
    /// Marten's own async daemon guards the same hazard with a stale-sequence threshold; this is
    /// the feed's.
    /// </para>
    /// <para>
    /// Ten seconds because a write here is one <c>SaveChangesAsync</c> lasting milliseconds, and
    /// the frontier is only unsafe if some transaction holds its sequence open for a meaningful
    /// share of this window — three orders of magnitude out from anything this platform writes.
    /// The two costs are asymmetric, which is what decides the direction to err in: a window too
    /// short loses an item permanently, while a window too long only shows the newest few seconds
    /// of items again on the next read, which a plain read is already built to do.
    /// </para>
    /// </summary>
    public static readonly TimeSpan SettlingWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The items <paramref name="candidates"/> yields for a project reading at
    /// <paramref name="level"/>, in the order they were handed in.
    /// </summary>
    /// <param name="startedFrom">
    /// Where the cursor stood before this read. The floor for
    /// <see cref="OrchestratorFeedRead.DrainableThroughSequence"/>, so a read that inspects
    /// nothing at all still reports the cursor rather than zero — which a drain would otherwise
    /// take as an instruction to rewind.
    /// </param>
    /// <param name="settledThrough">
    /// The instant before which an event counts as settled, which is
    /// <see cref="SettlingWindow"/> back from now. The drain frontier stops at the last candidate
    /// stamped at or before it: everything newer is still printed, and simply stays undrained
    /// until the next read, by which time nothing lower can still be in flight behind it.
    /// </param>
    /// <param name="scopeOf">
    /// Which project and task an admitted candidate belongs to, or null when it belongs to no
    /// project this reader can see. Called once per admitted candidate, never for a rejected one.
    /// </param>
    public static async Task<OrchestratorFeedRead> SelectAsync(
        IReadOnlyList<OrchestratorFeedCandidate> candidates,
        Guid projectId,
        OrchestratorFeedLevel level,
        long startedFrom,
        DateTimeOffset settledThrough,
        bool scanWasCapped,
        Func<OrchestratorFeedCandidate, CancellationToken, ValueTask<OrchestratorFeedScope?>> scopeOf,
        CancellationToken cancellationToken)
    {
        List<OrchestratorFeedItem> items = [];
        long drainableThrough = startedFrom;
        bool stillSettled = true;

        foreach (OrchestratorFeedCandidate candidate in candidates)
        {
            // Before any admission test: the cursor advances past everything settled that this
            // read looked at, so a long run of events nobody is interested in is settled once
            // rather than re-read on every sweep for the life of the project. The frontier stops
            // at the first candidate too new to be sure of rather than skipping over it, because
            // an event that is still invisible sits between sequences, not after them.
            if (stillSettled && candidate.At <= settledThrough)
            {
                drainableThrough = Math.Max(drainableThrough, candidate.Sequence);
            }
            else
            {
                stillSettled = false;
            }

            if (!OrchestratorFeedInterest.Admits(candidate.EventType, candidate.Data, level))
            {
                continue;
            }

            if (OrchestratorFeedDescription.Of(candidate.Data) is not { } description)
            {
                // The interest table named this type and the description table did not — a gap
                // between two tables a unit test already fails the build over. Skipping is the
                // fail-soft half: a line reading as a bare type name would be worse than no line.
                continue;
            }

            if (await scopeOf(candidate, cancellationToken) is not { } scope || scope.ProjectId != projectId)
            {
                continue;
            }

            items.Add(new OrchestratorFeedItem(
                candidate.Sequence, candidate.At, scope.TaskId, description,
                OrchestratorFeedUrgency.IsUrgent(candidate.EventType, candidate.Data)));
        }

        return new OrchestratorFeedRead(items, drainableThrough, scanWasCapped);
    }
}
