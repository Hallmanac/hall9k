using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// The facts <see cref="Handlers.TaskOwnerRule"/> judges, read off a board row instead of the
/// task's stream, so a surface that holds only <see cref="TaskListItem"/> documents asks the same
/// question the commands ask through <see cref="TaskOwnerFactsReader"/> and gets the same answer.
/// The two readers walk the same facts in the same order: the holder's root as recorded, the
/// assignee's root as recorded or else resolved from the owner id, and the creator only when both
/// of those are absent.
/// <para>
/// Pure: the owner-id lookup and the creator are handed in, because the caller that renders many
/// rows loads them once for the whole board. The creator is not a column of the row, since the
/// root a native genesis was stamped with is written after the inline projection has run
/// (<see cref="TaskOwnerFactsReader.ReadCreatorsAsync"/>).
/// </para>
/// </summary>
public static class TaskListItemOwnerFacts
{
    /// <param name="task">The board row.</param>
    /// <param name="ownerRoot">
    /// The root fingerprint an owner id currently claims on this node, or null when this node has no
    /// record of that owner (<see cref="Owner.OwnerRootFingerprintResolver"/>'s answer).
    /// </param>
    /// <param name="creator">
    /// The task's creator fact from <see cref="TaskOwnerFactsReader.ReadCreatorsAsync"/>. Looked at
    /// only for a task with neither a holder nor an assignee, so a caller need not read it for any
    /// other (<see cref="NeedsCreator"/>); null reads as unresolved.
    /// </param>
    public static TaskOwnerFacts From(TaskListItem task, Func<Guid, string?> ownerRoot, OwnerRootFact? creator)
    {
        OwnerRootFact holder = OwnerRootFact.KnownOrAbsent(task.HolderOwnerRootFingerprint);
        OwnerRootFact assigned = ReadAssigned(task, ownerRoot);
        OwnerRootFact creatorWhenItMatters = holder.State == OwnerRootFactState.Absent && assigned.State == OwnerRootFactState.Absent
            ? creator ?? OwnerRootFact.Unresolved
            : OwnerRootFact.Absent;
        return new TaskOwnerFacts(holder, assigned, creatorWhenItMatters);
    }

    /// <summary>Whether <see cref="From"/> would read the creator for this row, so a caller loads it only where it matters.</summary>
    public static bool NeedsCreator(TaskListItem task) =>
        string.IsNullOrEmpty(task.HolderOwnerRootFingerprint)
        && string.IsNullOrEmpty(task.AssignedOwnerFingerprint)
        && task.AssignedOwnerId is null;

    private static OwnerRootFact ReadAssigned(TaskListItem task, Func<Guid, string?> ownerRoot)
    {
        if (!string.IsNullOrEmpty(task.AssignedOwnerFingerprint))
        {
            return OwnerRootFact.Known(task.AssignedOwnerFingerprint);
        }

        if (task.AssignedOwnerId is not { } assignedOwnerId)
        {
            return OwnerRootFact.Absent;
        }

        string? resolved = ownerRoot(assignedOwnerId);
        return string.IsNullOrEmpty(resolved)
            ? OwnerRootFact.Unresolved
            : OwnerRootFact.Known(resolved);
    }
}
