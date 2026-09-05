namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A human's recorded go-ahead at one of interactive mode's routine phase boundaries (task:
/// interactive mode becomes a recorded property of the task, design rulings R2, R5, R9) — the
/// bare-proceed sibling of <see cref="ReviewParkResolved"/>: there is no verdict to argue with
/// here, only permission to continue exactly where the park interrupted the loop
/// (<see cref="RunAggregate.ParkedFromReviewPhase"/>, <see cref="RunAggregate.ParkedFromState"/>).
/// <c>h9k review resolve --merge-ready</c>/<c>--needs-fixes</c> remains the lever for a boundary
/// the human wants to redirect instead of merely approve — existing levers keep their exact
/// meaning (design ruling R9) — so this event only ever fires from <c>h9k review proceed</c>,
/// never from any automated engine code. Only accepted against a run whose current park is
/// interactive mode's own (<see cref="RunAggregate.ParkedIsInteractiveGate"/>): a disputed or
/// cap/budget park still takes only <see cref="ReviewParkResolved"/>.
/// </summary>
public sealed record ReviewBoundaryApproved(Guid Id, DateTimeOffset ApprovedAt, Guid ApprovedByOwnerId);
