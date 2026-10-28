namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// An interactive claim taken on a task whose pull request is open (<c>h9k task drive</c> at the
/// PR stage) is given back without delivering anything: the task returns to Done with its pull
/// request, and the run that opened that pull request is again the task's current run, so
/// closeout's ordinary watch resumes where the claim paused it. This is the reverse of the
/// reopen-and-claim, and not <see cref="TaskRequeued"/>: a requeue would queue a follow-up lap for
/// a pull request nobody asked closeout to touch.
/// <para>
/// <see cref="PullRequestRunId"/> rides on the event rather than being re-derived by each reader,
/// for the same reason <see cref="PullRequestReviewMentionFollowUpSkipped"/> carries what its claim
/// overwrote: every projection that mirrors the task has to land on the identical answer.
/// <see cref="ClaimRunId"/> is the claim's own run, which the caller retires.
/// </para>
/// </summary>
/// <param name="ClearInteractiveMode">
/// Whether the task's interactive flag comes off with the claim, as <see cref="TaskRequeued.ClearInteractiveMode"/>
/// does for a release: true unless the owner asked to keep it.
/// </param>
public sealed record InteractiveClaimReturnedToPullRequest(
    Guid Id,
    Guid ClaimRunId,
    Guid PullRequestRunId,
    bool ClearInteractiveMode,
    DateTimeOffset ReturnedAt);
