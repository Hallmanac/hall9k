using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Everything a display row is composed from, gathered once per command. Bundled rather than
/// passed as eight parameters because the set grew with the three surfaces (Decisions Log #66)
/// and will grow again when ranking facts arrive.
/// </summary>
/// <param name="Runs">Run documents by run id — the phase line's material.</param>
/// <param name="Activity">Stream telemetry by run id, which is how silence is measured.</param>
/// <param name="Projects">Project names by id.</param>
/// <param name="Owners">Owner names by id.</param>
/// <param name="NodeMachines">
/// Each node's machine name by node id. A run's session pid names a process in its own node's
/// process table, so this is what decides whether liveness is observable here at all.
/// </param>
/// <param name="Sessions">How liveness is observed; swapped in tests, which cannot spawn processes.</param>
/// <param name="MachineName">This machine's name, compared against <paramref name="NodeMachines"/>.</param>
/// <param name="Pressure">
/// What this node's dispatch sweep last measured about its own capacity (Decisions Log #64), or
/// null when nothing current was measured. The only thing that can say a queued row is waiting on
/// a slot rather than simply waiting: with no measurement, no surface claims one.
/// </param>
/// <param name="BudgetParkedRuns">
/// How many of the loaded rows are held on the same exhausted subscription window (Decisions Log
/// #40). Counted once for the whole set rather than recomputed per row, because that is what the
/// count is for: several budget parks are one condition, and a row that says so is a row a human
/// can read once and consciously leave alone.
/// </param>
internal sealed record TaskStatusContext(
    IReadOnlyDictionary<Guid, RunDetails> Runs,
    IReadOnlyDictionary<Guid, RunActivity> Activity,
    IReadOnlyDictionary<Guid, string> Projects,
    IReadOnlyDictionary<Guid, string> Owners,
    IReadOnlyDictionary<Guid, string> NodeMachines,
    ISessionObserver Sessions,
    string MachineName,
    DispatchPressure? Pressure = null,
    int BudgetParkedRuns = 0);

/// <summary>
/// The one truth about how a task reads. Every surface that shows a task — h9k status,
/// h9k task list, h9k project list's rollups, h9k project show, h9k task show — composes its
/// rows here rather than re-deriving the rules.
/// <para>
/// The board answers four questions with three surfaces (Decisions Log #66): the lifecycle
/// <see cref="LifecycleState"/>, the live <see cref="TaskPhase"/>, and the composed
/// <see cref="TaskAttention"/>, with a Published row's derived facts beside them. This type owns
/// the first and delegates the rest to <see cref="TaskPhaseComposer"/>,
/// <see cref="AttentionComposer"/>, and <see cref="PublishedFacts"/>.
/// </para>
/// </summary>
internal static class TaskStatusComposer
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromHours(1);

    /// <summary>
    /// Loads every task and composes its display row. The whole table is read because the
    /// rollups count across it; when task volume outgrows that, the bounding that list
    /// commands apply to their output is where server-side paging goes.
    /// </summary>
    public static async Task<IReadOnlyList<TaskStatusRow>> ComposeAllAsync(
        IQuerySession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return [];
        }

        TaskStatusContext context = await LoadContextAsync(session, tasks, now, cancellationToken);
        return [.. tasks.Select(task => Compose(task, context, now))];
    }

    /// <summary>
    /// One task's row, for the surfaces that show exactly one (h9k task show). The caller's
    /// detail document comes in with it because it can answer something the lean row cannot:
    /// see <see cref="RecordedFailureReason"/>.
    /// </summary>
    public static async Task<TaskStatusRow?> ComposeOneAsync(
        IQuerySession session, TaskDetails details, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TaskListItem? task = await session.LoadAsync<TaskListItem>(details.Id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        task.FailureReason = RecordedFailureReason(task, details);
        return Compose(task, await LoadContextAsync(session, [task], now, cancellationToken), now);
    }

    /// <summary>
    /// The failure reason this task actually recorded, taken from the detail document when the
    /// lean row cannot answer. <see cref="TaskListItem.FailureReason"/> arrived with the three
    /// surfaces (Decisions Log #66), so a row last written before that has no key for it and the
    /// attention line composes "the failure was recorded without a reason" — while
    /// <see cref="TaskDetails.FailureReason"/> has carried the text all along, including on the
    /// launch-failure path, which fails a task before the run stream exists and so leaves no run
    /// document to read it off either. <c>TaskLifecycleProjectionBackfill</c> repairs both at the
    /// next daemon start; until it runs, the reader is exactly the person inspecting a launch
    /// failure with no daemon up, and stating an absence the records contradict is the
    /// never-guess rule pointed the wrong way.
    /// <para>
    /// Both documents are projections of the same stream, so the substituted text is the reason
    /// the same <c>TaskFailed</c> would have written on the lean row, never a different one. The
    /// row is written to in memory only: this is a query session, which persists nothing.
    /// </para>
    /// </summary>
    private static string? RecordedFailureReason(TaskListItem task, TaskDetails details) =>
        task.State == TaskState.Failed && task.FailureReason.IsBlank()
            ? details.FailureReason
            : task.FailureReason;

    /// <summary>
    /// Everything the given rows read, in one pass. RunDetails rather than the leaner
    /// RunListItem because the phase line is composed from the run's records — its cycle, its
    /// failing checks, its unresolved threads, and above all the session it has in flight.
    /// <para>
    /// The run documents are loaded by id rather than swept, because RunDetails is the heaviest
    /// document the platform keeps and only the tasks' <em>current</em> runs compose anything;
    /// every other run a task has ever had is history that no row reads. The reference tables
    /// (projects, owners, nodes) are small and swept whole.
    /// </para>
    /// </summary>
    private static async Task<TaskStatusContext> LoadContextAsync(
        IQuerySession session,
        IReadOnlyList<TaskListItem> tasks,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid[] runIds = [.. tasks.Select(task => task.CurrentRunId).OfType<Guid>().Distinct()];
        Dictionary<Guid, string> projects = (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id, p => p.Name);
        Dictionary<Guid, string> owners = (await session.Query<OwnerDetails>().ToListAsync(cancellationToken))
            .ToDictionary(o => o.Id, o => o.Name);
        Dictionary<Guid, string> nodes = (await session.Query<NodeDetails>().ToListAsync(cancellationToken))
            .ToDictionary(n => n.Id, n => n.MachineName);
        Dictionary<Guid, RunDetails> runs = runIds.Length == 0
            ? []
            : (await session.LoadManyAsync<RunDetails>(cancellationToken, runIds)).ToDictionary(r => r.Id);
        Dictionary<Guid, RunActivity> activity = runIds.Length == 0
            ? []
            : (await session.LoadManyAsync<RunActivity>(cancellationToken, runIds)).ToDictionary(a => a.Id);

        return new TaskStatusContext(
            runs,
            activity,
            projects,
            owners,
            nodes,
            ProcessSessionObserver.Instance,
            Environment.MachineName,
            await DispatchPressure.ReadAsync(session, now, cancellationToken),
            runs.Values.Count(run => run.State == RunState.BudgetParked));
    }

    /// <summary>
    /// One row's three surfaces. The lifecycle state is composed first because it decides
    /// whether a phase applies at all; the session is observed once and shared by the phase and
    /// the attention line, so the two cannot disagree about whether anything is running.
    /// </summary>
    public static TaskStatusRow Compose(TaskListItem task, TaskStatusContext context, DateTimeOffset now)
    {
        RunDetails? run = task.CurrentRunId is { } runId ? context.Runs.GetValueOrDefault(runId) : null;
        LifecycleState state = State(task, run);

        bool onThisMachine = run is not null
            && context.NodeMachines.GetValueOrDefault(run.NodeId) == context.MachineName;
        SessionLiveness session = Observe(run, onThisMachine, context.Sessions);
        DispatchPressure? heldByCeiling = HeldByCeiling(task, context.Pressure);

        TaskPhase phase = TaskPhaseComposer.Compose(task, run, state, session, heldByCeiling);
        (bool stalled, string activity) = Silence(task, run, state, session, context, now);
        TaskAttention attention = AttentionComposer.Compose(
            task, run, state, phase, stalled, context.BudgetParkedRuns);
        AttentionBucket group = Group(task, run, state, attention, stalled);

        return new TaskStatusRow(
            task.Id,
            task.ProjectId,
            state,
            run?.State ?? RunState.Unknown,
            phase,
            attention,
            group,
            PublishedFacts.Compose(task, state, heldByCeiling),
            context.Projects.GetValueOrDefault(task.ProjectId) ?? "?",
            task.Objective,
            task.Type.Value,
            activity,
            task.PullRequestUrl ?? string.Empty,
            stalled,
            Priority(group, state),
            task.AddedAt,
            task.AssignedOwnerId is { } assignee ? context.Owners.GetValueOrDefault(assignee) ?? "?" : string.Empty,
            task.UnmetDependencies,
            task.DependencyFailureReason,
            heldByCeiling is not null,
            task.AssignedAt);
    }

    /// <summary>
    /// Whether the reason this task is not moving is one the platform actually measured: the node
    /// is at its concurrency ceiling and the task is in the queue that ceiling holds back
    /// (Decisions Log #64). Null covers both "the node has room" and "nothing current was
    /// measured", and every surface that reads it says nothing about slots in either case rather
    /// than inventing a contention nobody observed (AGENTS.md, the never-guess rule).
    /// <para>
    /// Read off the task's persisted state rather than the composed group, because the dispatcher
    /// reads that state too: every Queued task is deferred at the ceiling, including a closeout
    /// follow-up the monitor reopened, which the display groups under Delivered.
    /// </para>
    /// </summary>
    private static DispatchPressure? HeldByCeiling(TaskListItem task, DispatchPressure? pressure) =>
        task.State == TaskState.Queued && pressure is { AtCeiling: true }
            ? pressure
            : null;

    /// <summary>
    /// What can honestly be said about the sessions a run has in flight — plural, because a
    /// review cycle dispatches one pass per active track and both read at once (Decisions Log
    /// #59). A run is only reported as having lost its process when every process it records is
    /// gone: a cycle whose adversarial pass has already exited while its conformance pass reads
    /// on is a healthy run, and calling it a dead one is the incident this surface exists to
    /// prevent, told backwards. Where the passes disagree the more cautious reading wins —
    /// unobserved outranks gone, because a pid this machine cannot answer for is not evidence
    /// of a death.
    /// </summary>
    private static SessionLiveness Observe(RunDetails? run, bool onThisMachine, ISessionObserver observer)
    {
        if (run is null || run.ActiveSessions.Count == 0)
        {
            return SessionLiveness.NotApplicable;
        }

        SessionLiveness[] observed = [.. run.ActiveSessions.Select(
            session => observer.Observe(session.ProcessId, session.StartedAt, onThisMachine))];
        if (observed.Contains(SessionLiveness.Alive))
        {
            return SessionLiveness.Alive;
        }

        // Every recorded session answered, and none of them is there. NotApplicable cannot
        // reach here: a recorded session always carries a pid, so the observer only ever
        // returns it for the no-session case handled above.
        return observed.Contains(SessionLiveness.Unobserved)
            ? SessionLiveness.Unobserved
            : SessionLiveness.Gone;
    }

    /// <summary>
    /// Where the work is, in the seven words a human reads (Decisions Log #66). Nothing here is
    /// persisted: Queued, Blocked and Claimed keep their streams untouched and are shown as the
    /// lifecycle state they amount to, with the distinction moved onto the derived-facts line
    /// (<see cref="PublishedFacts"/>) or the phase line.
    /// <para>
    /// The one rule worth stating out loud is Done. It renders only at <em>true</em> closeout —
    /// the merge observed — which is exactly the bar the dependency rule uses (TASK-MODEL.md
    /// §2.3). Everything between the push and that observation is Delivered, including the
    /// window the old display called Done while the pull request was still open and follow-up
    /// runs were still going (the origin confusion, task 17).
    /// </para>
    /// </summary>
    private static LifecycleState State(TaskListItem task, RunDetails? run)
    {
        bool pushed = task.PullRequestUrl.IsNotBlank();
        return State(task.State, pushed, Closed(run, pushed));
    }

    /// <summary>
    /// The same word for a task seen from a dependent's side, so the blocker list on
    /// <c>h9k task show</c> and <c>h9k task assign</c> spells a blocker exactly as that blocker's
    /// own row does. Closeout is read from the dependency rule itself
    /// (<see cref="TaskDependency.IsClosedOut"/>, the merge the closeout monitor observed), which
    /// is the point: a blocker whose pull request is open reads Delivered on both screens instead
    /// of claiming Done beside the mark that says it is still holding the dependent back.
    /// <para>
    /// A blocker that never pushed is closed out for display purposes for the reason
    /// <see cref="Closed"/> gives — there was never a merge to observe — even though the
    /// dependency rule still refuses it. That disagreement is the dependency rule's, not the
    /// display's, and the mark beside the word plus the recorded death reason are what say so.
    /// </para>
    /// </summary>
    public static LifecycleState State(TaskDependency dependency)
    {
        bool pushed = dependency.PullRequestUrl.IsNotBlank();
        return State(dependency.State, pushed, !pushed || dependency.IsClosedOut);
    }

    /// <summary>
    /// The mark the word above depends on, three answers rather than two: a blocker that will
    /// never close out reads differently from one that simply has not yet, because only one of
    /// them needs a human (Decisions Log #34). It lives beside <see cref="State(TaskDependency)"/>
    /// because that method's honesty rests on it — a blocker that never pushed is spelt Done there
    /// while the dependency rule still refuses it, and the mark is the whole of what says so, so a
    /// screen that prints the word without the mark prints a contradiction. Origin incident
    /// (2026-08-22, pre-PR review cycle 4): <c>h9k task assign</c> listed a hand-resolved blocker
    /// as "(Done)" directly under the sentence saying it had not closed out.
    /// </summary>
    public static string DependencyMark(TaskDependency dependency) => dependency switch
    {
        { Blocks: false } => "[green]closed out[/]",
        { IsDead: true } => "[red]never closes out[/]",
        _ => "[yellow]waiting[/]",
    };

    /// <summary>
    /// The mapping itself, over the two facts every caller can answer: whether the work was
    /// pushed, and whether its closeout was actually observed.
    /// </summary>
    private static LifecycleState State(TaskState state, bool pushed, bool closedOut)
    {
        return state.Value switch
        {
            "Draft" => LifecycleState.Draft,
            // The three pre-dispatch states are one word on screen; which of them it is, is a
            // fact on the line below. A reopened task carrying a pull request is a follow-up in
            // flight, which is Delivered work rather than work waiting to start.
            "Published" or "Queued" or "Blocked" => pushed ? LifecycleState.Delivered : LifecycleState.Published,
            "Claimed" or "NeedsHuman" => pushed ? LifecycleState.Delivered : LifecycleState.Working,
            // Merged, or closed with nothing to watch: the story is over either way. A pull
            // request closed without merging is deliberately NOT Done — closeout ended, but not
            // the way Done claims, and the attention line says so.
            "Done" => closedOut ? LifecycleState.Done : LifecycleState.Delivered,
            "Failed" => LifecycleState.Failed,
            // Backlog 33 renames the persisted word; until then both spellings show as Archived,
            // and after it lands nothing here has to change.
            "Abandoned" or "Archived" => LifecycleState.Archived,
            _ => LifecycleState.Unknown,
        };
    }

    /// <summary>
    /// Whether the closeout the task claims is actually over. A merge observation is what counts
    /// (log #34's dependency rule, the same bar); a run that reached Completed carries that
    /// observation, and a task closed with no pull request at all had nothing to observe.
    /// <para>
    /// The no-pull-request answer is given before the run is read at all, because a task closed
    /// by hand keeps the run document it was closed on top of: <c>TaskResolved</c> does not clear
    /// <c>CurrentRunId</c>, so <c>h9k task resolve &lt;id&gt; --reason "…"</c> with no --pr leaves
    /// the failed run's document in place. Reading that run's state would render a task that never
    /// pushed anything as Delivered, which claims a push and a pending merge that never existed.
    /// </para>
    /// </summary>
    private static bool Closed(RunDetails? run, bool pushed) => (run, pushed) switch
    {
        (_, false) => true,
        ({ PullRequestMergedAt: not null }, _) => true,
        ({ State.Value: "Completed" }, _) => true,
        _ => false,
    };

    /// <summary>
    /// Whether live work has gone quiet, and the age string the pane prints beside it. Two
    /// different silences count: a stream that stopped producing, and a session the machine says
    /// is gone while the run still believes in it — the second is the one that would have caught
    /// the 2026-08-22 incident from the other side.
    /// </summary>
    private static (bool Stalled, string Activity) Silence(
        TaskListItem task,
        RunDetails? run,
        LifecycleState state,
        SessionLiveness session,
        TaskStatusContext context,
        DateTimeOffset now)
    {
        if (run is null || !Live(task, run, state))
        {
            return (false, string.Empty);
        }

        if (session == SessionLiveness.Gone)
        {
            return (true, "process gone");
        }

        if (!context.Activity.TryGetValue(run.Id, out RunActivity? activity))
        {
            return (false, string.Empty);
        }

        TimeSpan silence = now - activity.LastActivityAt;
        return (silence > StallThreshold, RelativeAge(silence));
    }

    /// <summary>
    /// Whether silence means anything on this row. It only does where the platform owns the next
    /// move: a run the daemon believes is executing right now. A parked run and an agent that
    /// asked a question are quiet <em>by design</em> — their last activity stamp freezes at the
    /// park, because nothing writes to the stream while a human holds the worktree — so measuring
    /// them against the stall threshold turns every such row red-and-then-Stalled an hour later,
    /// which both misfiles it out of the Needs-you count and renames a wait for a human as a
    /// machine failure.
    /// </summary>
    private static bool Live(TaskListItem task, RunDetails run, LifecycleState state) =>
        (state == LifecycleState.Working || state == LifecycleState.Delivered)
        && task.State == TaskState.Claimed
        && run.State != RunState.ReviewParked
        && run.State != RunState.CloseoutParked
        // A budget park is quiet by design for the same reason (Decisions Log #40): the session
        // that hit the limit has exited and nothing writes to the stream until the retry sweep
        // resumes it, so measuring it against the stall threshold would rename an automatic wait
        // as a machine failure an hour later.
        && run.State != RunState.BudgetParked;

    /// <summary>
    /// The coarse bucket the rollups count and h9k status groups by. Single assignment on
    /// purpose: every task lands in exactly one, so a project's counts sum to its tasks.
    /// The groups follow the lifecycle vocabulary now, with the two that are really questions
    /// about attention — needs-you and stalled — taken first, because that is the order a human
    /// reads the board in.
    /// </summary>
    private static AttentionBucket Group(
        TaskListItem task, RunDetails? run, LifecycleState state, TaskAttention attention, bool stalled)
    {
        if (stalled)
        {
            return AttentionBucket.Stalled;
        }

        // A Delivered row asking only for its merge is still Delivered work: the attention column
        // says it wants you, and moving every open pull request out of its lifecycle group would
        // empty that group on most boards. That is the whole of the exception — a park, an
        // unanswered question or a pull request nothing is watching any more has genuinely
        // stopped, and belongs in the group a reader scans for work that needs them.
        if (attention.NeedsYou && !AwaitingMerge(state, run))
        {
            return AttentionBucket.NeedsYou;
        }

        return state.Word switch
        {
            "Working" => AttentionBucket.Working,
            "Delivered" => AttentionBucket.Delivered,
            "Failed" => AttentionBucket.NeedsYou,
            "Done" => AttentionBucket.Done,
            "Draft" => AttentionBucket.Draft,
            "Published" => task.State.Value switch
            {
                "Queued" => AttentionBucket.Queued,
                "Blocked" => AttentionBucket.Blocked,
                _ => AttentionBucket.Ready,
            },
            _ => AttentionBucket.Closed,
        };
    }

    /// <summary>
    /// A Delivered row whose only outstanding ask is the merge itself: the pull request is open,
    /// the closeout monitor is still watching it, and nothing has been recorded against it.
    /// </summary>
    private static bool AwaitingMerge(LifecycleState state, RunDetails? run) =>
        state == LifecycleState.Delivered && run?.State == RunState.AwaitingReview;

    /// <summary>
    /// Attention-first ordering: what needs a human, then what has gone quiet, then what is
    /// working, then what is waiting on someone else, then the settled rows - and inside a
    /// group, whichever row is holding the most still.
    /// <para>
    /// The group is the major key and the row's own standing the minor one, because the pane
    /// sorts <em>inside</em> a single section: a rank read off the group alone is the same
    /// number for every row it is ever asked to compare, which is no ordering at all.
    /// </para>
    /// </summary>
    private static int Priority(AttentionBucket group, LifecycleState state) =>
        (GroupRank(group) * 10) + RankWithinGroup(group, state);

    /// <summary>
    /// Where a row sits among the others asking the same thing. Only Needs-you separates today:
    /// a park, an unanswered question or a held dependency has a worktree stopped behind it and
    /// nothing moves until it is answered, while a failed task is a decision a human can take
    /// whenever they reach it (Decisions Log #27). Every other group is one kind of wait, so its
    /// rows fall through to the attention level and then to age.
    /// </summary>
    private static int RankWithinGroup(AttentionBucket group, LifecycleState state) =>
        group == AttentionBucket.NeedsYou && state == LifecycleState.Failed ? 1 : 0;

    /// <summary>Which group of rows is read first, in the order the sections are printed.</summary>
    private static int GroupRank(AttentionBucket group) => group switch
    {
        AttentionBucket.NeedsYou => 0,
        AttentionBucket.Stalled => 1,
        AttentionBucket.Working => 2,
        AttentionBucket.Delivered => 3,
        AttentionBucket.Queued => 4,
        // The development states rank below dispatched work: they are not waiting on the
        // platform, they are waiting on a human to finish thinking (Blocked is waiting on
        // another task, which is closer to running than either).
        AttentionBucket.Blocked => 5,
        AttentionBucket.Ready => 6,
        AttentionBucket.Draft => 7,
        AttentionBucket.Done => 8,
        _ => 9,
    };

    /// <summary>How long ago, in the resolution a human actually reads at a glance.</summary>
    public static string RelativeAge(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 90 } => $"{(int)elapsed.TotalMinutes}m ago",
        { TotalHours: < 36 } => $"{(int)elapsed.TotalHours}h ago",
        _ => $"{(int)elapsed.TotalDays}d ago",
    };
}
