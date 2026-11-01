namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// What the promotion step decided about one note: kept, with the lesson it became, or dropped, with
/// the reason. Exactly one of the two is set. An id and a closed word, never text.
/// </summary>
/// <param name="NoteId">The note.</param>
/// <param name="LessonId">The lesson the note became, or null when it was dropped.</param>
/// <param name="Dropped">Why the note was dropped, or null when it was kept.</param>
public sealed record NotePromotionDecision(Guid NoteId, Guid? LessonId, NoteDropReason? Dropped)
{
    public static NotePromotionDecision Kept(Guid noteId, Guid lessonId) => new(noteId, lessonId, null);

    public static NotePromotionDecision Drop(Guid noteId, NoteDropReason reason) => new(noteId, null, reason);

    public bool IsKept => LessonId is not null;
}
