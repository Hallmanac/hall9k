using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Daemon.Feedback;

/// <summary>
/// The notes a task's blockers left that the trust rule lets into a prompt, read by id. The epic view
/// (<see cref="Hall9k.Domain.Features.Tasks.Queries.TaskNoteFeed"/>) covers only a task's epic siblings, and a blocker is not
/// necessarily one, so the step reads each blocker's own notes. The trust rule is <see cref="TaskNoteInjection"/>'s own, called
/// rather than copied: a note replicated from outside the reading node's fleet, from a node nobody observed, or with no
/// provenance is held out and never reaches the check's prompt or the refinement's, and an unreadable fleet holds every
/// replicated note.
/// </summary>
public static class FeedbackNotes
{
    /// <summary>
    /// The notes of <paramref name="blockerIds"/> as a prompt carries them: those the trust rule lets through, newest first,
    /// bounded by the same caps every prompt's notes section has, with the rest counted. Empty when no blocker left one.
    /// </summary>
    public static async Task<InjectedTaskNotes> LoadAsync(
        IQuerySession query, IReadOnlyList<Guid> blockerIds, Guid thisNodeId, LocalFleetReader? localFleet,
        CancellationToken cancellationToken)
    {
        if (blockerIds.Count == 0)
        {
            return InjectedTaskNotes.None;
        }

        List<Guid> declared = [.. blockerIds];
        IReadOnlyList<TaskDetails> blockers = await query.LoadManyAsync<TaskDetails>(cancellationToken, [.. declared]);
        List<(Guid TaskId, TaskNoteEntry Entry)> entries =
        [
            .. blockers.OrderBy(blocker => declared.IndexOf(blocker.Id))
                .SelectMany(blocker => blocker.Notes.Select(entry => (blocker.Id, entry))),
        ];
        if (entries.Count == 0)
        {
            return InjectedTaskNotes.None;
        }

        IReadOnlySet<Guid>? fleet = entries.Any(pair => pair.Entry.IsReplicated) && localFleet is not null
            ? await localFleet(cancellationToken)
            : null;

        // No task is "the reading task" here: every note is a blocker's, so none is marked own and each line names its blocker.
        return TaskNoteInjection.Select(TaskNoteInjection.Classify(Guid.Empty, [], entries, thisNodeId, fleet));
    }
}
