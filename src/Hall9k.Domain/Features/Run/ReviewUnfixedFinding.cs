namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One <see cref="ReviewResidualDisposition.Unfixed"/> residual as the run stream names it once
/// the review settles: what grade it carried and where it points, the same shape
/// <see cref="ReviewRideAlongFinding"/> already gives a ride-along, so a reader of the pull
/// request or <c>h9k task show</c> can identify the finding itself rather than only its count.
/// </summary>
/// <param name="Severity">The reviewer's grade; <see cref="ReviewSeverity.Unknown"/> only for the unstructured placeholder <see cref="ReviewFindingDisposition.Fix"/> covers.</param>
/// <param name="Location">Where the reviewer pointed (`path/to/file.cs:123`), or blank when it named none.</param>
public sealed record ReviewUnfixedFinding(ReviewSeverity Severity, string Location);
