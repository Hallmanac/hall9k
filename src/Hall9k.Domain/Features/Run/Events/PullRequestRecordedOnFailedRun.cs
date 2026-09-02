namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Records a pull request against a run that stays Failed (backlog: a pull request recorded by
/// h9k task resolve --pr is observed to merge like any other). <c>h9k task resolve --pr</c>
/// carries the URL onto the task stream via <see cref="Hall9k.Domain.Features.Tasks.Events.TaskResolved"/>,
/// but that never touched the run stream — <see cref="Hall9k.Domain.Features.Run.Projections.RunDetails.PullRequestNumber"/>
/// stayed null forever, which is exactly what excludes a Failed row from
/// <c>CloseoutEngine</c>'s orphan sweep (Decisions Log #72). This is the run-side counterpart,
/// appended alongside <c>TaskResolved</c> when the resolution names a parseable pull request.
/// <para>
/// Deliberately not <see cref="PullRequestOpened"/> or <see cref="PullRequestUpdated"/>: either
/// one moves <c>RunState</c> to <c>AwaitingReview</c>, which would pull a Failed run into the
/// WATCHED sweep instead of the orphan one — the watched path dispatches follow-up runs onto the
/// branch, and the orphan sweep's own doc comment says inventing a follow-up onto a dead run's
/// branch is not its job. This event carries the pull request without moving the run out of
/// Failed, so it lands in the orphan sweep's candidate set exactly as any other Failed run with a
/// recorded pull request number does.
/// </para>
/// </summary>
public sealed record PullRequestRecordedOnFailedRun(
    Guid Id,
    string PullRequestUrl,
    int PullRequestNumber,
    DateTimeOffset RecordedAt);
