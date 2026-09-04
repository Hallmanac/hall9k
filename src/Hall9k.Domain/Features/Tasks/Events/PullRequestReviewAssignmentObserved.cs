namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The go signal for an auto-created pr-review task (idea e5e98a33, PLAN.md §16 decision #34's
/// amendment): a GitHub reviewer assignment to this install's own login, observed on an opted-in
/// project's repo, is what created and started this task — not a human typing
/// <c>h9k task add --from-pr</c>. Recorded as provenance rather than trusted silently, the same
/// observed-vs-platform-act separation <see cref="WorkItemLinked"/> already draws:
/// <see cref="AssigneeLogin"/> and <see cref="AssignedByLogin"/> are what GitHub reported,
/// <see cref="ObservedAt"/> is when this install's own poll saw it — never GitHub's own
/// assignment timestamp, which the read this feature uses does not reliably carry.
/// <see cref="AssignedByLogin"/> is honestly null when the read could not attribute the request
/// to a specific actor (AGENTS.md, never guess at unobserved facts).
/// </summary>
public sealed record PullRequestReviewAssignmentObserved(
    Guid Id,
    string PullRequestUrl,
    string AssigneeLogin,
    string? AssignedByLogin,
    DateTimeOffset ObservedAt);
