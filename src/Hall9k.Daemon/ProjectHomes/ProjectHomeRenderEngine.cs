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

                // Whether a Done task has reached true closeout needs a run it hangs on, not
                // just its own state (TaskDependencyQuery.IsClosedOut carries the same bar for
                // the dependency rule): a Done task's own record never changes again between
                // its pull request opening and the closeout monitor observing the merge, so
                // only the run projection can tell those two moments apart. Any of the task's
                // runs reaching RunCompleted counts — not only its current one (adversarial
                // review, backlog 51 cycle 2, the same reason BlockerHandoffQuery.ClosedOutRunsAsync
                // reads every run rather than just the current): a follow-up that closes out
                // before its own RunDispatched lands leaves CurrentRunId pointing at a run with
                // no projection at all, which would never archive if only the current run's state
                // counted. h9k task resolve's Failed-only attestation exit (Decisions Log #27) is
                // a separate case IsArchived checks for on its own (TaskDetails.ResolvedReason):
                // it ends the task Done specifically because no run of it will ever carry
                // RunCompleted, so no amount of searching every run finds one.
                Guid[] doneTaskIds = [.. tasks.Where(task => task.State == TaskState.Done).Select(task => task.Id)];
                HashSet<Guid> taskIdsWithCompletedRun = doneTaskIds.Length == 0
                    ? []
                    : (await query.Query<RunListItem>()
                            .Where(run => run.TaskId.IsOneOf(doneTaskIds))
                            .ToListAsync(cancellationToken))
                        // Filtered here rather than in the query itself: RunState is a value
                        // object behind a JsonConverter, and every other query in this codebase
                        // that needs to select on it server-side goes through MatchesSql against
                        // the raw JSON (DispatchEngine.LoadAsync, CloseoutEngine's watch queries)
                        // rather than a plain == Marten's LINQ provider can translate.
                        .Where(run => run.State == RunState.Completed)
                        .Select(run => run.TaskId)
                        .ToHashSet();
                // The current run's own state is still needed, for two other guards: an Abandoned
                // task's archiving is deferred while its current run is still live (adversarial
                // review, cycle 1: abandoning does not kill whatever process is running for it, no
                // daemon-side handler reacts to TaskAbandoned, so archiving unconditionally could
                // move a live run's runs/<run-id>/ out from under itself), and a task in any other
                // state whose directory currently sits under tasks/_archive/ (a reopen RunLauncher
                // dispatched straight there, ahead of this sweep ever moving it back) must not have
                // that directory moved back out while its run is still live. A run that has left its
                // actively-running states — parked awaiting a human, or waiting on closeout's own
                // retry budget — can still have a pull request opened onto its RunDirectory, or a
                // handoff read from it, once a human resolves the park (adversarial review, backlog
                // 51 cycles 2 and 4); what makes deferring on liveness alone safe again (cycle 5) is
                // that every daemon-side reader of a run's recorded RunDirectory
                // (ClaudeExecutor, PullRequestOpener, CloseoutEngine.ReadHandoffAsync, ReviewEngine)
                // now re-resolves where the directory actually sits (RunPaths.ResolveCurrentDirectory)
                // instead of trusting the value RunDispatched carried once at dispatch, so a parked
                // run finding its directory already moved back to tasks/ still finds its own files.
                // Every task's current run is fetched, not only Done/Abandoned's, because the second
                // guard applies regardless of state.
                Guid[] currentRunIds = [.. tasks.Select(task => task.CurrentRunId).OfType<Guid>()];
                Dictionary<Guid, RunState> currentRunStates = currentRunIds.Length == 0
                    ? []
                    : (await query.Query<RunListItem>()
                            .Where(run => run.Id.IsOneOf(currentRunIds))
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
                    bool archived = IsArchived(
                        task, taskIdsWithCompletedRun, currentRunStates, archivedTasksRoot, logger);
                    if (!archived
                        && CurrentRunMightStillTouchDirectory(task, currentRunStates)
                        && HomeEntryWriter.FindExistingDirectory(archivedTasksRoot, task.Id) is not null)
                    {
                        // The task's own directory is presently inside tasks/_archive/ — a reopen
                        // RunLauncher dispatched straight there via its alternate-root search, ahead
                        // of this sweep ever moving it back — and the run now writing into it is
                        // still live (or its projection has not caught up, which is the same "cannot
                        // prove it's safe" signal). Moving it back out from under an actively-running
                        // process is exactly the hazard the Abandoned branch above already guards
                        // against, generalized to every non-terminal state. Once the run leaves its
                        // live states — parked, or genuinely done with the directory — a later sweep
                        // moves it back on its own; a still-parked run's own daemon-side readers
                        // re-resolve the directory rather than trusting a stale recorded path (see
                        // the doc comment above), so this does not need to wait for the run's
                        // terminal state the way it once did (adversarial review, backlog 51 cycle 5).
                        archived = true;
                    }

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
    /// (2026-08-25, backlog 51): true closeout, or abandoned with nothing still live. True closeout
    /// is the same bar <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskDependencyQuery"/> uses
    /// for the dependency rule — Done alone is not enough, because <c>TaskCompleted</c> fires the
    /// moment the pull request opens, well before a human, Copilot, or the closeout monitor's own
    /// review loop is done with it. Only <c>RunCompleted</c>, appended once the closeout monitor
    /// observes the merge, means the story is actually over — and it is asked of every run the
    /// task ever had, not only its current one (adversarial review, backlog 51 cycle 2): a
    /// follow-up whose closeout lands before its own <c>RunDispatched</c> does leaves
    /// <c>CurrentRunId</c> naming a run with no projection at all, which would never archive if
    /// only the current run's own state counted.
    /// <para>
    /// <c>h9k task resolve</c>'s Failed-only attestation exit (Decisions Log #27) needs a second,
    /// run-independent signal rather than a broader run search: it ends the task Done specifically
    /// because the platform's own bookkeeping never will observe a merge for it ("the bookkeeping
    /// died"), so no run of this task will ever carry <c>RunCompleted</c> — <c>TaskResolved</c> IS
    /// the closure, on the human's attestation alone. <see cref="TaskDetails.ResolvedReason"/>,
    /// set only by that event and never cleared, is exactly that signal.
    /// </para>
    /// <para>
    /// Abandoned does not get the same free pass "run or no run" first gave it (adversarial
    /// review, cycle 1): a human abandoning a task with a live agent does not kill that agent —
    /// no daemon-side handler reacts to <c>TaskAbandoned</c> at all — so a task can sit Abandoned
    /// while its current run is still writing to <c>runs/&lt;run-id&gt;/</c>. Moving that directory
    /// out from under a live process is exactly the hazard the Done rule above already exists to
    /// avoid, so Abandoned waits on the same signal as
    /// <see cref="CurrentRunMightStillTouchDirectory"/>: no run at all, or the current run is still
    /// live (<see cref="RunState.IsLive"/>). A missing or not-yet-materialised run projection is
    /// treated as still-in-play rather than guessed safe, deferring the move to a future sweep
    /// instead of racing it.
    /// <para>
    /// The bar is liveness, not <see cref="RunState.IsTerminal"/> (reverted, adversarial review,
    /// backlog 51 cycle 5): a parked run — <c>ReviewParked</c>, <c>BudgetParked</c>,
    /// <c>CloseoutParked</c> — never runs again on its own, and requiring it to reach a terminal
    /// state before archiving left an Abandoned task whose current run sat permanently parked
    /// (nothing un-parks a run whose task nobody is running `pr resolve`/`review resolve` on any
    /// more) stranded at the top level of <c>tasks/</c> forever — the exact opposite of what this
    /// method exists to do. Cycle 4 broadened the bar to <c>IsTerminal</c> because
    /// <c>PullRequestOpener</c> and <c>CloseoutEngine</c> trusted a run's recorded
    /// <c>RunDirectory</c> as-is; now that every daemon-side reader of it re-resolves where the
    /// directory actually sits (see the sweep's own comment above this method), a parked run's
    /// files are found wherever the sweep has since moved them, so archiving the moment the run
    /// stops being live is safe again.
    /// </para>
    /// <para>
    /// One missing-projection case is knowable rather than merely deferred, though (adversarial
    /// review, backlog 51 cycle 3): a launch that dies before <c>RunDispatched</c> ever commits
    /// (a worktree checkout failure, say) has <see cref="RunLauncher"/> record it as
    /// <c>TaskFailed</c> directly — <c>RunLauncher.RecordLaunchFailureAsync</c> only appends
    /// <c>RunFailed</c> when the run's own stream already exists — so <c>TaskFailed</c> having named
    /// the task's *current* run (<see cref="TaskDetails.FailedRunId"/>) is proof that this exact run
    /// went through <c>TaskFailed</c>, and by the time that event lands the launch attempt that
    /// would have written <c>RunDispatched</c> has already returned. Checking
    /// <see cref="TaskDetails.FailureReason"/> alone is not enough (conformance and adversarial
    /// review, cycle 4): it survives a retry on purpose (<c>Apply(TaskRetried)</c>), so a task whose
    /// first run failed, was retried, and got abandoned again while its *second* run's
    /// <c>RunDispatched</c> was still in flight would otherwise read as "this run already recorded a
    /// launch failure" when the reason on file belongs to the first run, not the current one.
    /// <see cref="TaskDetails.FailedRunId"/> is compared against the current run id specifically so
    /// only a same-run failure counts. No later code path appends to that run id, so its projection
    /// provably never appears; this is the "sits at top level permanently" case reviewers found, and
    /// it archives with a diagnostic logged once — checked against disk, the same way
    /// <see cref="HomeEntryReconciler"/> avoids repeating its own log line forever — rather than
    /// every sweep for as long as the task remains Abandoned. An Abandoned task with no recorded
    /// failure for its current run is not this case — it may still be an in-flight launch racing the
    /// abandon — and keeps waiting exactly as before.
    /// </para>
    /// </summary>
    private static bool IsArchived(
        TaskDetails task, IReadOnlySet<Guid> taskIdsWithCompletedRun, IReadOnlyDictionary<Guid, RunState> currentRunStates,
        string archivedTasksRoot, ILogger logger)
    {
        if (task.State == TaskState.Done)
        {
            return taskIdsWithCompletedRun.Contains(task.Id) || task.ResolvedReason.IsNotBlank();
        }

        if (task.State == TaskState.Abandoned)
        {
            if (!CurrentRunMightStillTouchDirectory(task, currentRunStates))
            {
                return true;
            }

            if (task.CurrentRunId is { } currentRunId
                && !currentRunStates.ContainsKey(currentRunId)
                && task.FailedRunId == currentRunId)
            {
                // Logged once, checked against disk rather than kept in memory: the engine is a
                // pure function of the store's state on every sweep (this class's own doc comment),
                // so a flag in a field would contradict that shape, and the directory itself is the
                // record of whether an earlier sweep already reported this — the same reasoning
                // HomeEntryReconciler.Mark uses for its own orphan marker (adversarial review,
                // backlog 51 cycle 4).
                if (HomeEntryWriter.FindExistingDirectory(archivedTasksRoot, task.Id) is null)
                {
                    logger.LogWarning(
                        "Task {TaskId} is abandoned with current run {RunId} that never got a run projection "
                        + "and already recorded a launch failure for that same run — archiving; that run will never dispatch",
                        DomainId.Short(task.Id), DomainId.Short(currentRunId));
                }

                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Whether the run this task currently hangs on is still live (<see cref="RunState.IsLive"/>)
    /// or cannot yet be proven not to be — no run at all is the only case this returns false for
    /// without a run state to check, since nothing can write to or read from this task's directory
    /// when it has no current run. A missing or not-yet-materialised projection reads as still-live
    /// rather than guessed safe, deferring the move to a future sweep instead of racing it.
    /// <para>
    /// Liveness, not <see cref="RunState.IsTerminal"/> (reverted, adversarial review, backlog 51
    /// cycle 5 — cycle 4 had widened it to <c>IsTerminal</c> after finding that <c>PullRequestOpener</c>
    /// and <c>CloseoutEngine</c> could still touch a parked run's <c>RunDirectory</c> once a human
    /// resolved it, which made "no longer live" the wrong bar on its own). Requiring the run's own
    /// terminal state left a task whose current run sits permanently parked — nothing un-parks a
    /// run whose task nobody is running a resolve lever on any more — stranded and never archived
    /// (Abandoned) or stranded inside <c>tasks/_archive/</c> through its whole needs-you lifecycle
    /// (the reopen guard below). What makes liveness sufficient again is that every daemon-side
    /// reader of a run's recorded <c>RunDirectory</c> (<c>ClaudeExecutor</c>, <c>PullRequestOpener</c>,
    /// <c>CloseoutEngine.ReadHandoffAsync</c>, <c>ReviewEngine</c>) now re-resolves where the
    /// directory actually sits (<see cref="RunPaths.ResolveCurrentDirectory"/>) rather than trusting
    /// the value <c>RunDispatched</c> carried once at dispatch, so a parked run finding its
    /// directory already moved still finds its own files.
    /// </para>
    /// <para>
    /// Shared by the Abandoned archive guard above and the render loop's own guard against moving a
    /// task's directory back out of <c>tasks/_archive/</c> while the run that was dispatched
    /// straight into it (a reopen, <c>RunLauncher</c>'s alternate-root search, backlog 51) is still
    /// live — the same "cannot prove it's safe" caution, generalized past the Abandoned state that
    /// first needed it.
    /// </para>
    /// </summary>
    private static bool CurrentRunMightStillTouchDirectory(
        TaskDetails task, IReadOnlyDictionary<Guid, RunState> currentRunStates) =>
        task.CurrentRunId is { } currentRunId
        && (!currentRunStates.TryGetValue(currentRunId, out RunState? currentRunState) || currentRunState.IsLive);

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
