namespace Hall9k.Domain.Features.Replication;

/// <summary>What asking for one stream's history came to — an in-process outcome the coordinator
/// returns and a CLI command prints, never persisted (AGENTS.md's own enum rule).</summary>
public enum StreamRequestOutcome
{
    /// <summary>Nothing had ever been asked for this stream here, and a first request is queued now.</summary>
    Queued,

    /// <summary>Every earlier request for this stream is closed — answered, declined, or superseded
    /// — so this call asked again rather than reporting an ask that is no longer in flight.</summary>
    ReAsked,

    /// <summary>An earlier request is genuinely still outstanding, so nothing new was queued.</summary>
    AlreadyOutstanding,

    /// <summary>An outstanding request was closed out as superseded and a fresh one queued in its
    /// place (<c>h9k task pull --again</c>).</summary>
    Superseded,

    /// <summary>Every earlier request is closed, but the most recent one closed inside the caller's
    /// own re-mint cooldown, so nothing was queued. Only an automatic caller passes a cooldown at
    /// all; a human's own ask never cools down.</summary>
    CoolingDown,
}

/// <summary>
/// Whether an ask for one stream queues anything, and what to call what it did — the whole of the
/// re-ask rule, as a pure function over the requests this node already has for that stream, so it
/// is checkable without a database (task 9eb5b245).
/// <para>
/// The rule the 2026-09-19 incident asked for: a CLOSED request is not an ask in flight. Windows
/// queued a broadcast for ec35ceca's stream at 13:40, the Mac declined it at 13:41, and the 23:36
/// re-run was told the old request was still on its way — because the only question anybody asked
/// was "does a request exist", never "is it still outstanding". A closed request re-asks;
/// an outstanding one is reported as outstanding and is cleared only by an explicit
/// <c>--again</c>, which is also the one lever that clears a request minted before v0.10.5, whose
/// decline the inbox of the day never applied to a broadcast at all.
/// </para>
/// </summary>
public static class StreamRequestDecision
{
    /// <summary>
    /// <paramref name="priorForStream"/> is every request this node already holds for the one
    /// (project, stream) pair being asked about, in any order — this reads their own
    /// <see cref="EventCatchUpRequest.IsOutstanding"/> and <see cref="EventCatchUpRequest.SentAt"/>
    /// rather than assuming a sort. <paramref name="reMintCooldown"/> is null for a human's own ask
    /// (which never cools down) and a real span for an automatic one, the identical guard
    /// <c>EventCatchUpCoordinator.RequestGapFillAsync</c> already applies to a gap that nobody holds
    /// the far side of.
    /// </summary>
    public static StreamRequestOutcome Decide(
        IReadOnlyList<EventCatchUpRequest> priorForStream, bool again, TimeSpan? reMintCooldown, DateTimeOffset now)
    {
        if (priorForStream.Any(request => request.IsOutstanding))
        {
            return again ? StreamRequestOutcome.Superseded : StreamRequestOutcome.AlreadyOutstanding;
        }

        if (priorForStream.Count == 0)
        {
            return StreamRequestOutcome.Queued;
        }

        DateTimeOffset mostRecentlySent = priorForStream.Max(request => request.SentAt);
        return reMintCooldown is { } cooldown && now - mostRecentlySent < cooldown
            ? StreamRequestOutcome.CoolingDown
            : StreamRequestOutcome.ReAsked;
    }

    /// <summary>Whether an outcome means an envelope actually went out — the one question every
    /// caller that has to decide whether to persist a fresh request asks of it.</summary>
    public static bool Queues(this StreamRequestOutcome outcome) =>
        outcome is StreamRequestOutcome.Queued or StreamRequestOutcome.ReAsked or StreamRequestOutcome.Superseded;
}
