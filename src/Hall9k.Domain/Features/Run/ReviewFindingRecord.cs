namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One review finding as the run stream records it (Decisions Log #62): its grade, its scope
/// tag, where it points, and what the loop decided to do with it — never the finding's own
/// text, which stays an artifact in the run's directory (log #6).
/// <para>
/// This is the shape that makes the review history answerable: which severities forced which
/// cycles, which track produced them, and how many findings the loop routed away instead of
/// fixing. <see cref="Location"/> is a pointer (`path/to/file.cs:123`) rather than content, and
/// it is blank when the reviewer stated none.
/// </para>
/// </summary>
public sealed record ReviewFindingRecord(
    ReviewSeverity Severity,
    ReviewFindingScope Scope,
    string Location,
    ReviewFindingDisposition Disposition);
