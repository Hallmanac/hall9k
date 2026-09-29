namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// This pr-review task's pre-flight (idea 6be68ee2, finding 1, phase one) came back unsafe for the
/// pull request's own current head: Claimed -> Published, in one event, the same "give the claim
/// back and take the task out of the dispatcher's sight in a single write" shape
/// <see cref="TaskInteractiveClaimUnassigned"/> gives an interactive release, so there is no window
/// in which the dispatcher could see this task claimable again before a human has looked at the
/// verdict. Unlike <see cref="PullRequestReviewGateParked"/> — which parks a task that was never
/// claimed at all — this fires on a task the daemon already claimed and is mid-dispatch on, before
/// any worktree was cut; nothing is checked out.
/// <para>
/// Every field here is what the pre-flight session actually said, never re-derived later: the
/// session's own marker line (<see cref="Verdict"/>, <see cref="Reason"/>), the surfaces that
/// pointed its attention (<see cref="Surfaces"/>), and the exact head oid it judged
/// (<see cref="HeadRefOid"/>) — so a card reader can tell, after the pull request's head has since
/// moved again, exactly which commit this verdict was ever about.
/// </para>
/// </summary>
public sealed record PrReviewPreflightParked(
    Guid Id,
    IReadOnlyList<string> Surfaces,
    string HeadRefOid,
    string Verdict,
    string Reason,
    DateTimeOffset ParkedAt);
