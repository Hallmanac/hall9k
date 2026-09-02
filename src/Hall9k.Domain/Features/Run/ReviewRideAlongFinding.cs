namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One ride-along (Decisions Log #87) as the run stream names it once the review settles: what
/// grade it carried and where it points, so a reader of the pull request or <c>h9k task show</c>
/// can identify the finding itself rather than only its count (independent pre-PR review, cycle
/// 2, conformance finding: the pull request body used to name only how many ride-alongs a run
/// carried, with no way to see or even identify what they actually were).
/// </summary>
/// <param name="Severity">The grade the reviewer stated, or <see cref="ReviewSeverity.Unknown"/> when it could not be read.</param>
/// <param name="Location">Where the reviewer pointed (`path/to/file.cs:123`), or blank when it named none.</param>
public sealed record ReviewRideAlongFinding(ReviewSeverity Severity, string Location);
