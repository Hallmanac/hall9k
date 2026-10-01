using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Which project an admitted event belongs to, and which of that project's tasks if any — what
/// <see cref="OrchestratorFeedSelection"/> asks its caller for, because answering it means
/// reading documents and the selection itself reads nothing.
/// </summary>
/// <param name="OwnerFacts">
/// The facts card C's ownership rule judges for <see cref="TaskId"/>, read off the task's board row
/// the way the viewer's board reads them. Supplied only for a replicated event naming a task, the one
/// case the selection asks about ownership; null everywhere else, which the selection reads as "no
/// ownership opinion" rather than as an unknown owner.
/// </param>
/// <param name="PendingTakeRequesterRoot">
/// The owner root of whoever has a take request pending on <see cref="TaskId"/>, read off the same
/// board row; the viewer's board counts a task its root is waiting on as the viewer's, so the feed
/// does too.
/// </param>
public sealed record OrchestratorFeedScope(
    Guid ProjectId,
    Guid? TaskId,
    TaskOwnerFacts? OwnerFacts = null,
    string? PendingTakeRequesterRoot = null);
