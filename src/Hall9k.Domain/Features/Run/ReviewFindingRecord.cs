namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One review finding as the run stream records it (Decisions Log #63): its grade, its scope
/// tag, where it points, and what the loop decided to do with it — never the finding's own
/// text, which stays an artifact in the run's directory (log #6).
/// <para>
/// This is the shape that makes the review history answerable: which severities forced which
/// cycles, which track produced them, and how many findings the loop routed away instead of
/// fixing. <see cref="Location"/> is a pointer (`path/to/file.cs:123`) rather than content, and
/// it is blank when the reviewer stated none.
/// </para>
/// </summary>
/// <param name="Track">
/// Which track a <see cref="ReviewMode.Verify"/> pass's single reviewer said this finding
/// belongs to (task: review cycles after the first, cycle-3 finding) — carried from
/// <c>ReviewFinding.Track</c>'s own `track=` tag onto the stream (the c-tag form, because Domain
/// references no Hall9k project and a Daemon cref cannot resolve here), so
/// a still-active track can be force-concluded (<c>ReviewEngine.SettleAsync</c>) crediting the
/// tag it actually named rather than whichever lens the settling loop happens to iterate first.
/// Null for every finding a real, single-lens pass produces, and for a Verify pass's finding
/// that named no track or an unrecognized one — read there the same conservative way an
/// ungraded severity already is, never guessed at.
/// </param>
public sealed record ReviewFindingRecord(
    ReviewSeverity Severity,
    ReviewFindingScope Scope,
    string Location,
    ReviewFindingDisposition Disposition,
    ReviewLens? Track = null);
