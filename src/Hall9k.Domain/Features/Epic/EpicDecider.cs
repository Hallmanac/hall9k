using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// Every decision an epic makes, in one place — the same pattern <c>TaskDecider</c> and
/// <c>IdeaDecider</c> already follow. The rules are deliberately few: an epic is a name and a
/// project, and the only hard edge is closing, which is human-only and always carries a
/// reason (Brian's ruling, 2026-08-28: nothing closes an epic automatically, including its
/// last member task closing out).
/// </summary>
public static class EpicDecider
{
    public static EpicAdded Add(
        Guid id, Guid projectId, string title, DateTimeOffset addedAt, Guid addedByOwnerId)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("An epic belongs to a project.");
        }

        if (title.IsBlank())
        {
            throw new DomainValidationException(
                "An epic needs a title: h9k epic add --project <p> --title \"<name>\".");
        }

        return new EpicAdded(id, projectId, title.Trim(), addedAt, addedByOwnerId);
    }

    /// <summary>
    /// Whether the epic already carries exactly this Jira reference. Asked before
    /// <see cref="LinkJira"/> so repeating the link is quiet rather than an error, the same
    /// idempotency <c>TaskDecider.AlreadyLinkedTo</c> gives a task's own external reference.
    /// </summary>
    public static bool AlreadyLinkedTo(EpicAggregate epic, string reference) =>
        epic.JiraReference is { } existing && existing == reference.Trim();

    /// <summary>
    /// Record a Jira epic key or URL, identity only: nothing here reads Jira, and nothing here
    /// ever will (Brian's Jira ruling, 2026-08-28). The reference is stored exactly as typed —
    /// this is a pointer for a human to click, not a fact the platform verified.
    /// </summary>
    public static EpicLinkedToJira LinkJira(
        EpicAggregate epic, string reference, DateTimeOffset linkedAt, Guid linkedByOwnerId)
    {
        if (reference.IsBlank())
        {
            throw new DomainValidationException(
                "A Jira link needs a key or URL, for example PROJ-123 or "
                + "https://your-org.atlassian.net/browse/PROJ-123.");
        }

        if (epic.State == EpicState.Closed)
        {
            throw new DomainConflictException(
                $"Epic {epic.Id} is closed, so there is nothing live to point a Jira link at.");
        }

        if (epic.JiraReference is { } existing && existing != reference.Trim())
        {
            throw new DomainConflictException(
                $"Epic {epic.Id} is already linked to {existing}. An epic carries one Jira pointer; "
                + "if it is wrong, that is worth a human looking at rather than silently overwriting.");
        }

        return new EpicLinkedToJira(epic.Id, reference.Trim(), linkedAt, linkedByOwnerId);
    }

    /// <summary>
    /// The only way an epic ends: an explicit human act with a reason. Never automatic — not
    /// when its last member task closes out, not when every task leaves it (Brian's ruling,
    /// 2026-08-28, the standing never-auto-close doctrine).
    /// </summary>
    public static EpicClosed Close(
        EpicAggregate epic, string reason, DateTimeOffset closedAt, Guid closedByOwnerId)
    {
        if (epic.State != EpicState.Open)
        {
            throw new DomainConflictException(
                $"Epic {epic.Id} is {epic.State.Value} — only an open epic closes.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                $"Closing an epic records why: h9k epic close {epic.Id} --reason \"<why this is done>\".");
        }

        return new EpicClosed(epic.Id, reason.Trim(), closedAt, closedByOwnerId);
    }
}
