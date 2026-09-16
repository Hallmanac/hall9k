namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed that Copilot's errored review names a quota refusal —
/// "the user who requested the review has reached their quota limit" — rather than a
/// transient failure. Brian's ruling, 2026-09-16 morning: once Copilot says the quota is
/// out, accept it and keep going, rather than spend the automatic budget re-requesting a
/// review that will keep refusing for the identical reason. Recorded once per errored
/// review url (<see cref="Projections.RunDetails.CopilotReviewUnavailableUrl"/> is the
/// dedup key, on the same terms <see cref="ReviewErrored"/>'s own <c>ErroredReviewUrl</c>
/// already uses), and carries no state transition of its own: the run is left exactly
/// where it was, so the remaining gates decide the closeout as they would with a landed
/// review.
/// </summary>
public sealed record CopilotReviewUnavailable(
    Guid Id,
    string Reviewer,
    string ReviewUrl,
    string Reason,
    DateTimeOffset ObservedAt);
