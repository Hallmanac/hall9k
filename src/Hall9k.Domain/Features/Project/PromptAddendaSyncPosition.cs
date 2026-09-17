namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The daemon's own durable progress pushing one project's own prompt-addendum changes out to the
/// ledger (idea b9b09779, piece 6) — a plain document, not an event-sourced aggregate, the same
/// reasoning <c>EventReplicationOutboxPosition</c> gives for being one: derived, mechanical
/// bookkeeping about how far a sweep has scanned, never a domain fact worth a stream of its own.
/// <see cref="Id"/> is the project's own local id, one document per project.
/// </summary>
public sealed class PromptAddendaSyncPosition
{
    public Guid Id { get; set; }

    /// <summary>The highest global event sequence this node has already scanned for this
    /// project's own prompt-addendum changes — never an event past this point is scanned again.</summary>
    public long LastScannedGlobalSequence { get; set; }

    /// <summary>
    /// What the ledger push last threw, when it did — cleared the moment a later push actually
    /// lands. A push failure otherwise leaves the whole feature silently inert (the CLI reports
    /// success, <c>list</c>/<c>show</c> keep reporting the addendum as set) with nothing visible
    /// short of reading daemon logs, so <c>h9k project prompt-addendum list</c>/<c>show</c> surface
    /// this instead of leaving it there alone (independent pre-PR review, cycle 1, adversarial
    /// lens, medium).
    /// </summary>
    public string? LastPushError { get; set; }

    /// <summary>When <see cref="LastPushError"/> was last recorded.</summary>
    public DateTimeOffset? LastPushErrorAt { get; set; }
}
