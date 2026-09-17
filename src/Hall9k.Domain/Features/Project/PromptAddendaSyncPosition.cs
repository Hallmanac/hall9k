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
}
