namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// FailedDuringPullRequestOpen is true only for a failure <c>PullRequestOpener</c>
/// itself recorded (a push refusal or a failed <c>gh pr create</c>, task: a run that failed only at
/// pull-request opening resumes at that step on retry) — the observed fact <c>RunLauncher</c>'s own
/// retry-time check reads to tell "everything upstream already finished; only the open itself failed"
/// apart from every other way a run can fail. False, never guessed, for every other failure and for a
/// stream written before this field existed.
/// </summary>
public sealed record RunFailed(
    Guid Id,
    string Reason,
    DateTimeOffset FailedAt,
    bool FailedDuringPullRequestOpen = false);
