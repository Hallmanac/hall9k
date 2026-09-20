namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The daemon's own durable progress pushing one project's own run-skill changes out to the
/// ledger (idea b9b09779, piece 4) — a plain document, not an event-sourced aggregate, the same
/// reasoning <see cref="PromptAddendaSyncPosition"/> gives for being one: derived, mechanical
/// bookkeeping about how far a sweep has scanned, never a domain fact worth a stream of its own.
/// <see cref="Id"/> is the project's own local id, one document per project.
/// </summary>
public sealed class RunSkillSyncPosition
{
    public Guid Id { get; set; }

    /// <summary>The highest global event sequence this node has already scanned for this
    /// project's own run-skill changes — no event at or below this point is scanned again.</summary>
    public long LastScannedGlobalSequence { get; set; }

    /// <summary>
    /// What the ledger push last threw, when it did — cleared the moment a later push actually
    /// lands. Surfaced by <c>h9k project run-skill show</c> for the identical reason the
    /// prompt-addenda pane surfaces its own: without it, a standing push failure leaves the whole
    /// feature silently inert while every local read keeps reporting the skill as set.
    /// </summary>
    public string? LastPushError { get; set; }

    /// <summary>When <see cref="LastPushError"/> was last recorded.</summary>
    public DateTimeOffset? LastPushErrorAt { get; set; }
}
