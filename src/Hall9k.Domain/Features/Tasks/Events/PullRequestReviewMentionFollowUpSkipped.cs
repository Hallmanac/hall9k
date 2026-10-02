namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A mention follow-up was claimed for this pr-review task and then refused before any session
/// launched, because the stored comment did not tag this install's own login, was written by it,
/// or this install's login could not be read to tell even after the launch retried the read. The claim is given back: the task returns
/// to the state it held before the follow-up was first claimed, and
/// <see cref="TaskAggregate.PendingMentionFollowUpAfterPreflight"/> clears so the next dispatch
/// does not try the same comment again.
/// <para>
/// <see cref="ReturnedToState"/>, <see cref="ReturnedToRunId"/> and <see cref="ReturnedToNodeId"/>
/// are carried on the event rather than re-derived by each reader, because the claim overwrote the
/// aggregate's own copies and every projection that mirrors the task has to land on the identical
/// answer. <see cref="RunId"/> is the claim's run: the event only applies while that run is still
/// the task's current one, so a skip that arrives late never undoes a newer claim.
/// </para>
/// </summary>
public sealed record PullRequestReviewMentionFollowUpSkipped(
    Guid Id,
    Guid RunId,
    string CommentId,
    string Reason,
    TaskState ReturnedToState,
    Guid? ReturnedToRunId,
    Guid? ReturnedToNodeId,
    DateTimeOffset SkippedAt);
