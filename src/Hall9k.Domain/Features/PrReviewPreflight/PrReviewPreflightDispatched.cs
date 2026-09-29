using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// The pull-request review pre-flight (idea 6be68ee2, finding 1, phase one): a short security lap
/// that reads a pull request's own changed-file list and matched diff hunks through <c>gh</c>,
/// with no worktree, before any checkout is cut. Its own stream, keyed by <see cref="Id"/>, a
/// fresh id minted the same way <c>CourierRunDispatched</c>'s own is — there is no
/// <see cref="Tasks.TaskAggregate"/> event this belongs on, since a pull request's head can move
/// between a pre-flight and the task's next dispatch, and a verdict is a fact about ONE judged
/// oid, never the task's own current state.
/// </summary>
public sealed record PrReviewPreflightDispatched(
    Guid Id,
    Guid TaskId,
    Guid NodeId,
    AgentModel Model,
    string HeadRefOid,
    IReadOnlyList<string> Surfaces,
    DateTimeOffset DispatchedAt);
