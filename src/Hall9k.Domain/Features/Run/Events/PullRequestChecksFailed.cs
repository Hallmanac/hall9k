namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed the run's pull request with completed-and-failing CI
/// checks. Recorded only once no check is still pending, so FailedChecks is the full
/// picture a fix-the-CI follow-up run needs. A follow-up dispatch (or a CloseoutParked,
/// when the automatic budget is spent) is appended in the same transaction.
/// </summary>
public sealed record PullRequestChecksFailed(
    Guid Id,
    IReadOnlyList<string> FailedChecks,
    DateTimeOffset ObservedAt);
