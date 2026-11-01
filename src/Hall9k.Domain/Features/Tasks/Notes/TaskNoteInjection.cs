using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Shared.Rendering;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// Turns a task's notes and its epic siblings' notes into the bounded section a prompt carries, and
/// into the unbounded marked list <c>h9k task notes</c> prints. Pure, as <see cref="LessonInjection"/>
/// is: the store read and the node's identity are the caller's, so the caps, the ordering and the
/// trust rule are all provable without a store.
/// <para>
/// The trust rule is <see cref="LessonProvenanceMark"/>'s own, called rather than copied: a note
/// replicated from outside the reader's fleet, from a node nobody observed, or with no provenance
/// stays out of every prompt, and an unreadable fleet (a null <c>localFleet</c>) holds every
/// replicated note.
/// </para>
/// </summary>
public static class TaskNoteInjection
{
    /// <summary>
    /// Every note, marked for the reading node and ordered the way both readers list them: this
    /// task's own notes first, then the siblings', each group newest first (recorded time, then note
    /// id, so two notes in the same instant order the same way on every node).
    /// </summary>
    /// <param name="own">The reading task's own notes.</param>
    /// <param name="siblings">The notes of the other tasks in its epic in this project, each with the task it is on.</param>
    /// <param name="thisNodeId">The reading node's id, or <see cref="Guid.Empty"/> when it does not know it yet.</param>
    /// <param name="localFleet">The reading owner's fleet, or null when it could not be read.</param>
    public static IReadOnlyList<TaskNoteView> Classify(
        Guid ownTaskId,
        IEnumerable<TaskNoteEntry> own,
        IEnumerable<(Guid TaskId, TaskNoteEntry Entry)> siblings,
        Guid thisNodeId,
        IReadOnlySet<Guid>? localFleet)
    {
        IEnumerable<TaskNoteView> ownViews = Newest(
            own.Select(entry => new TaskNoteView(ownTaskId, true, entry, MarkOf(entry, thisNodeId, localFleet))));
        IEnumerable<TaskNoteView> siblingViews = Newest(
            siblings.Select(pair => new TaskNoteView(pair.TaskId, false, pair.Entry, MarkOf(pair.Entry, thisNodeId, localFleet))));

        return [.. ownViews, .. siblingViews];
    }

    /// <summary>
    /// The section a prompt carries: the notes the trust rule lets through, in the order
    /// <see cref="Classify"/> gave them, until the next one would pass either cap. The first note
    /// that does not fit ends the section, so what is carried is always an unbroken run of the
    /// newest, and everything after it is counted as left out. Notes held on provenance are counted
    /// under their marks and never offered, so a run of them cannot use up the budget.
    /// </summary>
    public static InjectedTaskNotes Select(IReadOnlyList<TaskNoteView> views)
    {
        List<InjectedTaskNote> shown = [];
        int eligible = 0;
        int characters = 0;
        bool full = false;
        Dictionary<LessonProvenanceMark, int> held = [];
        foreach (TaskNoteView view in views)
        {
            if (!view.ReachesAPrompt)
            {
                held[view.Mark] = held.GetValueOrDefault(view.Mark) + 1;
                continue;
            }

            eligible++;
            InjectedTaskNote note = new(
                view.TaskId, view.IsOwn, KnowledgeDocumentText.SingleLine(view.Entry.Note.Text), view.Mark);
            int cost = note.Text.Length;
            if (full || shown.Count >= TaskNoteLimits.MaxPromptNotes || characters + cost > TaskNoteLimits.MaxPromptCharacters)
            {
                full = true;
                continue;
            }

            shown.Add(note);
            characters += cost;
        }

        List<HeldLessonCount> heldByMark =
        [
            .. LessonProvenanceMark.HeldFromPrompts
                .Select(mark => new HeldLessonCount(mark, held.GetValueOrDefault(mark)))
                .Where(count => count.Count > 0),
        ];

        return new InjectedTaskNotes(shown, eligible - shown.Count, heldByMark);
    }

    private static LessonProvenanceMark MarkOf(TaskNoteEntry entry, Guid thisNodeId, IReadOnlySet<Guid>? localFleet) =>
        LessonProvenanceMark.Of(
            entry.Note.Provenance, entry.RecordedOnNodeId, thisNodeId, entry.ReceivedFromNodeId, localFleet);

    private static IEnumerable<TaskNoteView> Newest(IEnumerable<TaskNoteView> views) =>
        views.OrderByDescending(view => view.Entry.Note.RecordedAt)
            .ThenBy(view => view.Entry.Note.NoteId.ToString("N"), StringComparer.Ordinal);
}
