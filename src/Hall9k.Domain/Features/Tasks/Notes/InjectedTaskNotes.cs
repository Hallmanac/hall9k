using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// One note as the reader of a task's notes sees it: the entry, whose task it is on, and the
/// provenance mark the trust rule gave it from this node's point of view. Derived at read time and
/// never persisted.
/// </summary>
/// <param name="TaskId">The task the note is on.</param>
/// <param name="IsOwn">True for a note on the task being read, false for a sibling's in the same epic.</param>
/// <param name="Entry">The note and the node ids it was projected with.</param>
/// <param name="Mark">What <see cref="LessonProvenanceMark"/> says about it for the reading node.</param>
/// <param name="Decision">What the closeout promotion step recorded about this note, or null when its task's record does not name it.</param>
public sealed record TaskNoteView(
    Guid TaskId, bool IsOwn, TaskNoteEntry Entry, LessonProvenanceMark Mark, NotePromotionDecision? Decision = null)
{
    /// <summary>Whether the trust rule lets this note into a prompt, by the same rule lessons use.</summary>
    public bool ReachesAPrompt => Mark.ReachesAPrompt;

    /// <summary>This view with what <paramref name="record"/>, the note's own task's promotion record, decided about the note; unchanged when there is no record.</summary>
    public TaskNoteView DecidedBy(NotePromotionRecorded? record) =>
        record is null
            ? this
            : this with { Decision = record.DecisionFor(Entry.Note.NoteId) };

    /// <summary>
    /// What became of the note, for the two surfaces that show it: the promotion record's word when
    /// that record names the note, otherwise held when the reading node holds it, otherwise
    /// undecided, which means no promotion step decided it.
    /// </summary>
    public string OutcomeLabel => Decision switch
    {
        { LessonId: { } lessonId } => $"kept as lesson {DomainId.Short(lessonId)}",
        { Dropped: { } reason } => $"dropped ({reason.Value})",
        _ when !ReachesAPrompt => "held",
        _ => "undecided",
    };
}

/// <summary>One note as a prompt carries it: a single line of text, labelled with the task it is on.</summary>
/// <param name="TaskId">The task the note is on.</param>
/// <param name="IsOwn">True for the reading task's own note.</param>
/// <param name="Text">The note collapsed onto one line.</param>
/// <param name="Mark">The provenance mark, rendered with its own words.</param>
public sealed record InjectedTaskNote(Guid TaskId, bool IsOwn, string Text, LessonProvenanceMark Mark)
{
    /// <summary>The eight characters every other h9k surface names a task by.</summary>
    public string ShortTaskId => DomainId.Short(TaskId);

    /// <summary>
    /// The bullet this note renders as. A sibling's note leads with that task's short id, so a reader
    /// can tell a colleague's note on another task from one on this task.
    /// </summary>
    public string Line => $"- {(IsOwn ? "this task" : $"task {ShortTaskId}")}: {Text} ({Mark.Label})";
}

/// <summary>
/// The bounded slice of a task's notes (and its epic siblings' notes) a prompt carries, together
/// with everything the section has to say aloud about what it left out. A capped section that
/// quietly drops its overflow teaches a reader that what it was handed is everything there is, so
/// every note not carried is counted under the reason it was not.
/// </summary>
/// <param name="Notes">The notes carried: this task's own, newest first, then its epic siblings', newest first.</param>
/// <param name="LeftOut">How many notes the trust rule let through that the two caps left out.</param>
/// <param name="HeldByMark">How many notes the trust rule held out of every prompt, under each mark that held one.</param>
public sealed record InjectedTaskNotes(
    IReadOnlyList<InjectedTaskNote> Notes,
    int LeftOut,
    IReadOnlyList<HeldLessonCount> HeldByMark)
{
    /// <summary>A task that holds no notes: the section still teaches the verb, and has nothing to list.</summary>
    public static readonly InjectedTaskNotes None = new([], 0, []);

    /// <summary>True when at least one note is carried.</summary>
    public bool Any => Notes.Count > 0;

    /// <summary>How many notes the trust rule held back, whatever the reason.</summary>
    public int HeldForProvenance => HeldByMark.Sum(held => held.Count);
}
