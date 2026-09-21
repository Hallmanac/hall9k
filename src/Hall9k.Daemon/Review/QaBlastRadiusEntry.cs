using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// One line of a QA review's blast-radius map, as the reviewer wrote it (idea b9b09779, piece
/// 2): a behaviour this change touches, directly or through a code path or a data shape it
/// shares, and what that behaviour is owed. An artifact rather than event payload, the same
/// call <see cref="ReviewFinding"/> makes and for the same reason (log #6) — the map's text is
/// carried in process as far as the findings report a human reads, and nowhere further.
/// </summary>
/// <param name="Id">The short label later findings cite this entry by. Blank when the entry named none.</param>
/// <param name="Verdict">Covered, owed a new automated test, or owed a human walk-through; Unstated when the entry graded nothing.</param>
/// <param name="Text">The entry verbatim, header line included.</param>
public sealed record QaBlastRadiusEntry(string Id, QaCoverageVerdict Verdict, string Text);
