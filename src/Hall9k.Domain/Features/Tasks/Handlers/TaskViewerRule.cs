namespace Hall9k.Domain.Features.Tasks.Handlers;

/// <summary>
/// Whether a task is the viewer's own, for the surfaces that show a person their work and keep a
/// teammate's out of it. A task is the viewer's when <see cref="TaskOwnerRule"/> lets the viewer's
/// owner root act on it, which is the receive gate's own ownership rule and so the identical
/// answer the task commands give, or when a take request pending on it was made by the viewer's
/// root, since that task is one the viewer is waiting on. Every other task is a teammate's,
/// including one whose owner root cannot be resolved: an owner nobody can name is not the viewer.
/// </summary>
public static class TaskViewerRule
{
    public static TaskViewerCheck Decide(string viewerRoot, TaskOwnerFacts facts, string? pendingTakeRequesterRoot)
    {
        TaskOwnerCheck owner = TaskOwnerRule.Decide(viewerRoot, facts);
        bool askedByViewer = !string.IsNullOrEmpty(pendingTakeRequesterRoot) && pendingTakeRequesterRoot == viewerRoot;
        return new TaskViewerCheck(owner.MayAct || askedByViewer, owner);
    }
}

/// <summary>
/// What <see cref="TaskViewerRule.Decide"/> concluded. <see cref="Owner"/> is the underlying
/// ownership answer, which names the root a teammate's task belongs to when it is not the viewer's.
/// </summary>
public sealed record TaskViewerCheck(bool IsViewers, TaskOwnerCheck Owner);
