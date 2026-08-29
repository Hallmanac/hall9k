namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// An out-of-scope, non-High review finding was routed out of this pull request instead of
/// fixed in it (Decisions Log #63): the daemon turned the reviewer's structured finding into a
/// draft bug task, inert until a human publishes it. A pre-existing defect neither grows this
/// diff nor gets forgotten.
/// <para>
/// <see cref="DraftTaskId"/> names the draft the finding landed on: a Medium's own fresh
/// <c>Bugfix</c> draft, or a Low's project-wide sweep draft, which most routings revise rather
/// than create (Decisions Log #99). It is null when routing failed, and
/// <see cref="FailureReason"/> then says why: routing is a courtesy the review loop pays to a
/// defect it is not fixing, and a courtesy that fails must never fail the review. The finding is
/// recorded as routed either way, because "we tried to route this and could not" is the fact,
/// and a silently dropped routing would read afterwards as one that worked.
/// </para>
/// </summary>
public sealed record ReviewFindingRouted(
    Guid Id,
    ReviewLens Lens,
    int Cycle,
    ReviewSeverity Severity,
    string Location,
    Guid? DraftTaskId,
    string? FailureReason,
    DateTimeOffset RoutedAt);
