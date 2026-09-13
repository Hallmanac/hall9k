namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Historical only — retired by the fan-out redesign (backlog 31). <c>h9k idea promote</c> no
/// longer appends this event; it is now sugar over <see cref="IdeaTaskCut"/> followed by
/// <see cref="IdeaConcluded"/>, the two doors every idea uses now. This type and its
/// <c>Apply</c> handlers stay only to replay streams that already recorded it — nothing is
/// deleted or rewritten. A promotion always meant something came of the idea, which is exactly
/// what <see cref="IdeaState.Concluded"/> means today, so replaying this event (or reading a
/// document it last wrote) lands there.
/// <para>
/// Provenance ran both ways: this named the draft the idea became, and that draft's TaskAdded
/// named this idea as its source. Objective is the seed the draft was created with, recorded
/// here because it is what promotion decided — taken mechanically from the idea's first
/// sentence, or typed by the human as --objective, never inferred.
/// </para>
/// </summary>
public sealed record IdeaPromoted(
    Guid Id,
    Guid TaskId,
    Guid ProjectId,
    string Objective,
    DateTimeOffset PromotedAt,
    Guid PromotedByOwnerId);
