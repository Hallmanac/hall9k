namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// This project's run skill, as it now stands (idea b9b09779, piece 4). The one event every route
/// to a run skill goes through — a discovery session's composed markdown handed back through the
/// daemon, the daemon's own none-discoverable record for a repository with nothing to read, and a
/// human's <c>h9k project run-skill set --file</c> — so there is exactly one audit trail of who
/// composed it, when, and against what.
/// <para>
/// This node's own record, never the ledger's source of truth: the daemon alone writes
/// <c>run-skill.md</c> on <c>refs/hall9k/ledger/run-skill</c> from this event, so a dispatched
/// session can compose the whole skill without ever holding write access to the ledger — the same
/// split <c>ProjectPromptAddendumSet</c> already uses, and for the same reason.
/// </para>
/// </summary>
/// <param name="Content">The whole document, first line included (<c>RunSkillDocument.Compose</c> made it).</param>
/// <param name="Shape">
/// <c>RunSkillShape</c>'s own value — a closed vocabulary carried as a plain string, the way every
/// other closed vocabulary lands on an event.
/// </param>
/// <param name="ComposedAgainstCommit">
/// The commit the composer read. Blank when there was none to read honestly (a hand-set file
/// whose author named no commit, a repository whose HEAD could not be resolved) — never filled in
/// with a plausible value.
/// </param>
/// <param name="Author"><c>RunSkillAuthor</c>'s own value: discovery-session, hand, or platform.</param>
public sealed record ProjectRunSkillRecorded(
    Guid ProjectId,
    string Content,
    string Shape,
    string ComposedAgainstCommit,
    string Author,
    DateTimeOffset RecordedAt,
    Guid RecordedByOwnerId);
