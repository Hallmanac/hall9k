namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A follow-up run pushed new commits to the task's existing pull request — the PR
/// updates in place; no second PR exists. Counterpart of PullRequestOpened for runs
/// dispatched via TaskReopened.
/// </summary>
public sealed record PullRequestUpdated(
    Guid Id,
    string PullRequestUrl,
    int PullRequestNumber,
    DateTimeOffset UpdatedAt);
