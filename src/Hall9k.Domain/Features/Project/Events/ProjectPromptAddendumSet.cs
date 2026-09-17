namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// A project set (or replaced) its own addendum for one prompt builder (idea b9b09779, piece 6).
/// This node's own audit record of who and when — never the ledger's source of truth: the daemon
/// alone writes <c>prompt-addenda/&lt;builder&gt;.md</c> on <c>refs/hall9k/ledger/prompt-addenda</c>
/// from this event, so a dispatched session can add project guidance to a prompt without ever
/// holding write access to the ledger itself.
/// <para>
/// Each set replaces the whole file — there is no partial edit — so 'additive' describes only how
/// the addendum joins a shipped prompt (after the rules section, never in place of any of it),
/// never the addendum's own history.
/// </para>
/// </summary>
public sealed record ProjectPromptAddendumSet(
    Guid ProjectId,
    string BuilderKey,
    string Content,
    bool OverCap,
    string? OverCapReason,
    DateTimeOffset SetAt,
    Guid SetByOwnerId);
