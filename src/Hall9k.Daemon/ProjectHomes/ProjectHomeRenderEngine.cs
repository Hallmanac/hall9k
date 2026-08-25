using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Idea.Rendering;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
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
        // Fetched once, in memory, rather than per project with a WorkspaceHome filter in the
        // query itself: WorkspaceHome is a value object behind a JsonConverter (backlog 49), and
        // an equality filter on that shape is safest evaluated in C# rather than trusted to
        // Marten's LINQ-to-JSON translation. Ideas are few and small (IdeaDetails' own doc
        // comment), so one unfiltered fetch here costs nothing a per-project query would have saved.
        IReadOnlyList<IdeaDetails> allIdeas = await query.Query<IdeaDetails>().ToListAsync(cancellationToken);

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
                string archivedTasksRoot = ProjectHomePaths.ArchivedTasksDirectory(home);
                string ideasRoot = ProjectHomePaths.IdeasDirectory(home);
                Directory.CreateDirectory(tasksRoot);
                Directory.CreateDirectory(ideasRoot);

                IReadOnlyList<TaskDetails> tasks = await query.Query<TaskDetails>()
                    .Where(task => task.ProjectId == project.Id)
                    .ToListAsync(cancellationToken);
                IReadOnlyList<IdeaDetails> ideas = [.. allIdeas.Where(idea => idea.ProjectId == project.Id)];

                // Whether a Done task has reached true closeout needs the run it hangs on, not
                // just its own state (TaskDependencyQuery.IsClosedOut carries the same bar for
                // the dependency rule): a Done task's own record never changes again between
                // its pull request opening and the closeout monitor observing the merge, so
                // only the run projection can tell those two moments apart.
                Guid[] doneRunIds = [.. tasks
                    .Where(task => task.State == TaskState.Done && task.CurrentRunId.HasValue)
                    .Select(task => task.CurrentRunId!.Value)];
                Dictionary<Guid, RunState> currentRunStates = doneRunIds.Length == 0
                    ? []
                    : (await query.Query<RunListItem>()
                            .Where(run => run.Id.IsOneOf(doneRunIds))
                            .ToListAsync(cancellationToken))
                        .ToDictionary(run => run.Id, run => run.State);
                // An idea reassigned away from this project keeps its real, capture-time
                // workspace here permanently (backlog 49: assignment never retroactively
                // relocates an already-materialised workspace) even though it no longer renders
                // idea.md under this home. Its directory name still has to reach
                // ReconcileOrphans as "known", or the very next sweep reads this project's
                // ideas list, does not find it, and treats the one true copy of that idea's
                // research as an orphan to delete or mark (adversarial review, cycle 4).
                IReadOnlyList<IdeaDetails> ideasAnchoredHereButOwnedElsewhere = [.. allIdeas.Where(idea =>
                    idea.ProjectId != project.Id
                    && ProjectHomePaths.SameDirectory(idea.WorkspaceHome.Value, project.HomeDirectory.Value))];

                projectsInspected++;

                HashSet<string> failedTaskShortIds = [];
                HashSet<string> liveTaskDirectoryNames = [];
                HashSet<string> archivedTaskDirectoryNames = [];
                foreach (TaskDetails task in tasks)
                {
                    bool archived = IsArchived(task, currentRunStates);
                    string directoryName = TaskDocumentRenderer.DirectoryName(task);
                    (archived ? archivedTaskDirectoryNames : liveTaskDirectoryNames).Add(directoryName);

                    switch (RenderTask(tasksRoot, archivedTasksRoot, task, archived, project.Name))
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
                    tasksRoot, archivedTasksRoot, ideasRoot, liveTaskDirectoryNames, archivedTaskDirectoryNames,
                    ideas, ideasAnchoredHereButOwnedElsewhere, project.Name, failedTaskShortIds, failedIdeaShortIds);
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

    /// <summary>
    /// Whether a task's directory belongs under <c>tasks/_archive/</c> rather than <c>tasks/</c>
    /// (2026-08-25, backlog 51): true closeout, or abandoned. True closeout is the same bar
    /// <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskDependencyQuery"/> uses for the
    /// dependency rule — Done alone is not enough, because <c>TaskCompleted</c> fires the moment
    /// the pull request opens, well before a human, Copilot, or the closeout monitor's own review
    /// loop is done with it. Only <c>RunCompleted</c>, appended once the closeout monitor observes
    /// the merge, means the story is actually over. Abandoned archives unconditionally: a human
    /// walked away, whether or not the task ever claimed a run.
    /// </summary>
    private static bool IsArchived(TaskDetails task, IReadOnlyDictionary<Guid, RunState> currentRunStates) =>
        task.State == TaskState.Abandoned
        || (task.State == TaskState.Done
            && task.CurrentRunId is { } runId
            && currentRunStates.TryGetValue(runId, out RunState? runState)
            && runState == RunState.Completed);

    private RenderOutcome RenderTask(
        string tasksRoot, string archivedTasksRoot, TaskDetails task, bool archived, string projectName)
    {
        try
        {
            string directoryName = TaskDocumentRenderer.DirectoryName(task);
            string rendered = TaskDocumentRenderer.Render(task, projectName);
            string targetRoot = archived ? archivedTasksRoot : tasksRoot;
            string alternateRoot = archived ? tasksRoot : archivedTasksRoot;
            bool changed = HomeEntryWriter.Write(
                targetRoot, task.Id, directoryName, "task.md", rendered, alternateRoots: [alternateRoot]).Changed;
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
                includeWorkspace: ProjectHomePaths.SameDirectory(
                    idea.WorkspaceHome.Value, project.HomeDirectory.Value)).Changed;
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
        string tasksRoot, string archivedTasksRoot, string ideasRoot,
        IReadOnlySet<string> liveTaskDirectoryNames, IReadOnlySet<string> archivedTaskDirectoryNames,
        IReadOnlyList<IdeaDetails> ideas, IReadOnlyList<IdeaDetails> ideasAnchoredHereButOwnedElsewhere,
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
            //
            // tasks/ itself now holds one directory that is not a task at all — tasks/_archive/,
            // where a terminal task's directory actually lives (2026-08-25, backlog 51). It is
            // added to the live set unconditionally so this pass never judges it against
            // IsOnlyGeneratedContent (which would delete it the moment it's empty) or marks it
            // ORPHANED.md; the archive root gets its own reconciliation pass below, against the
            // terminal tasks that actually belong there, exactly like the live root's.
            HashSet<string> knownTaskDirectoryNames = [.. liveTaskDirectoryNames, ProjectHomePaths.ArchiveDirectoryName];
            // Also known: ideas reassigned away from this project whose real workspace still
            // lives here (ideasAnchoredHereButOwnedElsewhere) — not rendered under this project
            // any more, but their on-disk directory is the idea's one true home and must survive
            // orphan reconciliation exactly like a currently-owned idea's does. Resolved through
            // HomeEntryLookup rather than IdeaDocumentRenderer.DirectoryName: nothing renames this
            // directory once the idea is reassigned away, so a later revise changes the idea's slug
            // without ever touching the directory's actual name, and recomputing the "known" name
            // from the idea's current text would then name a directory that does not exist while
            // the real one — still sitting at its last-rendered-while-owned name — reads as an
            // orphan (adversarial review, cycle 5). The id-marker lookup finds the directory that
            // is actually there, however it is actually named.
            HashSet<string> knownIdeaDirectoryNames = [
                .. ideas.Select(IdeaDocumentRenderer.DirectoryName),
                .. ideasAnchoredHereButOwnedElsewhere
                    .Select(idea => Path.GetFileName(HomeEntryLookup.FindExistingDirectory(ideasRoot, idea.Id)))
                    .OfType<string>()];
            return HomeEntryReconciler.RemoveOrMarkOrphans(
                    tasksRoot, knownTaskDirectoryNames, "task.md", failedTaskShortIds).Count
                + HomeEntryReconciler.RemoveOrMarkOrphans(
                    archivedTasksRoot, archivedTaskDirectoryNames, "task.md", failedTaskShortIds).Count
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
