namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// The whole of the courier's own spawn decision (idea 89471598, piece 3), with no database in
/// it — the daemon's own sweep reads the four facts this needs (the feed, presence, whether a
/// courier is already running, the day's spawn count) and hands them here, so the four
/// conditions the acceptance criteria name are one pure function and its own unit tests rather
/// than an integration test threading a live daemon through a clock.
/// </summary>
public static class CourierGate
{
    public static CourierSpawnDecision Decide(
        bool hasUndrainedItems,
        bool orchestratorLive,
        bool courierAlreadyRunning,
        bool manualDrainLeaseHeld,
        long? newestUrgentSequence,
        CourierFailureStreak? streak,
        DateTimeOffset now,
        TimeSpan? elapsedSinceLastCourier,
        TimeSpan quietFor,
        TimeSpan quietThreshold,
        TimeSpan maxWait,
        int spawnsToday,
        int daySpawnCap)
    {
        if (!hasUndrainedItems)
        {
            return CourierSpawnDecision.NoItems;
        }

        if (!orchestratorLive)
        {
            return CourierSpawnDecision.NoOrchestrator;
        }

        if (courierAlreadyRunning)
        {
            return CourierSpawnDecision.AlreadyRunning;
        }

        // Checked ahead of the day cap and the urgent override too: a human reading the feed by
        // hand right now is the one case that outranks even an urgent item, since the courier
        // would otherwise deliver the identical items a person is already looking at.
        if (manualDrainLeaseHeld)
        {
            return CourierSpawnDecision.ManualDrainInProgress;
        }

        // Checked ahead of the wait and the urgent override, and applied to both: a storm of
        // daemon trouble or park events is exactly the shape this cap exists to guard against,
        // not just an ordinary busy feed.
        if (spawnsToday >= daySpawnCap)
        {
            return CourierSpawnDecision.DayCapReached;
        }

        // A failure streak replaces the ordinary batching wait outright, urgent items included
        // (the two runaways this closes are pinned by CourierGateTests): an urgent item must not
        // respawn a courier on every sweep tick, and the ordinary quiet-based ramp must not carry
        // a stuck project down to a zero wait, because that ramp reads the undelivered item's own
        // age and keeps falling the longer it sits there unchanged. The streak's NextAttemptAt is
        // the whole answer instead: the last failure's completion plus min(base x 2^(n-1), ceiling).
        // A streak of zero (a courier the daemon itself ended) holds the same gate for one base
        // wait, so nothing spawns at once after a shutdown.
        if (streak is { } held && held.NextAttemptAt > now)
        {
            return EarlyAttemptAllowed(held, newestUrgentSequence, elapsedSinceLastCourier, maxWait)
                ? CourierSpawnDecision.Spawn
                : CourierSpawnDecision.Waiting;
        }

        if (streak is { Count: > 0 })
        {
            return CourierSpawnDecision.Spawn;
        }

        // No streak is running: an urgent item bypasses the wait outright, and anything else waits
        // out the ordinary ramp.
        if (newestUrgentSequence is not null)
        {
            return CourierSpawnDecision.Spawn;
        }

        TimeSpan wait = Wait(quietFor, quietThreshold, maxWait);
        if (elapsedSinceLastCourier is null || elapsedSinceLastCourier >= wait)
        {
            return CourierSpawnDecision.Spawn;
        }

        return CourierSpawnDecision.Waiting;
    }

    /// <summary>
    /// The one early attempt a running streak grants: an urgent item no failed courier of the
    /// streak has carried (its sequence is above the highest one they carried) may spawn at any
    /// step of the backoff, once at least the base wait has passed since the last courier
    /// completed. The attempt does not reset the doubling, and the courier it spawns carries the
    /// item, so if it fails the item has used its bypass; a burst of urgent items therefore
    /// spawns at most one courier per base interval.
    /// </summary>
    private static bool EarlyAttemptAllowed(
        CourierFailureStreak streak,
        long? newestUrgentSequence,
        TimeSpan? elapsedSinceLastCourier,
        TimeSpan maxWait) =>
        streak.Count > 0
        && newestUrgentSequence is { } urgent
        && urgent > streak.HighestCarriedSequence
        && (elapsedSinceLastCourier is null || elapsedSinceLastCourier >= CourierBackoffWaits.BaseFor(maxWait));

    /// <summary>
    /// How long the next courier for this project must wait since the last one, given how
    /// recently a new item last arrived (<paramref name="quietFor"/>): zero once the feed has
    /// been quiet for <paramref name="quietThreshold"/> (ten minutes by default), ramping up
    /// linearly to <paramref name="maxWait"/> (the project's own <c>--courier-max-wait</c>, sixty
    /// seconds by default) the more recently something new landed. A quiet feed pays no batching
    /// cost at all; a feed that keeps producing new items pays the full ceiling on every one,
    /// which is what batches them into fewer, larger deliveries.
    /// </summary>
    public static TimeSpan Wait(TimeSpan quietFor, TimeSpan quietThreshold, TimeSpan maxWait)
    {
        if (quietFor >= quietThreshold || quietThreshold <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double fraction = 1 - (quietFor.TotalSeconds / quietThreshold.TotalSeconds);
        return maxWait * Math.Clamp(fraction, 0, 1);
    }
}
