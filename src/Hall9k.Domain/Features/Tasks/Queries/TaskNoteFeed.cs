using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// The one read every prompt-composing caller and <c>h9k task notes</c> make to get a task's notes:
/// the task's own, then those of the other tasks in its epic in this project, marked against this
/// node's own identity. One entry point in the Domain, as <see cref="LessonPromptFeed"/> is, so the
/// daemon's dispatch, the daemon's fix passes and the CLI's attended and headless launches cannot
/// disagree about which notes are eligible.
/// <para>
/// Siblings are read by <see cref="TaskDetails.EpicId"/> within the task's own project, not through
/// the epic-group resolver, which spans every project of a solution: a solution epic's tasks in
/// other projects are not siblings. The notes are read from the same events, with nothing copied or
/// re-recorded.
/// </para>
/// </summary>
public static class TaskNoteFeed
{
    /// <summary>
    /// Every note this node can see for <paramref name="taskId"/> and its siblings, marked, held ones
    /// included. The caller decides what to do with the held ones: a prompt drops them and counts
    /// them, <c>h9k task notes</c> shows them marked as held.
    /// </summary>
    public static async Task<IReadOnlyList<TaskNoteView>> LoadViewsAsync(
        IQuerySession query, Guid taskId, LocalFleetReader? localFleet, CancellationToken cancellationToken)
    {
        TaskDetails? task = await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        if (task is null)
        {
            return [];
        }

        List<(Guid TaskId, TaskNoteEntry Entry)> siblings = [];
        if (task.EpicId is { } epicId)
        {
            Guid projectId = task.ProjectId;
            IReadOnlyList<TaskDetails> inEpic = await query.Query<TaskDetails>()
                .Where(candidate => candidate.EpicId == epicId && candidate.ProjectId == projectId && candidate.Id != taskId)
                .ToListAsync(cancellationToken);
            siblings =
            [
                .. inEpic.SelectMany(sibling => sibling.Notes.Select(entry => (sibling.Id, entry))),
            ];
        }

        bool anyReplicated = task.Notes.Concat(siblings.Select(pair => pair.Entry)).Any(entry => entry.IsReplicated);
        IReadOnlySet<Guid>? fleet = anyReplicated && localFleet is not null
            ? await localFleet(cancellationToken)
            : null;

        string machineName = Environment.MachineName;
        NodeDetails? node = (await query.Query<NodeDetails>()
            .Where(candidate => candidate.MachineName == machineName)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();

        return TaskNoteInjection.Classify(taskId, task.Notes, siblings, node?.Id ?? Guid.Empty, fleet);
    }

    /// <summary>
    /// The section a prompt carries for <paramref name="taskId"/>, or null when it could not be read.
    /// Null is a real answer: this runs on the dispatch path, and a prompt composed without its notes
    /// section is a worse prompt, never a failed dispatch. A task that simply holds no notes yields
    /// <see cref="InjectedTaskNotes.None"/>, which still teaches the verb.
    /// </summary>
    public static async Task<InjectedTaskNotes?> LoadForPromptAsync(
        IQuerySession query, Guid taskId, LocalFleetReader? localFleet, CancellationToken cancellationToken,
        Action<Exception>? onFailure = null)
    {
        try
        {
            return TaskNoteInjection.Select(await LoadViewsAsync(query, taskId, localFleet, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            onFailure?.Invoke(exception);
            return null;
        }
    }
}
