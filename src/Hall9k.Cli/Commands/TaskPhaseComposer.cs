using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Composes the phase line (Decisions Log #66): what the machinery is doing right now, from the
/// run's own records plus one observation of the recorded process. Derived only — no new events
/// — because the liveness half can only ever be observed, never replayed.
/// <para>
/// This is where the run-level vocabulary lives. RunState's words (Dispatched, UnderReview,
/// ChecksFailing, …) are this line's material and are never printed in the Status column, which
/// is how the board stopped answering four questions with one field.
/// </para>
/// </summary>
internal static class TaskPhaseComposer
{
    /// <summary>
    /// The phase for one row, or <see cref="TaskPhase.None"/> when the lifecycle state has no
    /// live machinery behind it (a draft, a published task, a closed one).
    /// </summary>
    /// <param name="task">The task as the lifecycle projection records it.</param>
    /// <param name="run">Its current run, or null when the claim's run document has not appeared yet.</param>
    /// <param name="state">The composed lifecycle state, which decides whether a phase applies at all.</param>
    /// <param name="session">What was observed of the run's recorded session.</param>
    /// <param name="heldByCeiling">
    /// The measurement that says a queued follow-up is waiting on a dispatch slot, or null when
    /// nothing measured one (<see cref="DispatchPressure"/>, Decisions Log #64).
    /// </param>
    public static TaskPhase Compose(
        TaskListItem task,
        RunDetails? run,
        LifecycleState state,
        SessionLiveness session,
        DispatchPressure? heldByCeiling = null)
    {
        if (state == LifecycleState.Working)
        {
            return Working(task, run, session);
        }

        return state == LifecycleState.Delivered
            ? Delivered(task, run, session, heldByCeiling)
            : TaskPhase.None;
    }

    /// <summary>
    /// A run that has not pushed yet: build, gates, and the pre-PR review loop. The review leg
    /// is where the phase earns its keep — a run sits in UnderReview while a reviewer reads,
    /// while a fix session edits the worktree, and while nothing at all is running, and only the
    /// recorded session says which.
    /// </summary>
    private static TaskPhase Working(TaskListItem task, RunDetails? run, SessionLiveness session)
    {
        // A claim whose run document has not committed yet: the dispatch handoff, mid-step.
        if (run is null)
        {
            return new TaskPhase("dispatch handoff", SessionLiveness.NotApplicable,
                "the run record has not appeared yet");
        }

        if (task.State == TaskState.NeedsHuman)
        {
            return new TaskPhase("paused for your answer", session, "the session exited to wait");
        }

        // The blocker-context session is dispatched inside the launch itself, before the run's
        // own process starts (BlockerContextAssembler, then RunProcessStarted), so the run is
        // still Dispatched while it reads. Only the recorded session's role names that work: the
        // run state alone would call a live condensing pass worktree preparation.
        if (ActiveRole(run) == AgentRole.Synthesis)
        {
            return new TaskPhase("condensing blocker context", session, "context synthesis running");
        }

        return run.State.Value switch
        {
            "Dispatched" => new TaskPhase("starting up", session, "worktree and prompt being prepared"),
            "Running" => new TaskPhase("building", session, SessionGap(session)),
            // The gates run inside the daemon's own process, so there is no agent session to
            // observe and the line says nothing about one.
            "Verifying" => new TaskPhase("gates", SessionLiveness.NotApplicable, "build and test running"),
            "UnderReview" => Review(run, session),
            "ReviewParked" => new TaskPhase("review parked", SessionLiveness.NotApplicable,
                "the worktree is yours until you resolve it"),
            // Parked on the clock rather than on a person (backlog 40). The session that
            // hit the limit has already exited, so the line says nothing about liveness and
            // names the wait itself: the retry sweep is what ends it, not a human.
            "BudgetParked" => new TaskPhase("waiting on the budget window", SessionLiveness.NotApplicable,
                "the daemon retries hourly; nothing is running"),
            // The run ended and the task's own transition has not committed yet: the closing
            // half of the dispatch handoff, and a lane nothing is working in.
            "Completed" or "Failed" or "Killed" or "Superseded" => new TaskPhase(
                $"run {run.State.Value.ToLowerInvariant()}", SessionLiveness.NotApplicable,
                "the task's own transition has not landed yet"),
            _ => new TaskPhase("working", session),
        };
    }

    /// <summary>
    /// Which round of review, and which leg of it. The cycle cap is the daemon's configuration
    /// (DaemonOptions.MaxComplianceReviewCycles / MaxAdversarialReviewCycles) and the CLI cannot
    /// read it, so the line says which cycle the run is on and stops rather than printing an
    /// "of N" it would have to guess.
    /// </summary>
    private static TaskPhase Review(RunDetails run, SessionLiveness session)
    {
        string cycle = run.ReviewCycle > 0 ? $"review cycle {run.ReviewCycle}" : "review";
        return ActiveRole(run).Value switch
        {
            "Fix" => new TaskPhase(cycle, session, "fix session running"),
            "Review" => new TaskPhase(cycle, session, LensesReading(run)),
            // Nothing is recorded as running: between passes, or a run whose document predates
            // session recording. Either way the honest reading is that no session was observed.
            _ => new TaskPhase(cycle, SessionLiveness.NotApplicable, "no session recorded as running"),
        };
    }

    /// <summary>
    /// What leg of the run its sessions are on. A cycle's review passes share a role and every
    /// other role runs alone, so the first recorded session names the leg; a run with nothing in
    /// flight is Unknown, which is the honest reading of "nothing is running".
    /// </summary>
    private static AgentRole ActiveRole(RunDetails run) =>
        run.ActiveSessions.Count > 0 ? run.ActiveSessions[0].Role : AgentRole.Unknown;

    /// <summary>
    /// Which lenses still have a pass out (Decisions Log #59). "adversarial pending" is the
    /// difference between a cycle nobody is working on and a cycle waiting on its slower track.
    /// </summary>
    private static string LensesReading(RunDetails run)
    {
        ReviewLens[] reading = [.. run.ActiveSessions
            .Where(session => session.Role == AgentRole.Review)
            .Select(session => session.Lens)];
        string[] named = [.. reading
            .Where(lens => lens != ReviewLens.Unknown)
            .Select(lens => lens.Value.ToLowerInvariant())];
        return named.Length switch
        {
            0 when reading.Length > 0 => "a review pass is reading (its lens was not recorded)",
            0 => "a review pass is reading",
            _ => $"{string.Join(" and ", named)} pending",
        };
    }

    /// <summary>
    /// The work is pushed and the merge has not been observed. Two very different things live
    /// here and the phase is what tells them apart (origin incident, 2026-08-22, PR 24):
    /// a follow-up run driving the pull request, versus a pull request with nothing left on it
    /// but a human's merge.
    /// </summary>
    private static TaskPhase Delivered(
        TaskListItem task, RunDetails? run, SessionLiveness session, DispatchPressure? heldByCeiling)
    {
        string pullRequest = PullRequestLabel(task, run);

        // A reopened task is a follow-up in flight (or about to be): the machinery owns the
        // next move, not the reader. What it is waiting on is a measurement or nothing — the
        // ceiling line when this node's last sweep reported itself full, and silence otherwise,
        // because a queue that is not moving has many causes and the display observed none of
        // them (Decisions Log #64, AGENTS.md's never-guess rule).
        if (task.State == TaskState.Queued)
        {
            return new TaskPhase($"follow-up queued for {pullRequest}", SessionLiveness.NotApplicable,
                heldByCeiling?.ReasonLine ?? "not claimed yet");
        }

        // A reopened follow-up held by a dependency: nothing is dispatching it and no run is
        // watching the pull request, so the line says what it is waiting on. Answered before the
        // run is read at all, because TaskAssigned does not clear CurrentRunId — reading the run
        // here would describe the previous run's ending as this row's phase. Grouped with the
        // claimed states below, it composed Working's dispatch-handoff line instead and asserted
        // a handoff the platform never made (pre-PR review, 2026-08-22).
        if (task.State == TaskState.Blocked)
        {
            return new TaskPhase($"follow-up blocked for {pullRequest}", SessionLiveness.NotApplicable,
                BlockedDetail(task));
        }

        if (task.State == TaskState.Claimed || task.State == TaskState.NeedsHuman)
        {
            TaskPhase working = Working(task, run, session);
            return working with { Text = $"follow-up on {pullRequest}: {working.Text}" };
        }

        if (run is null)
        {
            return new TaskPhase($"{pullRequest} open", SessionLiveness.NotApplicable,
                "no run record is watching it");
        }

        return run.State.Value switch
        {
            // A checks-or-threads finding would already have moved the run off AwaitingReview
            // (ChecksFailing, ReviewPending), so what distinguishes one AwaitingReview row from
            // another here is only the post-PR review watcher's own read of Copilot: landed,
            // requested but still pending, or neither observed yet (origin: PR #50 sat Delivered
            // for 23 minutes with a landed Copilot review nobody had read before the merge).
            "AwaitingReview" => AwaitingReviewPhase(pullRequest, run),
            "ChecksFailing" => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable,
                ChecksDetail(run)),
            "ReviewPending" => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable, Threads(run)),
            "Conflicting" => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable,
                "conflicts with its base branch; a rebase follow-up is on the way"),
            "CloseoutParked" => new TaskPhase($"watching {pullRequest} — automatic follow-ups stopped",
                SessionLiveness.NotApplicable, "the monitor still watches for the merge"),
            // Every run failure records this state, not only the pull request being closed
            // without merging (PullRequestClosed), so the line says what is certain — the run
            // ended and no merge was observed — and leaves the recorded reason to the attention
            // line rather than naming a closure it did not observe.
            "Failed" => new TaskPhase($"{pullRequest}: the run ended without a merge",
                SessionLiveness.NotApplicable, "nothing is watching it any more"),
            "ReviewParked" => new TaskPhase($"{pullRequest} open — review parked",
                SessionLiveness.NotApplicable, "the worktree is yours until you resolve it"),
            "BudgetParked" => new TaskPhase($"{pullRequest} open — waiting on the budget window",
                SessionLiveness.NotApplicable, "the daemon retries hourly; nothing is running"),
            _ => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable),
        };
    }

    /// <summary>
    /// What a blocked follow-up is actually held by, in the same terms the derived-facts line
    /// uses for a Blocked row that never pushed (<see cref="PublishedFacts"/>) — that line is
    /// composed only for Published rows, so on a Delivered follow-up the phase is the one place
    /// the hold is said at all. A hold that will not clear itself is named as such, because the
    /// difference between waiting and stuck is what the reader is here for; the recorded death
    /// itself stays on the attention line, which quotes it whole.
    /// </summary>
    private static string BlockedDetail(TaskListItem task) => task switch
    {
        _ when task.DependencyFailureReason.IsNotBlank() => "a blocker will not close out on its own",
        // Blocked with nothing recorded as unmet is a record disagreeing with itself, so the
        // line says that rather than reporting a wait on zero things.
        { UnmetDependencies.Count: 0 } => "blocked, but no unmet dependency is recorded",
        { UnmetDependencies.Count: 1 } => "waiting on 1 dependency to close out",
        _ => $"waiting on {task.UnmetDependencies.Count} dependencies to close out",
    };

    /// <summary>
    /// The pull request as a reader names it. The run's own recorded number comes first, and the
    /// task's URL answers for every row the run has not recorded one on yet — a follow-up records
    /// its number only when it pushes (PullRequestUpdated, at the very end of the run), and a
    /// follow-up merely queued has no current run at all, so without the URL the whole of a
    /// follow-up would read as "the pull request" while the row's own PR column showed the number.
    /// The URL is parsed by the same reader the daemon opens pull requests with, which yields an
    /// honest absence rather than a guess when the shape is not what it expects.
    /// </summary>
    private static string PullRequestLabel(TaskListItem task, RunDetails? run)
    {
        int number = run?.PullRequestNumber
            ?? PullRequestUrls.ParseNumber(task.PullRequestUrl ?? string.Empty);
        return number > 0
            ? $"PR #{number}"
            : "the pull request";
    }

    /// <summary>
    /// What the post-PR review watcher has observed about Copilot's review, while nothing else
    /// has moved the run off AwaitingReview (Decisions Log #88). Unknown is a run recorded
    /// before this observation existed, or the sweep that would have recorded it has not run
    /// yet — strictly less information than None (a sweep that looked and found nothing), so
    /// it must not claim more than None does. The pre-this-branch line it replaces asserted
    /// "waiting on your merge" here, which is exactly the all-clear None's own comment below
    /// already refuses to assert; Unknown reads the identical conservative way instead.
    /// </summary>
    private static TaskPhase AwaitingReviewPhase(string pullRequest, RunDetails run) => run.ExternalReviewState.Value switch
    {
        "Landed" => new TaskPhase($"watching {pullRequest} — Copilot review landed",
            SessionLiveness.NotApplicable, CopilotThreadsDetail(run)),
        "RequestedPending" => new TaskPhase($"watching {pullRequest} — awaiting Copilot review",
            SessionLiveness.NotApplicable, "requested but not yet submitted"),
        // A stale review is review activity that happened, just against a commit that is no
        // longer the head — it must not read as "nothing recorded" (independent pre-PR review,
        // cycle 6), so it gets its own text and the same thread-count detail a landed review gets.
        "Stale" => new TaskPhase($"watching {pullRequest} — Copilot reviewed an earlier commit",
            SessionLiveness.NotApplicable, $"the review is stale; {CopilotThreadsDetail(run)}"),
        // No external review activity does not mean a human's merge is the only thing left:
        // the closeout sweep records this observation ahead of its own checks read, so a run
        // still building or testing reads identically to one that is genuinely idle. Naming
        // the human as the last gate here would assert an all-clear nobody made, so this stays
        // as unresolved as the silent line it replaced for every other AwaitingReview row.
        "None" => new TaskPhase($"watching {pullRequest}",
            SessionLiveness.NotApplicable, "no external review activity observed; its checks may still be reporting"),
        // Unknown carries even less than None: no sweep has recorded an observation at all, so
        // asserting the human's merge is the last gate here would be the same unfounded claim
        // the None arm above refuses to make, on a row that has been watched even less.
        _ => new TaskPhase($"watching {pullRequest}",
            SessionLiveness.NotApplicable, "no external review observation recorded yet; its checks may still be reporting"),
    };

    /// <summary>
    /// The comment-thread count a landed Copilot review left, resolved or not — distinct from
    /// <see cref="Threads"/>'s unresolved-only count, which only ever renders once a finding has
    /// moved the run to ReviewPending. While <see cref="RunDetails.ExternalReviewChecksPending"/>
    /// is still true, this count has not been re-checked for new unresolved threads this sweep
    /// (the same ordering gap <c>AttentionComposer.Delivered</c>'s Landed arm hedges against), so
    /// the detail says that rather than reading as an all-clear.
    /// </summary>
    private static string CopilotThreadsDetail(RunDetails run)
    {
        string threads = run.ExternalReviewThreadCount switch
        {
            0 => "no comment threads",
            1 => "1 comment thread",
            _ => $"{run.ExternalReviewThreadCount} comment threads",
        };

        return run.ExternalReviewChecksPending
            ? $"{threads}, not yet confirmed resolved; its checks may still be reporting"
            : threads;
    }

    /// <summary>
    /// The failing checks, named when the observation named them. A finding recorded without the
    /// job names still says that checks are failing, and says the absence of the names in those
    /// words rather than leaving the reader to read an empty list as "no checks".
    /// </summary>
    private static string ChecksDetail(RunDetails run) => run.FailingChecks.Count > 0
        ? $"checks failing: {string.Join(", ", run.FailingChecks)}"
        : "checks failing, but their names were not recorded";

    /// <summary>
    /// Unresolved review threads, with the human/bot split when the observation recorded one.
    /// An observation made before reviewers other than Copilot were counted has no breakdown,
    /// and stays silent about it rather than reporting a zero nobody observed (log #62).
    /// </summary>
    private static string Threads(RunDetails run)
    {
        if (run.ErroredReviewUrl.IsNotBlank() && run.UnresolvedReviewThreads == 0)
        {
            return "a review errored; the monitor re-requested it";
        }

        string count = $"{run.UnresolvedReviewThreads} unresolved review thread(s)";
        return run.UnresolvedHumanReviewThreads is { } human
            ? $"{count}, {human} from a human"
            : count;
    }

    /// <summary>
    /// A live run with no session recorded at all. Said out loud: the alternative is a phase
    /// that reads as "building" with nothing behind it, which is the reassurance this whole
    /// surface exists to stop giving.
    /// </summary>
    private static string SessionGap(SessionLiveness session) =>
        session == SessionLiveness.NotApplicable ? "no session recorded" : string.Empty;
}
