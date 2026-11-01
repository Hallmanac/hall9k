using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using UUIDNext;

namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// The rules for recording a task note. The aggregate holds none of them (it has nothing to change
/// for a note); they live here, as every other decider's do.
/// </summary>
public static class TaskNoteDecider
{
    /// <summary>
    /// Builds the note event for the verb: refuses blank text, and text over
    /// <see cref="TaskNoteLimits.MaxTextLength"/> characters with the limit named, recording nothing.
    /// </summary>
    public static TaskNoteRecorded Record(
        Guid taskId, string? text, RecordedProvenance provenance, string? sessionId, string? sessionName,
        DateTimeOffset now) =>
        new(taskId, DomainId.New(), RequireText(text), provenance, TaskNoteSource.Verb, now, sessionId, sessionName);

    /// <summary>
    /// The trimmed note text, or a refusal naming the limit. Public so a caller can refuse before it
    /// opens a store at all.
    /// </summary>
    public static string RequireText(string? text)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException(
                "A task note needs some text. Say what the next session on this task should know.");
        }

        return trimmed.Length > TaskNoteLimits.MaxTextLength
            ? throw new DomainValidationException(
                $"A task note is at most {TaskNoteLimits.MaxTextLength} characters; this one is {trimmed.Length}. "
                + "Shorten it, or split it into two notes. Nothing was recorded.")
            : trimmed;
    }

    /// <summary>
    /// Builds the note events for the bullets of a closing block, one per bullet, with note ids
    /// derived from the task, the run, the bullet's position and its text, so reading the same final
    /// message twice produces the same ids and the caller records each note once. Text is already
    /// bounded by the block parser; blank bullets are skipped here too.
    /// </summary>
    public static IReadOnlyList<TaskNoteRecorded> FromClosingBlock(
        Guid taskId, Guid runId, IReadOnlyList<string> bullets, RecordedProvenance provenance,
        string? sessionId, string? sessionName, DateTimeOffset now)
    {
        List<TaskNoteRecorded> notes = [];
        for (int position = 0; position < bullets.Count; position++)
        {
            string text = bullets[position].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            Guid noteId = Uuid.NewNameBased(runId, $"{taskId:N}|{position}|{text}");
            notes.Add(new TaskNoteRecorded(
                taskId, noteId, text, provenance, TaskNoteSource.ClosingBlock, now, sessionId, sessionName));
        }

        return notes;
    }
}
