namespace Hall9k.Domain.Features.Run.Documents;

/// <summary>
/// Mutable telemetry, NOT an event stream and NOT a projection (Decisions Log #11):
/// the daemon upserts this while tailing stream.jsonl. Id == RunId.
/// </summary>
public sealed class RunActivity
{
    public Guid Id { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public long StreamBytesRead { get; set; }

    /// <summary>
    /// Whether the tail has already found a result line at or before <see cref="StreamBytesRead"/>
    /// — persisted alongside the cursor rather than kept only in <c>RunSupervisor.MonitorAsync</c>'s
    /// own local (independent pre-PR review, cycle 3, adversarial lens): the cursor advances past
    /// a result line on the very same poll that finds it, so a daemon restart between that save
    /// and the session's own process later dying would otherwise resume tailing from a cursor
    /// already past the line, with the in-memory flag reset to false and nothing left unread to
    /// set it again — reading a completed session as one that never reported anything.
    /// </summary>
    public bool SawResult { get; set; }

    /// <summary>
    /// When the tail first found a result line, persisted alongside <see cref="SawResult"/> for
    /// the same reason: a daemon restart or orphan adoption between that poll and the session's
    /// own process later dying must resume <c>RunSupervisor.MonitorAsync</c>'s
    /// <c>SessionResultWaiter.PostResultGrace</c> clock from when the result was actually seen,
    /// not from the moment the restarted daemon happens to resume monitoring — an in-memory-only
    /// clock restarts the grace window from "now" on every adoption, silently widening the bound
    /// this field exists to keep exact (independent pre-PR review, cycle 4, adversarial lens).
    /// </summary>
    public DateTimeOffset? ResultSeenAt { get; set; }
}
