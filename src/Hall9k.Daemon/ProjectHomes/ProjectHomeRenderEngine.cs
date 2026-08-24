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

            projectsInspected++;
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

            foreach (TaskDetails task in tasks)
            {
                if (RenderTask(tasksRoot, task, project.Name))
                {
                    tasksRendered++;
                }
            }

            foreach (IdeaDetails idea in ideas)
            {
                if (RenderIdea(ideasRoot, idea, project.Name))
                {
                    ideasRendered++;
                }
            }

            orphansHandled += ReconcileOrphans(tasksRoot, ideasRoot, tasks, ideas, project.Name);
        }

        return new ProjectHomeRenderSweepResult(projectsInspected, tasksRendered, ideasRendered, orphansHandled);
    }

    private bool RenderTask(string tasksRoot, TaskDetails task, string projectName)
    {
        try
        {
            string directoryName = TaskDocumentRenderer.DirectoryName(task);
            string rendered = TaskDocumentRenderer.Render(task, projectName);
            return HomeEntryWriter.Write(tasksRoot, task.Id, directoryName, "task.md", rendered).Changed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "task.md render failed for task {TaskId} in project {Project}; a future sweep retries it",
                DomainId.Short(task.Id), projectName);
            return false;
        }
    }

    private bool RenderIdea(string ideasRoot, IdeaDetails idea, string projectName)
    {
        try
        {
            string directoryName = IdeaDocumentRenderer.DirectoryName(idea);
            string rendered = IdeaDocumentRenderer.Render(idea, projectName);
            return HomeEntryWriter.Write(ideasRoot, idea.Id, directoryName, "idea.md", rendered).Changed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "idea.md render failed for idea {IdeaId} in project {Project}; a future sweep retries it",
                DomainId.Short(idea.Id), projectName);
            return false;
        }
    }

    private int ReconcileOrphans(
        string tasksRoot, string ideasRoot, IReadOnlyList<TaskDetails> tasks, IReadOnlyList<IdeaDetails> ideas, string projectName)
    {
        try
        {
            HashSet<string> knownTaskIds = [.. tasks.Select(task => DomainId.Short(task.Id))];
            HashSet<string> knownIdeaIds = [.. ideas.Select(idea => DomainId.Short(idea.Id))];
            return HomeEntryReconciler.RemoveOrMarkOrphans(tasksRoot, knownTaskIds, "task.md").Count
                + HomeEntryReconciler.RemoveOrMarkOrphans(ideasRoot, knownIdeaIds, "idea.md").Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Orphan reconciliation failed for project {Project}", projectName);
            return 0;
        }
    }
}
