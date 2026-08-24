using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Idea.Rendering;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Daemon.ProjectHomes;

/// <summary>One sweep's tally, for the loop's log line — see <c>CardPublicationLoop</c> for the pattern.</summary>
public sealed record ProjectHomeRenderSweepResult(
    int ProjectsInspected, int TasksRendered, int IdeasRendered, int OrphansHandled);

/// <summary>
/// Renders every task and idea in every project that has a home on this machine (backlog 48).
/// <para>
/// There is no per-event Wolverine handler here on purpose: this codebase's established shape for
/// daemon-side reactions to store state is a sweep — query the read models, act, repeat (the same
/// pattern <c>DispatchLoop</c> and <c>CardPublicationLoop</c> already use) — and a render is a pure
/// function of a document's current state, so a sweep is not an approximation of "on every event",
/// it computes exactly that: whatever changed since the last sweep produces different rendered
/// bytes, and <see cref="HomeEntryWriter"/> only touches disk when the bytes actually differ. The
/// same sweep run at daemon start is the reconciliation pass the acceptance criteria ask for —
/// there is no separate "first run" code path, because a full render already is one.
/// </para>
/// <para>
/// Best-effort throughout: a write failure for one task or one project is logged and skipped, never
/// thrown past the sweep, because the event that produced the state being rendered already
/// committed — a file the daemon could not write is not a reason to call the sweep itself failed,
/// and the next sweep tries again.
/// </para>
/// </summary>
public sealed class ProjectHomeRenderEngine(IDocumentStore store, ILogger<ProjectHomeRenderEngine> logger)
{
    public async Task<ProjectHomeRenderSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<ProjectDetails> projects = await query.Query<ProjectDetails>().ToListAsync(cancellationToken);

        int projectsInspected = 0;
        int tasksRendered = 0;
        int ideasRendered = 0;
        int orphansHandled = 0;

        foreach (ProjectDetails project in projects)
        {
            // A recorded home is a setting, not a guarantee: it may not be materialised on this
            // machine yet (h9k project init has not run here), and rendering into a directory that
            // does not exist would just recreate a bare tasks/ideas pair nothing else populated.
            if (!project.HomeDirectory.HasValue || !Directory.Exists(project.HomeDirectory.Value))
            {
                continue;
            }

            try
            {
                string home = project.HomeDirectory.Value;
                string tasksRoot = ProjectHomePaths.TasksDirectory(home);
                string ideasRoot = ProjectHomePaths.IdeasDirectory(home);
                Directory.CreateDirectory(tasksRoot);
                Directory.CreateDirectory(ideasRoot);

                IReadOnlyList<TaskDetails> tasks = await query.Query<TaskDetails>()
                    .Where(task => task.ProjectId == project.Id)
                    .ToListAsync(cancellationToken);
                IReadOnlyList<IdeaDetails> ideas = await query.Query<IdeaDetails>()
                    .Where(idea => idea.ProjectId == project.Id)
                    .ToListAsync(cancellationToken);

                projectsInspected++;

                HashSet<string> failedTaskShortIds = [];
                foreach (TaskDetails task in tasks)
                {
                    switch (RenderTask(tasksRoot, task, project.Name))
                    {
                        case RenderOutcome.Written:
                            tasksRendered++;
                            break;
                        case RenderOutcome.Failed:
                            failedTaskShortIds.Add(DomainId.Short(task.Id));
                            break;
                    }
                }

                HashSet<string> failedIdeaShortIds = [];
                foreach (IdeaDetails idea in ideas)
                {
                    switch (RenderIdea(ideasRoot, idea, project))
                    {
                        case RenderOutcome.Written:
                            ideasRendered++;
                            break;
                        case RenderOutcome.Failed:
                            failedIdeaShortIds.Add(DomainId.Short(idea.Id));
                            break;
                    }
                }

                orphansHandled += ReconcileOrphans(
                    tasksRoot, ideasRoot, tasks, ideas, project.Name, failedTaskShortIds, failedIdeaShortIds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One project's failure (an unwritable tasks/ideas directory, a stray non-directory
                // file at that path, a transient query error) must never stop the sweep from
                // reaching every other project — the doc comment above promises "a write failure
                // for one task or one project is logged and skipped, never thrown past the sweep."
                logger.LogWarning(exception,
                    "Project home sweep failed for project {Project}; a future sweep retries it", project.Name);
            }
        }

        return new ProjectHomeRenderSweepResult(projectsInspected, tasksRendered, ideasRendered, orphansHandled);
    }

    /// <summary>
    /// A render's disposition, distinguishing a genuine failure from "nothing changed" — both of
    /// which are "not rendered" from the caller's old boolean, but only one of them means the
    /// directory on disk may not match this entity's current desired name (see
    /// <see cref="ReconcileOrphans"/>).
    /// </summary>
    private enum RenderOutcome { Written, Unchanged, Failed }

    private RenderOutcome RenderTask(string tasksRoot, TaskDetails task, string projectName)
    {
        try
        {
            string directoryName = TaskDocumentRenderer.DirectoryName(task);
            string rendered = TaskDocumentRenderer.Render(task, projectName);
            bool changed = HomeEntryWriter.Write(tasksRoot, task.Id, directoryName, "task.md", rendered).Changed;
            return changed ? RenderOutcome.Written : RenderOutcome.Unchanged;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "task.md render failed for task {TaskId} in project {Project}; a future sweep retries it",
                DomainId.Short(task.Id), projectName);
            return RenderOutcome.Failed;
        }
    }

    private RenderOutcome RenderIdea(string ideasRoot, IdeaDetails idea, ProjectDetails project)
    {
        try
        {
            string directoryName = IdeaDocumentRenderer.DirectoryName(idea);
            string rendered = IdeaDocumentRenderer.Render(idea, project.Name);
            // Only true for an idea whose real discovery workspace lives under THIS project's
            // home (captured with this home already materialised, backlog 49) — an idea later
            // reassigned to a different project keeps its workspace at its original capture-time
            // home, so rendering it under the new project must not also create a
            // same-looking-but-inert workspace/ here, which would invite a human to drop research
            // material into a folder nothing ever reads.
            bool changed = HomeEntryWriter.Write(
                ideasRoot, idea.Id, directoryName, "idea.md", rendered,
                includeWorkspace: idea.WorkspaceHome == project.HomeDirectory).Changed;
            return changed ? RenderOutcome.Written : RenderOutcome.Unchanged;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "idea.md render failed for idea {IdeaId} in project {Project}; a future sweep retries it",
                DomainId.Short(idea.Id), project.Name);
            return RenderOutcome.Failed;
        }
    }

    private int ReconcileOrphans(
        string tasksRoot, string ideasRoot, IReadOnlyList<TaskDetails> tasks, IReadOnlyList<IdeaDetails> ideas,
        string projectName, IReadOnlySet<string> failedTaskShortIds, IReadOnlySet<string> failedIdeaShortIds)
    {
        try
        {
            // Matched by the entry's *current* directory name, not just its short-id prefix: a
            // slug rename that could not complete (HomeEntryWriter leaves the old name standing
            // when a directory already sits at both) must still be caught here, and a prefix match
            // would wrongly treat that stale duplicate as live because it shares an id with the one
            // directory that is actually current. That current name is only trustworthy for an
            // entity whose render succeeded *this* sweep, though: RenderTask/RenderIdea failing
            // (a transient IOException mid-Directory.Move) can leave the old-named directory
            // standing while this set only knows the new name, so failedTaskShortIds/
            // failedIdeaShortIds tell the reconciler which short ids to leave alone regardless of
            // name — the same entity, the same sweep, not yet safe to judge.
            HashSet<string> knownTaskDirectoryNames = [.. tasks.Select(TaskDocumentRenderer.DirectoryName)];
            HashSet<string> knownIdeaDirectoryNames = [.. ideas.Select(IdeaDocumentRenderer.DirectoryName)];
            return HomeEntryReconciler.RemoveOrMarkOrphans(
                    tasksRoot, knownTaskDirectoryNames, "task.md", failedTaskShortIds).Count
                + HomeEntryReconciler.RemoveOrMarkOrphans(
                    ideasRoot, knownIdeaDirectoryNames, "idea.md", failedIdeaShortIds).Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Orphan reconciliation failed for project {Project}", projectName);
            return 0;
        }
    }
}
