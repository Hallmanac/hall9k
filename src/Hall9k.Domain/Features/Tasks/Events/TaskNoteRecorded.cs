using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A short note a task's working sessions leave each other, recorded on the task's own stream. It
/// lives there, with a note id inside each event, rather than on a stream of its own the way a
/// lesson does: a lesson needs an identity of its own to cite and retire, and a note has no retire
/// lifecycle and belongs to exactly one task. The task stream also brings project scope, the
/// private flag, the genesis rule, the inbox ownership guard and project purge for free
/// (<see cref="LessonPickRecorded"/> is the precedent).
/// <para>
/// <see cref="Provenance"/> is observed, never inferred: a note typed at a plain terminal names no
/// run even while the task has a live one, and a note from a dispatched or attended session names
/// that session's own run. The recording node is read off the event's metadata at projection time
/// (<c>EventRecordingNode</c>), which is why every append site pre-stamps it.
/// </para>
/// <para>
/// Nothing checks <see cref="Text"/> beyond its length. <c>RunHandoffRecorded</c>,
/// <c>TaskHandoffNoted</c> and <c>LearningRecorded</c> already put agent text in the same ledger
/// with no check, so a check on notes alone would close no hole; the prompt wording and the length
/// bound are the whole rule.
/// </para>
/// </summary>
/// <param name="Id">The task this note belongs to.</param>
/// <param name="NoteId">This note's own identity, which a closing block derives deterministically so reading the same final message twice records each note once.</param>
/// <param name="Text">The note, at most <see cref="TaskNoteLimits.MaxTextLength"/> characters.</param>
/// <param name="Provenance">The recording owner, the run if one was named, and what was observed of attendance.</param>
/// <param name="Source">Whether the verb or a closing block recorded it.</param>
/// <param name="RecordedAt">When the note was recorded.</param>
/// <param name="SessionId">The dispatched session's id, when a dispatched session recorded it; null otherwise.</param>
/// <param name="SessionName">The dispatched session's name, when a dispatched session recorded it; null otherwise.</param>
public sealed record TaskNoteRecorded(
    Guid Id,
    Guid NoteId,
    string Text,
    RecordedProvenance Provenance,
    TaskNoteSource Source,
    DateTimeOffset RecordedAt,
    string? SessionId = null,
    string? SessionName = null);
