namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// Why this node's last dispatch sweep left a gated task Queued (idea 64c75e43) — mutable
/// telemetry, not an event, the same standing <see cref="TaskLease"/> has and for the same reason
/// (Decisions Log #7): a refusal is a measurement the next sweep overwrites, never a fact about
/// the task's history. <c>Id == <see cref="KeyFor"/>(TaskId, NodeId)</c>.
/// <para>
/// It exists because the refusal happens in the daemon and has to be readable from the CLI: a
/// human running <c>h9k status</c>, <c>h9k task show</c>, or <c>h9k project show</c> on a board
/// with nothing dispatching needs the same sentence the daemon log printed, including the
/// tracker's own error verbatim when the tracker could not be read at all. The published-
/// measurement shape is <c>NodeDispatchLoad</c>'s (Decisions Log #64): the daemon writes what it
/// observed, the surfaces read it rather than re-deriving it, so the board and the dispatcher
/// cannot disagree about why a queue is not moving.
/// </para>
/// <para>
/// Every "who" field is honestly nullable. <see cref="Identity"/> is null when this install's own
/// tracker identity could not be read; <see cref="Holder"/> is null both when nobody holds the
/// item and when the read failed before it could say, which <see cref="Error"/> is what
/// distinguishes.
/// </para>
/// </summary>
public sealed class TrackerClaimHold
{
    /// <summary>
    /// The task and the node together (<see cref="KeyFor"/>), not the task alone: every reader
    /// keeps only its own machine's holds, which presumes two nodes' holds for one task can
    /// coexist — and keyed by the task alone they cannot, so on a shared-Postgres install
    /// (PLAN.md §6.1's roadmap #5, the topology node identity is carried from day one for) each
    /// daemon's upsert would erase the other's and each machine's own board would intermittently
    /// lose its explanation entirely (independent pre-PR review, cycle 1, adversarial lens).
    /// <c>NodeDispatchLoad</c> is keyed per node for the same reason; this measurement is
    /// per node <em>and</em> per task, so its key has to carry both.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The task this hold is about — what a reader filters on, since it is no longer the key.</summary>
    public Guid TaskId { get; set; }

    /// <summary>The node whose sweep observed this — the machine an operator would look at.</summary>
    public Guid NodeId { get; set; }

    /// <summary>That node's machine name, so a CLI on this machine can tell its own node's holds apart.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>The provider whose item is gating this claim: <c>jira</c> or <c>github</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The item as the people who filed it say it: <c>PROJ-123</c>, or a bare issue number.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Where to go and do something about it — the card or the issue in a browser; null when the reference could not be turned into one.</summary>
    public string? ItemUrl { get; set; }

    /// <summary>This install's own tracker identity, as the tracker reported it; null when that read itself failed.</summary>
    public string? Identity { get; set; }

    /// <summary>Who the tracker says holds the item, as a human reads it; null when nobody does, or when nothing could be read.</summary>
    public string? Holder { get; set; }

    /// <summary>The tracker's own error, verbatim, when the item could not be read at all; null when it was read fine and simply names someone else.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether <see cref="Error"/> was the tracker refusing the credentials rather than failing
    /// to answer — the one distinction that changes what ends the hold (renew the token versus
    /// wait out the outage).
    /// </summary>
    public bool AuthenticationRefusal { get; set; }

    /// <summary>
    /// What ends the hold, in the imperative, composed at read time and published so a CLI
    /// surface never has to re-derive it from a connection record it may not be able to reach:
    /// assign yourself in the tracker, renew the token with the command printed, restore the
    /// connection, or wait out the outage. Overwritten on every re-read, so it can only be as
    /// stale as the cadence allows.
    /// </summary>
    public string? Lever { get; set; }

    /// <summary>When this node last read the tracker for this task — what the re-read cadence is measured from.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>
    /// One node's hold on one task, which is the grain the whole document has: the writer upserts
    /// its own row and deletes its own row, and neither act can reach a hold another node
    /// published about the same task. Composed rather than hashed so the key reads as what it is
    /// in a <c>psql</c> session.
    /// </summary>
    public static string KeyFor(Guid taskId, Guid nodeId) => $"{taskId:D}:{nodeId:D}";
}
