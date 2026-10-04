using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// How to name whoever holds an idea, for every surface that prints one (the list, and the
/// <c>idea.md</c> the daemon renders): this node's own record of that owner when it has one that
/// agrees with the recorded root, else the project's own member label for the root, else the owner
/// id's short form for an assignment this node can resolve to nobody. The text is returned as found:
/// a teammate's label is read from their own self-signed file, so the caller bounds it and keeps it to
/// one line before it reaches a terminal or a file.
/// </summary>
public static class IdeaAssigneeLabel
{
    /// <summary>The label, or null when nobody holds <paramref name="idea"/>.</summary>
    public static string? Of(
        IdeaDetails idea, IReadOnlyDictionary<Guid, OwnerDetails> owners,
        IReadOnlyDictionary<Guid, ProjectMemberLabels> labels)
    {
        if (idea.AssigneeOwnerId is not { } ownerId)
        {
            return null;
        }

        OwnerDetails? local = owners.GetValueOrDefault(ownerId);
        return (local, idea.AssigneeOwnerFingerprint) switch
        {
            ({ } owner, null) => owner.Name,
            ({ } owner, { } root) when owner.RootFingerprint == root => owner.Name,
            (_, { } root) => MemberLabelResolver.LabelForFingerprint(
                labels.GetValueOrDefault(idea.ProjectId ?? Guid.Empty), root),
            _ => DomainId.Short(ownerId),
        };
    }
}
