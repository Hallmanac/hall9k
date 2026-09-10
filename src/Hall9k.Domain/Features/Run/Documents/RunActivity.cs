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
}
