namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// An owner skipped the rest of the pre-PR pipeline and sent the run straight to its pull request
/// (<c>h9k task skip-to-pr</c>, idea 6e6d0200, Card 3). A manual override, recorded so the stream
/// says who did it and what it bypassed, rather than the run quietly reaching merge-ready.
/// <para>
/// Applied, it leaves the run UnderReview at <see cref="ReviewPhase.MergeReady"/> with
/// <see cref="ReviewSettlement.Overridden"/> and the interactive gate cleared, so the daemon's
/// stranded-pipeline sweep hands it to <c>PullRequestOpener</c> without a gate, a review or fix
/// session, or another interactive park. The override covers this run only; a later run of the same
/// task starts with none.
/// </para>
/// <para>
/// <see cref="Skipped"/> is computed from where the run stood (<see cref="ReviewSkipPlan"/>), not
/// hard-coded: the gates when the run had not passed them, each review track it would still have
/// run, the final pass, and the settling gate. <see cref="Reason"/> is optional and null when the
/// owner gave none; <see cref="ReasonByAgent"/> marks a reason the driving session wrote rather than
/// the human, so no surface presents the session's words as the owner's.
/// </para>
/// </summary>
public sealed record ReviewSkippedToPullRequest(
    Guid Id,
    Guid SkippedByOwnerId,
    DateTimeOffset SkippedAt,
    string? Reason,
    bool ReasonByAgent,
    IReadOnlyList<string> Skipped,
    string? OwnerRootFingerprint = null);
