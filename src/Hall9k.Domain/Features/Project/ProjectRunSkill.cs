namespace Hall9k.Domain.Features.Project;

/// <summary>
/// This project's run skill as this node's own event stream last recorded it (idea b9b09779,
/// piece 4) — how to stand the project up locally, in the one shape every project shares
/// (<see cref="RunSkillDocument"/>). The ledger file the daemon writes from this is what a member
/// on another machine actually reads; this is this node's own audit trail of who composed it,
/// when, and against which commit.
/// </summary>
public sealed record ProjectRunSkill(
    string Content,
    RunSkillShape Shape,
    string ComposedAgainstCommit,
    RunSkillAuthor Author,
    DateTimeOffset RecordedAt,
    Guid RecordedByOwnerId);
