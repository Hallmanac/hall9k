namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed that the latest Copilot review on the run's pull
/// request is an error placeholder ("Copilot encountered an error and was unable to
/// review this pull request") rather than a real review. An errored review produces
/// zero review threads — indistinguishable from a clean pass by thread count alone —
/// so it is recorded as its own observation: the PR is not review-clean and the run
/// holds at ReviewPending (origin incident: PR #6, 2026-08-17, GitHub partial outage).
/// A ReviewRerequested (or a CloseoutParked, when the automatic budget is spent) is
/// appended in the same transaction.
/// </summary>
public sealed record ReviewErrored(
    Guid Id,
    string Reviewer,
    string ReviewUrl,
    DateTimeOffset ObservedAt);
