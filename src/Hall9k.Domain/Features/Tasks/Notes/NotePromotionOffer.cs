using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>One note as the promotion session is shown it: the id it is named by, and the note's own text.</summary>
/// <param name="NoteId">The note.</param>
/// <param name="Text">The note as recorded; the prompt builder makes it one line of printable text.</param>
public sealed record OfferedNote(Guid NoteId, string Text)
{
    /// <summary>The eight characters the session names this note by, as every other h9k surface names an id.</summary>
    public string ShortId => DomainId.Short(NoteId);
}

/// <summary>
/// The notes the closeout promotion step may show its session, picked from a task's own notes.
/// Pure, as <see cref="TaskNoteInjection"/> is: the store read and the node's identity are the
/// caller's, so the rule is provable without a store.
/// </summary>
/// <param name="Offered">The notes shown, newest first, at most <see cref="NotePromotionLimits.MaxOffered"/>.</param>
/// <param name="NotOffered">The ids of the notes the trust rule let through that the ceiling left out, newest first.</param>
/// <param name="HeldCount">How many of the task's own notes the trust rule held out of every prompt; counted and never shown.</param>
public sealed record NotePromotionOffer(IReadOnlyList<OfferedNote> Offered, IReadOnlyList<Guid> NotOffered, int HeldCount)
{
    /// <summary>
    /// Whether the step runs at all: only a task with at least one note the closing node may offer
    /// does. A task whose notes are all held runs nothing and records nothing.
    /// </summary>
    public bool Any => Offered.Count > 0;

    /// <summary>
    /// The offer from <paramref name="views"/>, as <see cref="TaskNoteInjection.Classify"/> marked
    /// and ordered them for the closing node. Only the task's own notes are considered, so a sibling
    /// task's notes are never offered, and a note is offered only when
    /// <see cref="TaskNoteView.ReachesAPrompt"/> says so, which is <see cref="LessonProvenanceMark"/>'s
    /// own rule and the one the notes section of a prompt applies: there is no second copy of it here.
    /// </summary>
    public static NotePromotionOffer Select(IReadOnlyList<TaskNoteView> views)
    {
        TaskNoteView[] own = [.. views.Where(view => view.IsOwn)];
        TaskNoteView[] eligible = [.. own.Where(view => view.ReachesAPrompt)];
        return new NotePromotionOffer(
            [.. eligible.Take(NotePromotionLimits.MaxOffered)
                .Select(view => new OfferedNote(view.Entry.Note.NoteId, view.Entry.Note.Text))],
            [.. eligible.Skip(NotePromotionLimits.MaxOffered).Select(view => view.Entry.Note.NoteId)],
            own.Length - eligible.Length);
    }
}
