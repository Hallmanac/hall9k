using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one composer that maps recorded facts to "does this want a human, why, and what do I
/// type" (Decisions Log #66, absorbing backlog 28). One owner on purpose: <c>h9k status</c> and
/// <c>h9k task show</c> read the same answer from here, so they cannot disagree about whether a
/// row is asking for something.
/// <para>
/// Every cause below is read off a record — <c>ReviewParked.Reason</c>, <c>CloseoutParked.Reason</c>,
/// the dependency-failure record, the recorded failure reason, the observed check and thread
/// counts. Nothing here re-guesses at a cause, and where a cause is genuinely not recorded
/// distinctly the line says what was observed instead of inventing a category.
/// </para>
/// </summary>
internal static class AttentionComposer
{
    /// <summary>
    /// What this row is asking of the reader. Ordered by who actually owns the next move: a task
    /// the human ended asks for nothing at all, an explicit park outranks everything that is
    /// still live, a dead blocker outranks a live one, and a lane the machinery is still working
    /// is never red however long it has been going.
    /// </summary>
    /// <param name="budgetParkedRuns">
    /// How many runs the same board is holding on the same exhausted budget window (backlog
    /// 40). Counted once for the board rather than per row, because that is the whole
    /// point: the origin incident was three rows that read as three unrelated failures when
    /// they were one condition.
    /// </param>
    public static TaskAttention Compose(
        TaskListItem task,
        RunDetails? run,
        LifecycleState state,
        TaskPhase phase,
        bool stalled,
        DateTimeOffset now,
        int budgetParkedRuns = 0,
        int interactiveClaimStaleAfterDays = Domain.Infrastructure.Persistence.OperatingSettings.DefaultInteractiveClaimStaleAfterDays)
    {
        string id = TaskListCommand.ShortId(task.Id);

        // A task a human walked away from owes that human nothing, whatever its last run is
        // still recorded as. Abandon deletes the lease and appends to the task's stream only, so
        // a run parked for review or closeout stays parked under the archived task forever:
        // nothing supersedes it, the dispatch sweep iterates leases and the closeout monitor
        // watches open pull requests, so neither ever reaches it. Without this the park arms
        // below would keep the row red in Needs-you and in every rollup that counts it, offering
        // a lever that would resume a run for work the human explicitly dropped.
        // Origin incident (2026-08-22, pre-PR review cycle 2): the three-surface rewrite widened
        // those arms from the run's state plus the task's ("Claimed" and review-parked) to the
        // run's state alone, and an abandoned task with a parked run was the leak.
        if (state == LifecycleState.Archived)
        {
            return TaskAttention.None;
        }

        // An agent asked a question and stopped. The ask-a-human loop records the question but
        // no command answers it yet, so the lever is the one that shows it rather than one the
        // platform does not have (never advise a lever the platform will refuse).
        if (task.State == TaskState.NeedsHuman)
        {
            return new TaskAttention(
                AttentionLevel.NeedsYou,
                "the agent asked a question and stopped; no command answers it yet",
                $"h9k task show {id}");
        }

        if (run?.State == Domain.Features.Run.RunState.ReviewParked)
        {
            return new TaskAttention(
                AttentionLevel.NeedsYou,
                Reason(run.ParkedReason, "the pre-PR review loop parked without recording a reason"),
                ReviewParkLever(task, run, id));
        }

        if (run?.State == Domain.Features.Run.RunState.CloseoutParked)
        {
            return new TaskAttention(
                AttentionLevel.NeedsYou,
                Reason(run.ParkedReason, "closeout parked without recording a reason"),
                $"h9k pr resolve {id} --reason \"…\"");
        }

        if (state == LifecycleState.Failed)
        {
            return new TaskAttention(AttentionLevel.NeedsYou, FailureCause(task, run),
                $"h9k task retry {id} --reason \"…\" (or resolve, or abandon)");
        }

        // A blocker observed dead will never close out on its own, so the task cannot unblock
        // itself. The recorded reason already names the lever the platform will honour (log #61)
        // and is quoted whole rather than re-derived into a second, possibly different, piece of
        // advice about the same blocker.
        if (task.State == TaskState.Blocked && task.DependencyFailureReason.IsNotBlank())
        {
            return new TaskAttention(AttentionLevel.NeedsYou, task.DependencyFailureReason);
        }

        if (stalled)
        {
            return new TaskAttention(AttentionLevel.NeedsYou, StallCause(phase), $"h9k logs {id}");
        }

        // An interactive claim (h9k task work) carries no lease and no heartbeat by design
        // (Decisions Log #103) — closing the terminal is a normal way to leave, so a quiet claim
        // is never a fault the way a stalled headless run is, and nothing here ever reclaims one
        // automatically (idea 3ba186b6: "a staleness nudge, not a timeout"). But a claim nobody
        // has touched in a long time is easy to forget about, so the only remedy is asking: still
        // yours, or ready to hand off? Measured from the last recorded touch — a session
        // attaching or detaching (RunDetails.LastInteractiveActivityAt) — falling back to when
        // the claim itself began (RunDetails.DispatchedAt) for a claim that has never yet
        // recorded either, rather than guessing at a touch nobody observed.
        if (task.State == TaskState.Claimed && task.IsInteractiveClaim && run is not null)
        {
            TimeSpan age = now - (run.LastInteractiveActivityAt ?? run.DispatchedAt);
            if (age >= TimeSpan.FromDays(interactiveClaimStaleAfterDays))
            {
                return new TaskAttention(
                    AttentionLevel.NeedsYou,
                    $"an interactive claim (h9k task work) has sat untouched for {TaskStatusComposer.RelativeAge(age)} "
                    + "— still yours, or ready to hand off?",
                    $"h9k task work {id} if you're still on it, or h9k task handback {id} to finish it headlessly");
            }
        }

        // A Jira write is stuck on an expired or missing twg login (Brian's design, 2026-08-28) —
        // a handled, expected state rather than a crash, and one the operator clears in their own
        // terminal rather than through an h9k command. Checked after every park, failure,
        // dead-blocker and stall arm above rather than ahead of them (independent pre-PR review,
        // cycle 1): the write carries no lifecycle state of its own, so it must not be shadowed
        // when nothing else is amiss — but a stuck write is never the reason a run is parked, a
        // task failed, a blocker died or a run stalled, and putting it first hid whichever of
        // those actually stopped the work behind "run twg login" instead. Checked ahead of
        // BudgetParked below, though (independent pre-PR review, cycle 2): a budget park clears
        // itself on a clock and is explicitly not an ask, so if it ran first it would suppress a
        // genuine needs-you row for as long as the budget window held, while the retry sweep kept
        // re-issuing the same doomed write underneath it.
        if (task.PendingJiraWriteIsAuthFailure)
        {
            return new TaskAttention(
                AttentionLevel.NeedsYou,
                Reason(task.PendingJiraWriteFailureReason, "a Jira write is pending and could not authenticate"),
                "twg login");
        }

        // Parked on the clock, not on a person (backlog 40): the subscription usage
        // window ran out, the retry sweep clears it hourly without anyone typing anything, and
        // so it is waiting-but-handled rather than an ask. Checked after every needs-you arm
        // above, including the pending-Jira-write one (independent pre-PR review, cycle 2), so an
        // ignorable clock-bound wait never shadows a genuine ask — but still ahead of the
        // Delivered and live-Blocked arms below, so a follow-up that pushed reads the same wait
        // its pre-push sibling does, one condition, said once, whatever lifecycle state the row
        // happens to be in.
        if (run?.State == Domain.Features.Run.RunState.BudgetParked)
        {
            return new TaskAttention(AttentionLevel.WaitingHandled, BudgetHoldCause(run, budgetParkedRuns));
        }

        if (state == LifecycleState.Delivered)
        {
            return Delivered(task, run, id);
        }

        // Still waiting on blockers that are all alive: it queues itself the moment they close
        // out, so it is a hold the reader can consciously ignore rather than an ask. Which
        // blockers, and how many, is on the derived-facts line; this says the ignorable part.
        if (task.State == TaskState.Blocked)
        {
            return new TaskAttention(
                AttentionLevel.WaitingHandled,
                "nothing for you to do — it queues itself when its blockers close out");
        }

        return TaskAttention.None;
    }

    /// <summary>
    /// The pushed-but-not-merged rows. Most of them are being handled and a few are the reader's
    /// turn, and saying which is which is the whole reason this state exists (origin incident,
    /// 2026-08-22, PR 24).
    /// </summary>
    private static TaskAttention Delivered(TaskListItem task, RunDetails? run, string id)
    {
        // Delivered work nobody is assigned to. h9k pr resolve reopens a done task to Queued and
        // keeps its pull request, and h9k task unassign accepts it from there — which leaves an
        // open pull request, no run watching it, and no owner whose nodes could claim the
        // follow-up. Nothing moves it until a human assigns it again, so it is a red row with a
        // lever rather than a wait that clears itself. Checked before the run is read: the reopen
        // clears CurrentRunId, so the run-is-null arm below would otherwise answer for this row
        // with the pull request as its only lever.
        if (task.State == TaskState.Published)
        {
            return new TaskAttention(AttentionLevel.NeedsYou,
                "the pull request is open and the task is unassigned — nothing will claim the follow-up",
                $"h9k task assign {id}");
        }

        // A follow-up run owns the pull request: the machinery is mid-move, not the human. The
        // marker alone, because the phase line above already says what that run is doing and a
        // second line repeating it is how a pane earns the scroll it was built to avoid.
        if (task.State != TaskState.Done)
        {
            return new TaskAttention(AttentionLevel.WaitingHandled);
        }

        if (run is null)
        {
            return new TaskAttention(AttentionLevel.NeedsYou,
                "the pull request is open and no run record is watching it for a merge",
                task.PullRequestUrl ?? string.Empty);
        }

        return run.State.Value switch
        {
            "AwaitingReview" => AwaitingReviewAttention(run),
            // The closeout monitor dispatches a follow-up or parks; while it is neither parked
            // nor out of budget, this is being handled and the reader can leave it alone.
            // Conflicting joins this arm for the identical reason: a rebase follow-up is
            // already on the way, exactly like a checks or review-feedback follow-up.
            "ChecksFailing" or "ReviewPending" or "Conflicting" => new TaskAttention(AttentionLevel.WaitingHandled,
                "the closeout monitor owns the next move on this pull request"),
            // The run ended without a merge. RunState.Failed is what most run failures record
            // and a pull request closed without merging (PullRequestClosed) is only one way to
            // reach it — a gate failure on a task a human then resolved onto this pull request
            // is another — so the cause quotes the reason the run recorded instead of naming a
            // closure nobody observed. RunState.Killed reaches the identical row (a Done task's
            // pull request nobody is watching any more) by a different door, and the orphan
            // sweep now reads a killed run's pull request exactly as it reads a failed one's
            // (Decisions Log #72), so this arm answers for both. Those two want different
            // levers, and the recorded reason is the fact that tells them apart, so the lever
            // is composed from it.
            "Failed" or "Killed" => new TaskAttention(AttentionLevel.NeedsYou,
                "the run ended without a merge being observed, and nothing is watching this pull "
                + $"request any more: {Reason(run.FailureReason, "the run recorded no reason")}",
                UnwatchedRemedy(task, run, id)),
            _ => TaskAttention.None,
        };
    }

    /// <summary>
    /// The AwaitingReview attention cause, split by the post-PR review watcher's own read of
    /// Copilot (Decisions Log #89) — the same readings <c>TaskPhaseComposer</c>'s phase
    /// line already draws, so the pane's cause never contradicts the line printed directly above
    /// it (pre-PR review, cycle 2: the two used to disagree — "awaiting Copilot review" on the
    /// phase line, "read its checks, then the merge is yours" on the attention line right under
    /// it). Copilot outstanding is not the human's turn yet, so it renders waiting-but-handled
    /// rather than red.
    /// </summary>
    private static TaskAttention AwaitingReviewAttention(RunDetails run) => run.ExternalReviewState.Value switch
    {
        "RequestedPending" => new TaskAttention(AttentionLevel.WaitingHandled,
            "Copilot's review is requested but not submitted yet — nothing for you until it lands"),
        // A landed review recorded while checks were still incomplete has not been read
        // against a settled CI result, and its threads have not been re-checked for new
        // unresolved ones this sweep either (RunDetails.ExternalReviewChecksPending) — the
        // unconditional claim below is only true once a sweep got past both reads without
        // moving the run off AwaitingReview, so a run still carrying the caveat gets the same
        // "read its checks first" hedge the None arm below already gives a quiet pull request.
        "Landed" when run.ExternalReviewChecksPending => new TaskAttention(AttentionLevel.NeedsYou,
            "Copilot's review landed, but its checks may still be reporting and its threads are not yet "
            + "confirmed resolved — read its checks, then the merge is yours",
            run.PullRequestUrl ?? string.Empty),
        "Landed" => new TaskAttention(AttentionLevel.NeedsYou,
            "Copilot's review landed — read it, then the merge is yours", run.PullRequestUrl ?? string.Empty),
        // A stale review is review activity that happened, just against a commit that is no
        // longer the head (independent pre-PR review, cycle 6) — it must not collapse into the
        // "nothing has been recorded" cause below, which would tell the reader Copilot never
        // looked at all.
        "Stale" => new TaskAttention(AttentionLevel.NeedsYou,
            $"Copilot reviewed an earlier commit and the review is stale ({StaleThreadCountText(run)}) "
            + "— read its checks, then the merge is yours",
            run.PullRequestUrl ?? string.Empty),
        // "None" is a sweep that looked and found nothing. Once that same sweep's checks read
        // was also complete (RunDetails.ExternalReviewChecksPending), there is nothing left
        // unresolved on this row and the cause says so plainly — the same split
        // TaskPhaseComposer.AwaitingReviewPhase's own "None" arm already makes, so the cause
        // never sends the reader back to checks the phase line directly above it just said were
        // done (independent pre-PR review, cycle 4).
        "None" when !run.ExternalReviewChecksPending => new TaskAttention(AttentionLevel.NeedsYou,
            "no external review activity recorded — the merge is yours",
            run.PullRequestUrl ?? string.Empty),
        "None" => new TaskAttention(AttentionLevel.NeedsYou,
            "no external review activity recorded, and its checks may still be reporting — read "
            + "them, then the merge is yours",
            run.PullRequestUrl ?? string.Empty),
        // Unknown is either no sweep at all or a sweep that read a Copilot review it could not
        // compare against the head commit — in neither case is there confirmed review activity
        // to report, so the cause must not claim "nothing recorded" (that would be false when a
        // review was seen but not classifiable) or hand out the all-clear "None" itself only
        // gives once checks are also settled. It sends the reader to the pull request's own
        // checks either way.
        _ => new TaskAttention(AttentionLevel.NeedsYou,
            "no confirmed review activity recorded — read its checks, then the merge is yours",
            run.PullRequestUrl ?? string.Empty),
    };

    /// <summary>
    /// The comment-thread count a stale Copilot review left, resolved or not — the same count
    /// <c>TaskPhaseComposer.CopilotThreadsDetail</c> renders on the phase line directly above
    /// this cause, so the two never disagree about how many threads a stale review is worth.
    /// </summary>
    private static string StaleThreadCountText(RunDetails run) => run.ExternalReviewThreadCount switch
    {
        0 => "no comment threads",
        1 => "1 comment thread",
        _ => $"{run.ExternalReviewThreadCount} comment threads",
    };

    /// <summary>
    /// What actually moves a Done task whose run ended with no merge observed. <c>h9k pr resolve</c>
    /// dispatches a follow-up run onto the existing pull-request branch, and that run rejoins the
    /// closeout monitor's watch set, so the merge is finally observed — the identical remedy a
    /// dependent is given for the identical situation (<c>TaskDependency.DescribeDoneRemedy</c>).
    /// It is named only where the decider would accept it: <c>TaskDecider.Reopen</c> needs a run
    /// to follow up on (the caller already has one here) and the branch that run pushed, so a run
    /// document with no branch recorded gets the pull request instead of a command that refuses.
    /// <para>
    /// A pull request the monitor observed closed is the exception, and the recorded reason is
    /// what says so. Reopening would be accepted and would achieve nothing: the follow-up pushes
    /// to a branch whose pull request nobody can merge, so there is no watch worth rejoining.
    /// That row's next act is on the pull request itself, which is what the URL is for.
    /// </para>
    /// <para>
    /// A pr-review task is a second exception, refused for a different reason (adversarial
    /// review, cycle 3): <c>TaskDecider.Reopen</c> refuses the type outright, since a pr-review
    /// run has no pull request of its own for a follow-up to push to. A pr-review task only
    /// reaches this row at all when a human closed it out by hand
    /// (<c>h9k task resolve --pr &lt;url&gt;</c> over a run that failed) — never advise the lever
    /// the platform will refuse.
    /// </para>
    /// </summary>
    private static string UnwatchedRemedy(TaskListItem task, RunDetails run, string id) =>
        task.Type != TaskType.PrReview
        && run.FailureReason != RunDetails.PullRequestClosedWithoutMerge && run.Branch.IsNotBlank()
            ? $"h9k pr resolve {id}"
            : run.PullRequestUrl ?? task.PullRequestUrl ?? string.Empty;

    /// <summary>
    /// Why a Failed row failed, composed from what is actually recorded rather than shown as the
    /// bare word (Decisions Log #66). A category is only named where a distinct record supports
    /// it: failed gates are listed because <c>VerificationFailed</c> names them, a kill is named
    /// because <c>RunKilled</c> records its reason. Token exhaustion no longer arrives here at
    /// all — it has a record and a park of its own now (backlog 40) — and any other cause
    /// with no distinct record arrives as whatever text the machinery wrote, shown verbatim
    /// rather than sorted into a category nobody observed.
    /// </summary>
    public static string FailureCause(TaskListItem task, RunDetails? run)
    {
        string recorded = task.FailureReason.IsNotBlank()
            ? task.FailureReason
            : run?.FailureReason.IsNotBlank() == true
                ? run.FailureReason
                : "the failure was recorded without a reason";

        return run switch
        {
            { FailedGates.Count: > 0 } => $"gate failure ({string.Join(", ", run.FailedGates)}): {recorded}",
            { State.Value: "Killed" } => $"the run was killed: {recorded}",
            null when task.CurrentRunId is not null =>
                $"no run record exists for this failure: {recorded}",
            _ => recorded,
        };
    }

    /// <summary>
    /// A stalled row's cause is whatever the phase already observed: a process that is gone is a
    /// different problem from a process that is alive and quiet, and they take different levers.
    /// </summary>
    private static string StallCause(TaskPhase phase) => phase.Liveness switch
    {
        SessionLiveness.Gone =>
            "the run believes a session is running and its process is gone",
        SessionLiveness.Alive =>
            "the session is alive but its stream has been silent past the stall threshold",
        _ => "the agent stream has been silent past the stall threshold",
    };

    /// <summary>
    /// The shared reason every budget-parked row carries (backlog 40): the condition the
    /// run itself recorded, plus how many runs on this board it caught. Named once, with a
    /// count, rather than each row reading as its own unrelated failure the way the origin
    /// incident's three Failed rows did. A board holding exactly one says no count at all — "(1
    /// run waiting)" beside a single row is noise.
    /// </summary>
    private static string BudgetHoldCause(RunDetails run, int budgetParkedRuns)
    {
        string recorded = Reason(
            run.ParkedReason, "token budget exhausted - resumes when the subscription window resets");
        return budgetParkedRuns > 1 ? $"{recorded} ({budgetParkedRuns} runs waiting)" : recorded;
    }

    /// <summary>
    /// The review-parked row's lever, honest about the two parks where one of the two verdicts
    /// is refused (<c>ReviewResolveCommand</c>'s own guards, never advertised past their refusal).
    /// A disputed rebase conflict raised before any review pass ran
    /// (<c>task.FollowUpKind == Rebase &amp;&amp; run.ReviewCycle == 0</c>) has nothing to call
    /// "ready" — nothing has been rebased yet — so only --needs-fixes applies. A pr-review task's
    /// own park (<c>ResolvePrReviewAsync</c>) is the opposite refusal: there is no diff of its own
    /// to fix, so only --merge-ready applies.
    /// </summary>
    private static string ReviewParkLever(TaskListItem task, RunDetails run, string id) =>
        task.Type == TaskType.PrReview
            ? $"h9k review resolve {id} --merge-ready (a pr-review task has no diff of its own for a fix session; direct the findings report by hand first)"
            : task.FollowUpKind == FollowUpKind.Rebase && run.ReviewCycle == 0
                ? $"h9k review resolve {id} --needs-fixes \"<how to resolve the conflict>\""
                : $"h9k review resolve {id} --merge-ready (or --needs-fixes \"…\")";

    private static string Reason(string? recorded, string absent) =>
        recorded.IsNotBlank() ? recorded : absent;
}
