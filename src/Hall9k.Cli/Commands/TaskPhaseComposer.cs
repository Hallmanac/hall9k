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
    /// <param name="held">
    /// The measured limit holding a queued follow-up back — this node's own ceiling, or its
    /// project's own cap (<see cref="QueueHold"/>, Decisions Log #64, #140) — or null when
    /// neither was measured to be full.
    /// </param>
    /// <param name="heldByTracker">
    /// The measurement that says a queued follow-up is waiting for its project's claim gate (idea
    /// 64c75e43), or null when nothing is holding it that way. Named ahead of
    /// <paramref name="held"/> where both apply, since a card somebody else holds is the
    /// more specific cause and this line carries one.
    /// </param>
    /// <param name="now">
    /// The one clock this composition reads, passed in rather than sampled here so every surface
    /// on a row measures the same instant. Only the pending-check clause needs it (Decisions Log
    /// #164): a check's wait is a length, and a length needs a now.
    /// </param>
    public static TaskPhase Compose(
        TaskListItem task,
        RunDetails? run,
        LifecycleState state,
        SessionLiveness session,
        DateTimeOffset now,
        QueueHold? held = null,
        TrackerClaimDecision? heldByTracker = null)
    {
        if (state == LifecycleState.Working)
        {
            return Working(task, run, session);
        }

        if (state == LifecycleState.Waiting)
        {
            return AwaitingAuthor(task);
        }

        return state == LifecycleState.Delivered
            ? Delivered(task, run, session, now, held, heldByTracker)
            : TaskPhase.None;
    }

    /// <summary>
    /// A posted review waiting on its author (task: a pr-review task stays open while the pull
    /// request's review threads are unresolved). The line names the pull request and how many of
    /// the reviewer's own threads are still unresolved, which are the two facts a reader wants —
    /// "waiting" alone sends them to GitHub to find out what for.
    /// <para>
    /// Liveness is <see cref="SessionLiveness.NotApplicable"/> and no session is named, because
    /// none exists: the run that produced the review has completed, and the watch is the daemon's
    /// own poll rather than a process anything can observe. Saying anything else here would
    /// reassure a reader about a session that is not there.
    /// </para>
    /// <para>
    /// Before the first poll has looked, the line says exactly that rather than reporting zero
    /// open threads — an unlooked-at pull request and one with nothing outstanding are different
    /// facts, and only one of them is Done's business (AGENTS.md, the never-guess rule).
    /// </para>
    /// </summary>
    private static TaskPhase AwaitingAuthor(TaskListItem task)
    {
        // owner/repo#42, the form every other pr-review surface prints, off the one derived reader
        // both this line and the attention line beside it read (TaskListItem.AdoptedPullRequestReference).
        // The recorded URL is the fallback and "the pull request" the last resort — never a
        // fabricated number.
        string pullRequest = task.AdoptedPullRequestReference
            ?? task.PrReviewFollowThroughPullRequestUrl ?? "the pull request";
        if (!task.PrReviewFollowThroughObserved)
        {
            return new TaskPhase(
                $"your review is posted on {pullRequest}", SessionLiveness.NotApplicable,
                "the closeout watcher has not looked at it yet");
        }

        string threads = task.PrReviewOpenThreadCount switch
        {
            0 => "none of your threads are still open",
            1 => "1 of your threads is still open",
            var count => $"{count} of your threads are still open",
        };
        // A newly-requested re-review wakes the reviewer now (needs-you, not Waiting), so a
        // WAITING row carrying this flag is a stream the older behaviour left mid-wait — the
        // request was recorded and never surfaced. Rendered anyway, and deliberately: dropping the
        // suffix would leave exactly those rows saying nothing about the one thing being asked of
        // them (independent pre-PR review, cycle 1, adversarial lens).
        string reReview = task.PrReviewReReviewRequested ? "; a re-review is requested of you" : string.Empty;
        return new TaskPhase(
            $"waiting on {pullRequest}'s author", SessionLiveness.NotApplicable, threads + reReview);
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

        // A deliberate headless start (h9k task start) whose session exited unattended and could
        // not be delivered automatically (task: a do-now session launched by h9k task start is
        // caught within seconds) — checked ahead of every other Working() branch below, including
        // the Guid.Empty/Running one a few lines down that this row would otherwise fall into and
        // read as an ordinary, benign "building" line, exactly the misreading the origin incident
        // (task ef2fefe5) sat behind for an hour. Gated on run.State, not on the reason alone
        // (mirrors AttentionComposer's own identical guard on this same field): the reason is
        // cleared by a fresh InteractiveSessionStarted (h9k task register-session, or
        // h9k task work --direct-launch, re-entering the claim, independent pre-PR review, cycle
        // 1) but nothing clears it when one of the OTHER levers it names moves RunState off
        // Dispatched/Running instead — h9k task handback, h9k task release, or (whenever
        // RunUnattendedExitFlagged.DeliverConfirmedRefuses is false — an unreadable branch, an
        // unreadable git status, a vanished process, or a plain error result, none of which ever
        // confirmed the tree was bad; AttentionComposer's own lever offers h9k task deliver only
        // then, independent pre-PR review, cycle 1, both lenses) h9k task deliver itself — so this
        // must still stop firing once that happens, or the phase line would keep reading "needs
        // your input" over a run already moved on.
        if (run.ExitedUnattendedReason is { } exitedUnattendedReason
            && (run.State.Value == "Dispatched" || run.State.Value == "Running"))
        {
            return new TaskPhase("needs your input", SessionLiveness.NotApplicable, exitedUnattendedReason);
        }

        // The blocker-context session is dispatched inside the launch itself, before the run's
        // own process starts (BlockerContextAssembler, then RunProcessStarted), so the run is
        // still Dispatched while it reads. Only the recorded session's role names that work: the
        // run state alone would call a live condensing pass worktree preparation.
        if (ActiveRole(run) == AgentRole.Synthesis)
        {
            return new TaskPhase("condensing blocker context", session, "context synthesis running");
        }

        // An interactive claim has no lease or heartbeat (Decisions Log #103): closing the
        // terminal is a normal way to leave, and h9k task work re-enters the same claim. The
        // Gone reading is correct — the process really is gone — but printing it in red as "the
        // recorded process is gone" (TaskPhase.LivenessMarkup) misfiles that normal wait as a
        // machine failure, the same misfiling TaskStatusComposer.Silence's own interactive check
        // already argues against one field over (adversarial review, cycle 1).
        //
        // Gated on task.ClaimedByNodeId rather than ActiveRole(run): a normal exit
        // (InteractiveSessionEnded) clears ActiveSessions entirely, so ActiveRole(run) reads
        // Unknown and session reads NotApplicable, not Gone — the same claim a Ctrl+C leaves
        // rendering the honest "building" line above would otherwise fall through to the ordinary
        // Running case and read as an unattended headless run with a lost process (adversarial
        // review, cycle 4). ClaimedByNodeId is the sentinel every other command on this surface
        // already reads for the same distinction and survives the session ending.
        //
        // Gone or NotApplicable only — never Unobserved: a session recorded live on another
        // machine this one cannot check is an unread fact, not an absent one, and folding it in
        // here told a second machine's reader "closing the terminal is a normal way to leave"
        // about a claim that is, as far as anyone here can honestly say, still attached
        // (adversarial review, cycle 1). Unobserved falls through to the ordinary Running case
        // below instead, whose SessionGap/LivenessMarkup already say "liveness not observed here."
        // task.Type != TaskType.PrReview excludes a third Guid.Empty-claimed shape (independent
        // pre-PR review, cycle 1, both lenses): AutoPrReviewEngine.CreateOneAsync's Now speed
        // launches a headless pr-review run under this identical sentinel, and its session name
        // (SessionRoleName.ReviewAdversarial and friends) carries neither the -build nor the
        // -interactive-claim suffix, so it fell into the else arm below and read as an attended
        // h9k task work claim nobody can actually re-enter — TaskWorkCommand refuses a pr-review
        // task outright. TaskWorkCommand and TaskStartCommand both refuse a pr-review task
        // (Decisions Log #99), so no pr-review task ever carries this sentinel from either of
        // those two commands — every pr-review task reaching here got here only through this
        // engine, and excluding the type lets it fall through to the ordinary state-based switch
        // below, which already reports a genuinely gone process honestly rather than as a normal
        // wait for a human to hand off.
        if (task.ClaimedByNodeId == Guid.Empty
            && task.Type != TaskType.PrReview
            && run.State.Value == "Running"
            && session is SessionLiveness.Gone or SessionLiveness.NotApplicable)
        {
            // The lever leads: TaskRowLayout.cs renders this line with Overflow.Ellipsis, so a
            // narrow terminal truncates the tail first — putting the actionable half first keeps
            // it visible even when the explanatory half is cut (adversarial review, cycle 2).
            //
            // Both an operator's own h9k task work and a deliberate h9k task start dispatch carry
            // this same Guid.Empty sentinel (TaskAggregate.IsInteractiveClaim's own discriminator
            // does not tell them apart), but only the former is actually attended — a finished
            // start-it-mine session's process really did just exit on its own, and nobody closed
            // any terminal. SessionRoleName's own role suffix is what tells them apart: h9k task
            // start names its session build, the same role a dispatcher-launched build carries,
            // never interactive-claim (adversarial review, cycle 4, on h9k task start). Checked
            // for the build suffix specifically, not the interactive-claim one: h9k task start is
            // new to this branch, so every run recorded before it existed is an ordinary h9k task
            // work claim regardless of what SessionName carries (blank, on a stream older than
            // session naming itself) — that history reads as attended, the message this branch
            // already gave before this distinction existed, rather than guessing headless from an
            // absent name.
            return run.SessionName.EndsWith("-" + SessionRoleName.Build, StringComparison.Ordinal)
                ? new TaskPhase("building", SessionLiveness.NotApplicable,
                    "h9k task deliver hands this session's work to the standard pipeline; a headless run exiting on its own is normal, not a lost process")
                : new TaskPhase("building", SessionLiveness.NotApplicable,
                    "h9k task work re-enters this claim; closing the terminal is a normal way to leave");
        }

        // An interactive claim's own Dispatched window is no longer necessarily brief (Decisions
        // Log #126): by default h9k task work returns as soon as it prints the worktree, branch,
        // and starting prompt, before the operator has pasted it anywhere — so the generic
        // "worktree and prompt being prepared" wording below would misdescribe a fully-prepared
        // claim sitting untouched, possibly for a long while, as still mid-setup.
        // InteractiveSessionStarted (h9k task register-session, or a --direct-launch's own
        // onStarted callback) is what moves the run to Running, so Dispatched here always means
        // "nothing has registered yet" — never a claim actively being worked. Gated on
        // ClaimedByNodeId (Guid.Empty), the same interactive-claim sentinel the Running case above
        // already keys on: a headless run's own Dispatched window (the brief instant before
        // RunLauncher's process actually starts) is exactly what the wording below still describes
        // correctly. --direct-launch's own Dispatched window is just as brief as a headless run's,
        // so this line is momentarily inaccurate for it too in principle — but nothing reads
        // h9k status inside the milliseconds between a claim and --direct-launch's own
        // Process.Start(), and the run record carries no field distinguishing which launch mode a
        // claim used, so there is no honest way to special-case that path out of this one.
        //
        // A deliberate h9k task start claim carries this same Guid.Empty sentinel and can sit
        // Dispatched indefinitely too, when HeadlessLaunch.SpawnDetached itself throws (claude
        // missing from PATH, a stale HALL9K_CLAUDE_PATH) — the claim and its Dispatched run
        // survive that by design, but no prompt was ever printed for a human to paste anywhere
        // (adversarial review, cycle 1). SessionRoleName's own role suffix is what tells the two
        // apart here too, exactly as the Running case above already relies on it.
        // Excludes task.Type == TaskType.PrReview for the identical reason the Running case above
        // does: AutoPrReviewEngine's Now-speed headless launch is the third Guid.Empty-claimed
        // shape, never an attended claim, and falling through to the ordinary "starting up" line
        // below is honest about it — nothing was printed for a human to paste.
        if (task.ClaimedByNodeId == Guid.Empty && task.Type != TaskType.PrReview && run.State.Value == "Dispatched")
        {
            return run.SessionName.EndsWith("-" + SessionRoleName.Build, StringComparison.Ordinal)
                ? new TaskPhase("awaiting launch", SessionLiveness.NotApplicable,
                    "h9k task start recorded this claim but its headless session never started — h9k task work re-enters it")
                : new TaskPhase("awaiting a pasted session", SessionLiveness.NotApplicable,
                    "h9k task work already printed the worktree, branch, and prompt — paste it into a Claude Code "
                    + "session (it self-registers) or h9k task work --direct-launch to launch one yourself");
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
    /// Which round of review, and which leg of it. The cycle cap now resolves task &gt; project &gt;
    /// node &gt; compiled default (Decisions Log #112, <c>ReviewCapResolver</c>) rather than being a
    /// single daemon-wide constant, and this line stops short of resolving that chain itself
    /// (a task and project read, plus the node's own config) just to print "of N" — it says which
    /// cycle the run is on and leaves the cap to <c>h9k task show</c>'s own override row and
    /// <c>h9k config show</c>/<c>h9k project show</c> rather than guessing or re-deriving it here.
    /// </summary>
    private static TaskPhase Review(RunDetails run, SessionLiveness session)
    {
        // Named only past Discovery (task: review cycles after the first): Discovery is the shape
        // review always had, so calling it out on every cycle 1 would just be noise, while Verify
        // and FinalFullPass are the new shapes a reader needs told apart from an ordinary cycle.
        string mode = run.ReviewCycleMode == ReviewMode.Verify ? " (verify)"
            : run.ReviewCycleMode == ReviewMode.FinalFullPass ? " (final full pass)"
            : string.Empty;
        string cycle = run.ReviewCycle > 0 ? $"review cycle {run.ReviewCycle}{mode}" : "review";
        // AgentRole.Fix alone cannot tell an ordinary review-fix session apart from the
        // pre-final-pass rebase-recovery session (task: a run rebases its branch onto the current
        // base branch) — both resolve the Fix role's model and share the role, the same reason
        // ActiveSession.Name is recorded rather than reconstructed from Role/Lens alone (see that
        // record's own doc). Named sessions only: a stream written before session naming existed
        // reads as an ordinary fix, the honest default this line already gave before this
        // distinction existed.
        if (ActiveRole(run) == AgentRole.Fix && run.ActiveSessions is [{ Name: { } name }, ..]
            && name.Contains(SessionRoleName.PreFinalPassRebasePrefix, StringComparison.Ordinal))
        {
            return new TaskPhase(cycle, session, "rebasing onto the base branch before the final pass");
        }

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
        TaskListItem task, RunDetails? run, SessionLiveness session, DateTimeOffset now,
        QueueHold? held, TrackerClaimDecision? heldByTracker)
    {
        string pullRequest = PullRequestLabel(task, run);

        // A reopened task is a follow-up in flight (or about to be): the machinery owns the
        // next move, not the reader. What it is waiting on is a measurement or nothing — the
        // hold's own line when this node's last sweep reported either its ceiling or this
        // project's cap full, and silence otherwise, because a queue that is not moving has many
        // causes and the display observed none of them (Decisions Log #64, #140, AGENTS.md's
        // never-guess rule).
        if (task.State == TaskState.Queued)
        {
            // The claim gate is named ahead of the ceiling for the same reason PublishedFacts
            // orders them that way: a reopened follow-up whose card the tracker says somebody else
            // holds is not going to be claimed here whatever the ceiling does (idea 64c75e43), and
            // this line has room for exactly one cause.
            return WithChecksPendingDetail(
                new TaskPhase($"follow-up queued for {pullRequest}", SessionLiveness.NotApplicable,
                    heldByTracker?.ReasonLine ?? held?.ReasonLine ?? "not claimed yet"),
                task, now);
        }

        // A reopened follow-up held by a dependency: nothing is dispatching it and no run is
        // watching the pull request, so the line says what it is waiting on. Answered before the
        // run is read at all, because TaskAssigned does not clear CurrentRunId — reading the run
        // here would describe the previous run's ending as this row's phase. Grouped with the
        // claimed states below, it composed Working's dispatch-handoff line instead and asserted
        // a handoff the platform never made (pre-PR review, 2026-08-22).
        if (task.State == TaskState.Blocked)
        {
            // The pending-check clause on the same terms as its two sibling arms above and below:
            // the dependency is what holds this row, and a check the merge is also waiting on is a
            // second fact the reader wants on the one line. Closeout itself parks rather than
            // reopening a task with an unmet dependency, so today only a manual h9k pr resolve
            // lands here — and that path records no anchor, so the clause renders nothing. Applied
            // anyway rather than left as the one arm of three that would silently drop it if that
            // ever changes.
            return WithChecksPendingDetail(
                new TaskPhase($"follow-up blocked for {pullRequest}", SessionLiveness.NotApplicable,
                    BlockedDetail(task)),
                task, now);
        }

        if (task.State == TaskState.Claimed || task.State == TaskState.NeedsHuman)
        {
            TaskPhase working = Working(task, run, session);
            return WithChecksPendingDetail(
                WithTriageDetail(working with { Text = $"follow-up on {pullRequest}: {working.Text}" }, run),
                task, now);
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
            "AwaitingReview" => WithTriageDetail(AwaitingReviewPhase(pullRequest, run, now), run),
            "ChecksFailing" => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable,
                ChecksDetail(run)),
            "ReviewPending" => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable, Threads(run)),
            "Conflicting" => new TaskPhase($"watching {pullRequest}", SessionLiveness.NotApplicable,
                "conflicts with its base branch; a rebase follow-up is on the way"),
            "CloseoutParked" => new TaskPhase($"watching {pullRequest} — automatic follow-ups stopped",
                SessionLiveness.NotApplicable, "the monitor still watches for the merge"),
            // Every run failure or kill records one of these two states, not only the pull
            // request being closed without merging (PullRequestClosed), so the line says what is
            // certain — the run ended and no merge was observed — and leaves the recorded reason
            // to the attention line rather than naming a closure it did not observe. The detail
            // calls the exact predicate AttentionComposer.IsOrphanSweepCandidate uses (rather
            // than a second, independently maintained copy of it) so this line and the attention
            // line printed directly under it can never drift apart on what the sweep would
            // actually match — Killed joins Failed here for the identical reason AttentionComposer's
            // own switch already groups the two (independent pre-PR review, cycle 3, low: this
            // arm previously covered only "Failed" and let a Killed run fall to the generic
            // "watching" default, contradicting the richer attention line directly under it).
            "Failed" or "Killed" => new TaskPhase($"{pullRequest}: the run ended without a merge",
                SessionLiveness.NotApplicable,
                AttentionComposer.IsOrphanSweepCandidate(task, run)
                    ? "still eligible for closeout's merge observation"
                    : "nothing is watching it any more"),
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
        // The remote twin of the line above: a stacked parent pull request that closed unmerged
        // is a hold nothing will clear either (task: a stacked child can stand on a pull request
        // another install owns). Ahead of the counts for the same reason its local sibling is.
        _ when task.RemoteStackedParentHoldReason.IsNotBlank() =>
            $"the pull request it is stacked on (#{task.StackedOnPullRequestNumber}) closed without merging",
        // Waiting on a remote parent is the ordinary case with no unmet dependency at all behind
        // it — there is no local task to name — so it is answered before the "no unmet dependency
        // is recorded" arm, which would otherwise read this as a record disagreeing with itself.
        _ when task.StackedOnPullRequestNumber is { } parentNumber
            && !task.RemoteStackedParentState.ReleasesChild =>
            $"waiting for pull request #{parentNumber}, which it is stacked on, to be open "
            + $"(last observed {task.RemoteStackedParentState.Describe()})",
        // Blocked with nothing recorded as unmet is a record disagreeing with itself, so the
        // line says that rather than reporting a wait on zero things.
        { UnmetDependencies.Count: 0 } => "blocked, but no unmet dependency is recorded",
        // The stacked arms sit ahead of the plain counts for the reason PublishedFacts' own twin
        // states: "to close out" is the wrong bar for a stacked parent, which releases this task at
        // its Delivered, and these two lines must not disagree about it.
        _ when task.StackedOnTaskId is { } stackedParentId
            && task.UnmetDependencies.Contains(stackedParentId) =>
            task.UnmetDependencies.Count == 1
                ? "waiting on its stacked parent to reach Delivered"
                : $"waiting on {task.UnmetDependencies.Count} dependencies to clear (one stacked)",
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
    /// has moved the run off AwaitingReview (Decisions Log #89). Unknown covers a run recorded
    /// before this observation existed, a sweep that has not run yet, and a sweep that read a
    /// Copilot review it could not compare against the head commit — strictly less information
    /// than None (a sweep that looked and found nothing), so it must not claim either that
    /// nothing was recorded or that a clean pull request was observed. The pre-this-branch line
    /// it replaces asserted "waiting on your merge" here, which is exactly the all-clear None's
    /// own comment below already refuses to assert; Unknown reads the identical conservative
    /// way instead.
    /// </summary>
    private static TaskPhase AwaitingReviewPhase(string pullRequest, RunDetails run, DateTimeOffset now) =>
        // Ahead of every Copilot reading below, for the same reason AttentionComposer's own stacked
        // branch sits ahead of its two arms (task: the bar machinery treats an un-retargeted stacked
        // PR as not at the bar): the phase line and the attention line under it must never disagree,
        // and "awaiting human review" on a pull request nobody can merge yet is the disagreement.
        run.StackedOnBranch is { } parentBranch
            ? new TaskPhase(
                $"watching {pullRequest} — stacked on {parentBranch}",
                SessionLiveness.NotApplicable,
                "its base is still the parent's branch; it retargets and replays when the parent merges")
            : AwaitingReviewCopilotPhase(pullRequest, run, now);

    /// <summary>The Copilot-observation readings, once the stacked check above has had its say.</summary>
    private static TaskPhase AwaitingReviewCopilotPhase(string pullRequest, RunDetails run, DateTimeOffset now) => run.ExternalReviewState.Value switch
    {
        "Landed" => new TaskPhase($"watching {pullRequest} — Copilot review landed",
            SessionLiveness.NotApplicable, CopilotThreadsDetail(run, now)),
        "RequestedPending" => new TaskPhase($"watching {pullRequest} — awaiting Copilot review",
            SessionLiveness.NotApplicable, "requested but not yet submitted"),
        // A stale review is review activity that happened, just against a commit that is no
        // longer the head — it must not read as "nothing recorded" (independent pre-PR review,
        // cycle 6), so it gets its own text and the same thread-count detail a landed review gets.
        "Stale" => new TaskPhase($"watching {pullRequest} — Copilot reviewed an earlier commit",
            SessionLiveness.NotApplicable, $"the review is stale; {CopilotThreadsDetail(run, now)}"),
        // No external review activity does not automatically mean a human's merge is the only
        // thing left: a pending check holds the merge on its own (the merge bar is unchanged by
        // Decisions Log #164), so a run whose CI picture was still incomplete as
        // of this observation would read identically to one that is genuinely idle if this ignored
        // RunDetails.ExternalReviewChecksPending the way the Landed arm above does not (independent
        // pre-PR review, cycle 7). What it says about that wait is its length, not that it might
        // exist: nothing on this row is going to move until the check reports, and a reader who is
        // told only "its checks may still be reporting" cannot tell an ordinary two-minute build
        // from the nine-hour dead check that ruling was written for. Once the same field says the
        // picture was complete, there is nothing left unresolved here and the human's merge is what
        // remains, so the line says so.
        "None" => run.ExternalReviewChecksPending
            ? new TaskPhase($"watching {pullRequest}",
                SessionLiveness.NotApplicable,
                $"no external review activity observed; {TaskStatusComposer.ChecksPendingClause(run.ExternalReviewChecksPendingSince, now)}")
            : new TaskPhase($"watching {pullRequest} — awaiting human review",
                SessionLiveness.NotApplicable, "no external review activity observed"),
        // Unknown carries even less than None: either no sweep has recorded an observation at
        // all, or a sweep read a Copilot review it could not compare against the head commit —
        // in neither case is there confirmed review activity to report, so asserting the human's
        // merge is the last gate here would be the same unfounded claim the None arm above
        // refuses to make.
        _ => new TaskPhase($"watching {pullRequest}",
            SessionLiveness.NotApplicable, "no confirmed review observation recorded; its checks may still be reporting"),
    };

    /// <summary>
    /// The comment-thread count a landed Copilot review left, resolved or not — distinct from
    /// <see cref="Threads"/>'s unresolved-only count, which only ever renders once a finding has
    /// moved the run to ReviewPending.
    /// <para>
    /// A pending CI picture no longer hedges the count. It used to: the sweep short-circuited on
    /// <see cref="RunDetails.ExternalReviewChecksPending"/> ahead of its own unresolved-thread
    /// read, so a landed review's threads genuinely had not been re-checked and the detail said
    /// "not yet confirmed resolved". Review feedback is now detected whatever the checks are doing
    /// (Decisions Log #164), so a row resting here with a pending check has in
    /// fact had its threads read — repeating that hedge would report a gap that no longer exists,
    /// and offering the pendingness alone would name it as the reason nothing is happening, which
    /// is the exact misreading that ruling was written against. What is left to say about the check
    /// is how long it has been pending, which the merge is genuinely waiting on.
    /// </para>
    /// </summary>
    private static string CopilotThreadsDetail(RunDetails run, DateTimeOffset now)
    {
        string threads = run.ExternalReviewThreadCount switch
        {
            0 => "no comment threads",
            1 => "1 comment thread",
            _ => $"{run.ExternalReviewThreadCount} comment threads",
        };

        return run.ExternalReviewChecksPending
            ? $"{threads}; {TaskStatusComposer.ChecksPendingClause(run.ExternalReviewChecksPendingSince, now)}"
            : threads;
    }

    /// <summary>
    /// The pending-check clause on a follow-up row: the lap is dispatched, queued or blocked — that
    /// is what the phase text already says — and this names the check the merge is still waiting on
    /// behind it, so the two facts sit on one line instead of the reader having to infer either.
    /// Read off the task rather than the run because <c>Apply(TaskReopened)</c> clears
    /// <c>TaskListItem.CurrentRunId</c>: on a queued follow-up the run that observed the checks is
    /// not reachable from the row at all (<see cref="TaskListItem.FollowUpChecksPendingSince"/>).
    /// A row with no anchor renders exactly as it did before this clause existed, rather than
    /// reporting a wait nobody observed.
    /// </summary>
    private static TaskPhase WithChecksPendingDetail(TaskPhase phase, TaskListItem task, DateTimeOffset now)
    {
        if (task.FollowUpChecksPendingSince is null)
        {
            return phase;
        }

        string clause = TaskStatusComposer.ChecksPendingClause(task.FollowUpChecksPendingSince, now);
        return phase with
        {
            Detail = phase.Detail.IsBlank() ? clause : $"{phase.Detail}; {clause}",
        };
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
        string detail = run.UnresolvedHumanReviewThreads is { } human
            ? $"{count}, {human} from a human"
            : count;
        return run.LastReviewThreadOutcomes.Count > 0
            ? $"{detail}; last triage: {DispositionSummary(run.LastReviewThreadOutcomes)}"
            : detail;
    }

    /// <summary>
    /// A follow-up's own triage (task: every review thread on a pull request gets a triage
    /// disposition before any fix work), folded into whichever phase line already names this
    /// follow-up's pull request. A run this task's follow-up has not yet triaged, or one whose
    /// prompt never taught the vocabulary, carries no outcomes and the phase renders exactly as
    /// it did before this field existed.
    /// </summary>
    private static TaskPhase WithTriageDetail(TaskPhase phase, RunDetails? run) =>
        run is { LastReviewThreadOutcomes.Count: > 0 }
            ? phase with
            {
                Detail = phase.Detail.IsBlank()
                    ? $"last triage: {DispositionSummary(run.LastReviewThreadOutcomes)}"
                    : $"{phase.Detail}; last triage: {DispositionSummary(run.LastReviewThreadOutcomes)}",
            }
            : phase;

    /// <summary>
    /// Each thread's disposition, counted rather than listed one row per thread — the phase line
    /// is one line by contract (<see cref="TaskPhase"/>'s own doc), and the count is what answers
    /// the question this field exists for: how much of a triage's own work turned into a real fix
    /// versus a decline or a route.
    /// </summary>
    private static string DispositionSummary(IReadOnlyList<ReviewThreadOutcome> outcomes)
    {
        int fix = outcomes.Count(outcome => outcome.Disposition == ReviewThreadDisposition.Fix);
        int decline = outcomes.Count(outcome => outcome.Disposition == ReviewThreadDisposition.Decline);
        int route = outcomes.Count(outcome => outcome.Disposition == ReviewThreadDisposition.Route);
        int unknown = outcomes.Count - fix - decline - route;
        List<string> parts = [];
        if (fix > 0)
        {
            parts.Add($"{fix} fix");
        }

        if (decline > 0)
        {
            parts.Add($"{decline} decline");
        }

        if (route > 0)
        {
            parts.Add($"{route} route");
        }

        if (unknown > 0)
        {
            parts.Add($"{unknown} unrecognized");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A live run with no session recorded at all. Said out loud: the alternative is a phase
    /// that reads as "building" with nothing behind it, which is the reassurance this whole
    /// surface exists to stop giving.
    /// </summary>
    private static string SessionGap(SessionLiveness session) =>
        session == SessionLiveness.NotApplicable ? "no session recorded" : string.Empty;
}
