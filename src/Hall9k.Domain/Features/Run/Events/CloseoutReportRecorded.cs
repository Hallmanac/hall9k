namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Closeout recorded what it found on a pull request it leaves alone while the task's interactive flag
/// is on (task: analysis-only closeout). Appended only when the report differs from the last one on
/// the run (a check newly failing or cleared, a conflict or a changes-requested review appearing or
/// cleared, a parent moving, a triage landing), never once per poll, because closeout re-reads the
/// same pull request every sweep. An empty report is recorded too, once, and is what clears the row.
/// <para>
/// The report carries check names, review urls, counts, and the platform's own sentences, never a
/// reviewer's words (see <see cref="CloseoutReport"/>), so the status row and the orchestrator feed
/// can render it with permissions bypassed.
/// </para>
/// </summary>
public sealed record CloseoutReportRecorded(
    Guid Id,
    CloseoutReport Report,
    DateTimeOffset RecordedAt);
