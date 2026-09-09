using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run;
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
    /// The ceiling <see cref="ConfigSetCommand.Validate"/>'s own <c>&gt;= 1</c> rule guards only on
    /// the CLI write path — a hand-edited config file skips that gate entirely (independent pre-PR
    /// review, cycle 1), and an unclamped value can turn negative (nudging every interactive claim
    /// immediately) or exceed <see cref="TimeSpan.MaxValue"/>'s ~10,675,199 days (throwing out of
    /// <see cref="TimeSpan.FromDays"/> and taking <c>h9k status</c> down with it). Ten years is far
    /// past any value an operator would set on purpose, so clamping here never changes real usage.
    /// </summary>
    internal const int MaxInteractiveClaimStaleAfterDays = 3650;

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
        int interactiveClaimStaleAfterDays = Domain.Infrastructure.Persistence.OperatingSettings.DefaultInteractiveClaimStaleAfterDays,
        string? machineName = null)
    {
        machineName ??= Environment.MachineName;
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

        // A deliberate headless start (h9k task start) whose session exited unattended and could
        // not be delivered automatically (task: a do-now session launched by h9k task start is
        // caught within seconds) — checked ahead of the dead-blocker, stalled, and stale-
        // interactive-claim arms below, all of which this row's own Dispatched/Running state and
        // Gone/NotApplicable liveness would otherwise also match, each with a less specific or
        // outright wrong cause (the stale-claim nudge, in particular, would advise re-attaching
        // with h9k task work as though a terminal were still open to it). RunSupervisor never
        // moves RunState for this flag, so this reads directly off the recorded reason rather than
        // any state transition.
        if (run?.ExitedUnattendedReason is { } exitedUnattendedReason
            && (run.State == Domain.Features.Run.RunState.Dispatched
                || run.State == Domain.Features.Run.RunState.Running))
        {
            // h9k task deliver is excluded only when RunSupervisor recorded it as confirmed to
            // refuse on this same ground (independent pre-PR review, cycle 1, both lenses:
            // RunUnattendedExitFlagged.DeliverConfirmedRefuses) — true for a definitively bad tree
            // (VerificationRunner.DetectStrandedWorkAsync found no commits beyond base, uncommitted
            // files, or both — the same check deliver itself runs, so deliver refuses on that same
            // ground every time) or a confirmed branch mismatch (TaskDeliverCommand's own
            // currentBranch != run.Branch check). It is false, and deliver is offered alongside the
            // rest, for the other variants this flag can name — an unreadable branch, an unreadable
            // git status (both of which TaskDeliverCommand only warns about and proceeds past
            // rather than refusing on), a process that vanished without reporting, or a plain error
            // result — none of which ever confirmed the tree was bad, so deliver may well succeed.
            // h9k task work always works (reattach and look by hand); h9k task handback and h9k
            // task release both work whenever there are no uncommitted files still sitting in the
            // worktree, and refuse the same files for the same reason otherwise — never advise a
            // lever the platform will refuse.
            string lever = run.ExitedUnattendedDeliverConfirmedRefuses
                ? $"h9k task work {id} (to look at it yourself), h9k task handback {id} (to queue a fresh "
                  + $"headless follow-up), or h9k task release {id} (to give it back) — the latter two refuse "
                  + "while uncommitted files remain, which h9k task work can still resolve by hand"
                : $"h9k task deliver {id} (it re-checks the worktree itself, so it may still succeed), "
                  + $"h9k task work {id} (to look at it yourself), h9k task handback {id} (to queue a fresh "
                  + $"headless follow-up), or h9k task release {id} (to give it back) — the latter two refuse "
                  + "while uncommitted files remain, which h9k task work can still resolve by hand";
            return new TaskAttention(AttentionLevel.NeedsYou, exitedUnattendedReason, lever);
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
        // Read through BlockingHoldReason rather than off DependencyFailureReason alone, so a
        // stacked child whose parent pull request closed unmerged reaches a human on exactly the
        // same footing as one whose local blocker died (task: a stacked child can stand on a pull
        // request another install owns). Both are holds nothing about them will clear.
        if (task.State == TaskState.Blocked && task.BlockingHoldReason.IsNotBlank())
        {
            return new TaskAttention(AttentionLevel.NeedsYou, task.BlockingHoldReason);
        }

        if (stalled)
        {
            return new TaskAttention(AttentionLevel.NeedsYou, StallCause(phase), $"h9k logs {id}");
        }

        // A Jira write is stuck on a rejected credential (Brian's design, 2026-08-28; the write
        // path's own transport moved off twg onto hall9k's REST client, Decisions Log #114) — a
        // handled, expected state rather than a crash, and one the operator clears by refreshing
        // the registered connection rather than through an h9k command that resubmits anything.
        // Checked after every park, failure, dead-blocker and stall arm above rather than ahead of
        // them (independent pre-PR review, cycle 1): the write carries no lifecycle state of its
        // own, so it must not be shadowed when nothing else is amiss — but a stuck write is never
        // the reason a run is parked, a task failed, a blocker died or a run stalled, and putting
        // it first hid whichever of those actually stopped the work behind this row instead.
        // Checked ahead of BudgetParked below, though (independent pre-PR review, cycle 2): a
        // budget park clears itself on a clock and is explicitly not an ask, so if it ran first it
        // would suppress a genuine needs-you row for as long as the budget window held, while the
        // retry sweep kept re-issuing the same doomed write underneath it.
        if (task.PendingJiraWriteIsAuthFailure)
        {
            // The fallback names no cause — "Jira rejected the credential" was the misattribution
            // this diff removed at every other site that reads this flag (AuthorizeAsync classifies
            // a credential the vault could never resolve, never asked about by Jira at all, the
            // same way as one Jira itself rejected), and the recorded reason is non-blank on every
            // path that exists today, so this only guards against ever falling back to a guess
            // (independent pre-PR review, conformance lens, cycle 8).
            return new TaskAttention(
                AttentionLevel.NeedsYou,
                Reason(task.PendingJiraWriteFailureReason, "a Jira write is pending on the registered connection — check it"),
                "h9k connection add jira --site https://your-org.atlassian.net --email you@example.com");
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
        // Checked after the Jira arm (independent pre-PR review, cycle 1: the ordering rationale
        // above applies here too — a stale claim is never the reason a Jira write failed to
        // authenticate, so it must not shadow the rejected-credential row either) and gated on the run's
        // own state (adversarial review, cycle 1): once h9k task deliver hands the run to the
        // standard pipeline the task can still read Claimed+interactive for the whole review
        // loop (TaskHandbackCommand and TaskWorkCommand both document this and refuse once
        // run.State is past Dispatched/Running), so nudging here too would advertise both levers
        // this row would in fact refuse — never advise a lever the platform will refuse. Also
        // gated on liveness (conformance review, cycle 2): the run-state guard alone still let
        // the nudge fire on a session this machine can see is Alive right now, offering the same
        // two levers — InteractiveSessionLiveness.EnsureNotAttachedElsewhere refuses both
        // h9k task work re-entry and h9k task handback with "still attached in another terminal"
        // for exactly that observation, and never overrides it even with --force. Restricted to
        // Gone, NotApplicable, or an Unobserved claim IsInteractiveSessionRecordedElsewhere below
        // reads as not-actually-elsewhere (adversarial review, cycle 6, following cycle 4's finding): a
        // session whose MachineName genuinely names a different machine also reads Unobserved, and
        // EnsureNotAttachedElsewhere refuses both levers for that one without --force — the
        // identical contradiction this arm exists to avoid, so that case alone stays excluded.
        // A blank MachineName is a different fact (a stream written before the field existed,
        // ActiveSession.cs's own doc comment): EnsureNotAttachedElsewhere reads it as unobservable
        // rather than "elsewhere" and proceeds without --force, so this arm can honestly nudge it
        // too — leaving it excluded would mean the oldest claims, the ones the nudge exists for,
        // could never be reached by it.
        bool reachedViaKnownGoneOrEnded = phase.Liveness is SessionLiveness.Gone or SessionLiveness.NotApplicable;
        bool reachedViaLegacyBlankMachineName = run is not null
            && phase.Liveness == SessionLiveness.Unobserved && !IsInteractiveSessionRecordedElsewhere(run, machineName);
        // task.Type != TaskType.PrReview excludes the same third Guid.Empty-claimed shape
        // TaskPhaseComposer's own Working() now excludes (independent pre-PR review, cycle 1,
        // both lenses): AutoPrReviewEngine's Now speed launches a headless pr-review run under
        // this identical sentinel, which is never an attended h9k task work claim, so nudging
        // toward h9k task work here — a command TaskWorkCommand refuses outright for a pr-review
        // task — would advise a lever the platform will refuse, the exact rule this arm's own
        // comment above states.
        if (task.State == TaskState.Claimed && task.IsInteractiveClaim && task.Type != TaskType.PrReview
            && run is not null
            && (run.State == Domain.Features.Run.RunState.Dispatched
                || run.State == Domain.Features.Run.RunState.Running)
            && (reachedViaKnownGoneOrEnded || reachedViaLegacyBlankMachineName)
            // RunDetails.LastInteractiveActivityAt null does not by itself mean never touched:
            // it is also what a document written before this field existed reads, forever, until
            // its claim's next attach or detach rewrites it for real (RunDetails.cs's own doc
            // comment on the field). InteractiveSessionCount is the tell that distinguishes the
            // two (conformance review, cycle 6) — it has been incremented by every
            // InteractiveSessionStarted since long before this field existed — and a claim with
            // count > 0 but no timestamp is exactly that pre-migration document: DispatchedAt is
            // not a fallback for its last touch, it is the claim's original start, which can
            // understate the real last touch by however long the claim has been open. Asserting
            // staleness off it would be the same unobserved-fact guess the wording two lines
            // below already refuses to make, so this arm stays silent on that claim rather than
            // risk telling an operator who was here an hour ago that they have not been seen in
            // days; it self-heals the moment any attach or detach next rewrites the document.
            // Scoped to the Gone/NotApplicable arm only (conformance review, cycle 7, following
            // cycle 6's own finding): a claim reached via reachedViaLegacyBlankMachineName cannot
            // be this ambiguous case in the first place — StartSession clears and re-adds the
            // session with the current MachineName and RunDetails.LastInteractiveActivityAt
            // together on every InteractiveSessionStarted/Ended, so a still-blank MachineName is
            // itself the proof that no touch has landed since MachineName tracking began, not an
            // absence of proof. Excluding this arm from the guard is what makes the oldest claims
            // — the ones the broadening above exists to reach — nudgeable at all; leaving the
            // guard unscoped made that broadening dead code, since every claim it could reach also
            // satisfies this guard's own suppression condition.
            && !(reachedViaKnownGoneOrEnded && run.LastInteractiveActivityAt is null && run.InteractiveSessionCount > 0))
        {
            TimeSpan age = now - (run.LastInteractiveActivityAt ?? run.DispatchedAt);
            if (age >= TimeSpan.FromDays(Math.Clamp(interactiveClaimStaleAfterDays, 1, MaxInteractiveClaimStaleAfterDays)))
            {
                // A claim that never recorded a touch (no InteractiveSessionStarted yet) says so
                // rather than claiming one happened (conformance review, cycle 2: the old wording
                // asserted "was last touched" for this case too, contradicting the never-guess
                // rule stated three lines above it). The touched case hedges the same way
                // (adversarial review, cycle 4): LastInteractiveActivityAt only moves on an
                // attach or detach, so h9k task verify's own gate runs on this same claim leave
                // no trace here, and asserting "was last touched" would claim a fact — that this
                // was the most recent activity — this field cannot actually see.
                // InteractiveSessionCount > 0 with LastInteractiveActivityAt null is a claim that
                // recorded a touch before this machine started tracking exact touch times (the
                // legacy-blank-MachineName arm can reach a claim like this: a document whose
                // session started before MachineName and LastInteractiveActivityAt began being
                // set together). Saying "has not recorded a touch since" there would contradict
                // the count sitting right next to it, so that case gets its own honest wording
                // instead of either the never-touched or the precise-last-touch claim.
                string activity = run.LastInteractiveActivityAt is not null
                    ? $"last recorded activity {TaskStatusComposer.RelativeAge(age)}"
                    : run.InteractiveSessionCount > 0
                        ? $"was claimed {TaskStatusComposer.RelativeAge(age)}, with activity recorded before this machine tracked exact touch times"
                        : $"was claimed {TaskStatusComposer.RelativeAge(age)} and has not recorded a touch since";
                return new TaskAttention(
                    AttentionLevel.NeedsYou,
                    $"an interactive claim (h9k task work) {activity} — still yours, or ready to hand off?",
                    $"h9k task work {id} if you're still on it, or h9k task handback {id} to finish it headlessly");
            }
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
            return Delivered(task, run, id, now);
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
    private static TaskAttention Delivered(TaskListItem task, RunDetails? run, string id, DateTimeOffset now)
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
            "AwaitingReview" => AwaitingReviewAttention(task, run, now),
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
            // A pull request the orphan sweep can find (Decisions Log #72, and its counterpart
            // for h9k task resolve --pr, backlog: a pull request recorded by h9k task resolve
            // --pr is observed to merge like any other) is not unwatched — its merge still
            // completes closeout on its own, only follow-ups do not — so the cause must not
            // claim nothing is watching when the sweep's own candidate query would in fact
            // find this row.
            "Failed" or "Killed" => IsOrphanSweepCandidate(task, run)
                ? new TaskAttention(AttentionLevel.NeedsYou,
                    "the run ended without a merge being observed; this pull request is still eligible "
                    + "for closeout's merge observation, but nothing will fix or follow up on it: "
                    + $"{Reason(run.FailureReason, "the run recorded no reason")}",
                    UnwatchedRemedy(task, run, id))
                : new TaskAttention(AttentionLevel.NeedsYou,
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
    private static TaskAttention AwaitingReviewAttention(TaskListItem task, RunDetails run, DateTimeOffset now) =>
        // Ahead of both arms below, because it outranks whatever either would say: an
        // un-retargeted stacked pull request is not at the merge bar at all (task: the bar
        // machinery treats an un-retargeted stacked PR as not at the bar), so "the merge is yours"
        // and "the daemon merges it on its own" are both false of it — the one would hand the
        // reader a merge that would land this branch's commits on its parent's branch, and the
        // other would promise something the daemon's own gate refuses. Waiting-but-handled rather
        // than red: the retarget arrives on its own the moment the parent merges.
        run.StackedOnBranch is { } parentBranch
            ? new TaskAttention(
                AttentionLevel.WaitingHandled,
                $"stacked on `{parentBranch}` and its pull request still targets that branch — nothing for "
                + "you here until its parent merges, which retargets this one onto the base branch and "
                + "replays it there automatically")
            : task.EffectivePreApproval.MergesAutomatically
                ? PreApprovedAwaitingReviewAttention(task, run, now)
                : AwaitingReviewAttention(run);

    /// <summary>
    /// The pre-approved arm (task: a task can be published pre-approved): a synchronous human gate
    /// is exactly what this flag removes, so nothing here is ever <see cref="AttentionLevel.NeedsYou"/>
    /// — a required human approval or an outstanding requested reviewer is stated as a visible,
    /// self-resuming wait (design ruling 3), named plainly so the owner can take whatever social
    /// action they choose on their own initiative; the platform itself never nudges. "Named" is
    /// literal since the people a pull request is waiting on became nameable: wherever the last
    /// observation recorded logins, this says whose review the merge is waiting on rather than
    /// "human approval", because the owner's only lever here is social and an anonymous wait does
    /// not tell them who to talk to. Age is
    /// <see cref="RunDetails.PullRequestPushedAt"/> when this run's own opener call recorded one —
    /// the pull request's own opened/updated timestamp — falling back to the run's dispatch time for
    /// a stream recorded before that field existed, named honestly as "dispatched" rather than
    /// "open" so the fallback is never read as an observed fact it is not (independent pre-PR
    /// review, cycle 1, adversarial finding).
    /// </summary>
    private static TaskAttention PreApprovedAwaitingReviewAttention(
        TaskListItem task, RunDetails run, DateTimeOffset now)
    {
        PreApprovalMode mode = task.EffectivePreApproval;
        List<string> waitingOn = [];
        // The daemon's own gate checks this ahead of everything else (CloseoutEngine's
        // HasPendingChecks short-circuit runs before the review-decision and outstanding-reviewer
        // reads it feeds this composer), so an incomplete CI picture must not let the "GitHub's
        // own gates read satisfied" claim below fire while checks are still reporting and the
        // daemon is in fact refusing to merge (independent pre-PR review, cycle 1, both lenses).
        if (run.ExternalReviewChecksPending)
        {
            waitingOn.Add("CI checks to finish reporting");
        }

        if (run.ExternalReviewState == Domain.Features.Run.ExternalReviewState.RequestedPending)
        {
            waitingOn.Add("copilot review");
        }

        // CloseoutEngine's own unresolved-thread gate sits ahead of the auto-merge read it feeds
        // this composer, and returns before ever reaching it while any thread is still open —
        // including one this run's own follow-up already declined or routed and is deliberately
        // leaving open for the human to close (Decisions Log #159). RunDetails.UnresolvedReviewThreads
        // is the wrong field to read that off: it is only ever written by ReviewFeedbackReceived,
        // which lands on the run that DISCOVERED the threads and immediately moves that run to
        // ReviewPending then Superseded in the same sweep (RunDetails.Apply(ReviewFeedbackReceived),
        // CloseoutEngine's dispatch right after appending it) — so the run actually sitting here in
        // AwaitingReview is always a *different* run (the dispatched follow-up), whose own count is
        // never anything but its own default. The gate's "nothing left for a follow-up to act on"
        // skip therefore never sets it either, leaving the clause permanently dead in the one
        // scenario it exists for; on the run that DID once receive a count, the same field only ever
        // grows, so a stale non-zero value can equally survive a park-resolve-and-push round trip
        // that fixed everything (independent pre-PR review, cycle 4, both lenses). LastReviewThreadOutcomes
        // does not have either failure: Apply(ReviewThreadsTriaged) fully replaces it, on this exact
        // run's own stream, every time a triage actually runs — which by construction is always
        // before this run can ever reach AwaitingReview carrying a thread left open on purpose,
        // since only Decline/Route ever leaves one open (Decisions Log #159; Fix either lands or the
        // thread stays counted as outstanding and keeps buying follow-ups instead of idling here).
        //
        // Scoped to IsHuman == true, not every Decline/Route outcome: the design this field
        // exists to reflect (Decisions Log #159) only ever leaves a HUMAN-authored thread open on
        // purpose — a bot-authored one (Copilot, another agent) is resolved by this same triage
        // run before it ever pushes (resolve-review-threads skill step 9), so it stops being
        // genuinely unresolved the moment this run reaches AwaitingReview. LastReviewThreadOutcomes
        // itself is never revisited after that point (Apply(ReviewThreadsTriaged) only ever fires
        // on this run's own triage), so counting every outcome regardless of author kind would
        // report an already-closed bot thread as still blocking the merge for the entire remaining
        // life of this run (independent pre-PR review, cycle 6, adversarial finding). This mirrors
        // CloseoutEngine's own scoping of its "already answered" exclusion to human threads
        // (CloseoutEngine.cs, snapshot.HumanThreadIds) — that gate reads a live GitHub actor-type
        // classification instead of this outcome's own self-reported IsHuman, because a live
        // provider read is available there; this composer has no live snapshot to read at command
        // time, so the self-report is the only signal on hand, and using it here is a read for
        // display, not the "may an agent resolve this thread" decision IsHuman's own doc warns
        // against trusting it for.
        IReadOnlyList<ReviewThreadOutcome> openThreads = [.. run.LastReviewThreadOutcomes
            .Where(outcome => (outcome.Disposition == ReviewThreadDisposition.Decline
                    || outcome.Disposition == ReviewThreadDisposition.Route)
                && outcome.IsHuman == true)];
        if (openThreads.Count > 0)
        {
            waitingOn.Add($"{openThreads.Count} unresolved review thread(s) ({openThreads.Count} from a human) to close");
        }

        // Named rather than reported as an anonymous "human approval" wherever the last observation
        // actually recorded who (task: the people a pull request is waiting on are named): the
        // owner's only lever here is social, and "waiting on human approval" does not tell them who
        // to talk to. The anonymous wording survives as the honest fallback for the one shape that
        // has no login to name — a branch rule demanding a review nobody has been asked for yet.
        // Which is exactly why the mode's own clause below can retire it: under after-human-review
        // with reviewers recorded as awaiting approval, there IS somebody to name, and stating the
        // same wait twice — once anonymously and once by login — is the doubling both clauses were
        // corrected for (conformance review, cycle 1).
        HumanReviewWait humanWait = ReadHumanReviewWait(run);
        bool modeNamesSomebody = mode.WaitsForHumanReview
            && run.ExternalHumanReviewEverRequested == true
            && run.ExternalHumanReviewersAwaitingApprovalLogins.Count > 0;
        waitingOn.AddRange(NamedHumanReviewWaits(humanWait, suppressAnonymousApproval: modeNamesSomebody));

        // The after-human-review mode's own two gates (task: ... and pre-approval gains a mode that
        // waits for human review), stated in the same list as everything else the merge is waiting
        // on, because to the reader they are the same kind of fact. Both are the owner's to clear,
        // and unlike the gates above them, the first one names how. It is handed the wait the line
        // above was built from so it can avoid saying the same person twice.
        if (mode.WaitsForHumanReview)
        {
            waitingOn.AddRange(AfterHumanReviewWaits(task, run, humanWait));
        }

        if (waitingOn.Count == 0)
        {
            return new TaskAttention(
                AttentionLevel.WaitingHandled,
                mode.WaitsForHumanReview
                    ? "pre-approved after-human-review — GitHub's own gates read satisfied and every requested "
                        + "reviewer has approved the current head; the daemon merges it on its own"
                    : "pre-approved — GitHub's own gates read satisfied; the daemon merges it on its own");
        }

        // RunDetails carries no PR-opened timestamp on a stream recorded before
        // PullRequestPushedAt existed, so that gap is named rather than papered over with the
        // run's own dispatch age, which can understate or overstate how long the pull request has
        // actually been open by however long the build session ran before it pushed (independent
        // pre-PR review, cycle 1, adversarial finding).
        bool openAgeObserved = run.PullRequestPushedAt is not null;
        string age = TaskStatusComposer.RelativeAge(now - (run.PullRequestPushedAt ?? run.DispatchedAt));
        string ageClause = openAgeObserved ? $"open {age}" : $"dispatched {age}";

        // "Nothing for you to do here" is true of every wait on this list except one: under
        // after-human-review with no reviewer ever requested, the merge waits indefinitely on an
        // act only the owner can perform, and telling them there is nothing to do would be the
        // sentence that leaves the pull request sitting forever. The level stays WaitingHandled
        // either way (design ruling 3: nothing on a pre-approved task's arm is ever NeedsYou),
        // because this wait is the owner's own standing instruction working as they asked.
        // Deliberately not "that first item": the reviewer-request wait is appended last, after CI
        // and Copilot, so naming it by position would be wrong the moment anything else is also
        // outstanding. It names the act instead.
        bool ownerMustAct = mode.WaitsForHumanReview && run.ExternalHumanReviewEverRequested == false;
        string closing = ownerMustAct
            ? " — it merges automatically once satisfied, but requesting a reviewer is yours to do; "
                + "nothing here will do it for you"
            : " — it merges automatically once satisfied; nothing for you to do here, though you may want "
                + "to nudge a reviewer yourself";
        return new TaskAttention(
            AttentionLevel.WaitingHandled,
            $"pre-approved; waiting on {string.Join(" and ", waitingOn)} for the pull request "
            + $"({ageClause}){closing}");
    }

    /// <summary>
    /// What the last observation says a human still owes this pull request — the one reading of
    /// that question, shared by the pre-approved arm and the unflagged one so the two can never
    /// disagree about whether a person is owed anything (task: the people a pull request is waiting
    /// on are named). Only the grammar differs between them, which is why this carries the facts
    /// rather than the sentence.
    /// <para>
    /// The three shapes are deliberately not mutually exclusive: a reviewer can have requested
    /// changes while a second reviewer's request is still open, and a branch rule can demand an
    /// approval nobody has been asked for at all. That last one is the shape with nobody to name,
    /// and so is a <c>CHANGES_REQUESTED</c> verdict whose author the last observation did not
    /// record — both state the fact without inventing a login for it, or a reason for its absence
    /// (AGENTS.md, never guess at unobserved facts).
    /// </para>
    /// </summary>
    private sealed record HumanReviewWait(
        IReadOnlyList<string> Outstanding,
        IReadOnlyList<string> ChangesRequestedBy,
        bool ChangesRequested,
        bool UnnamedApprovalRequired);

    private static HumanReviewWait ReadHumanReviewWait(RunDetails run)
    {
        bool changesRequested = run.ExternalReviewDecision == "CHANGES_REQUESTED";
        return new HumanReviewWait(
            run.ExternalOutstandingHumanReviewerLogins,
            changesRequested ? run.ExternalChangesRequestedByLogins : [],
            changesRequested,
            // REVIEW_REQUIRED (or any other unsatisfied verdict) with nobody requested: a branch
            // rule wants an approval and no reviewer has been asked for one, so there is no login
            // to name.
            !changesRequested
            && run.ExternalReviewDecision is not (null or "APPROVED")
            && run.ExternalOutstandingHumanReviewerLogins.Count == 0);
    }

    /// <summary>
    /// <see cref="ReadHumanReviewWait"/> phrased for the pre-approved arm, whose host sentence is
    /// "waiting on X and Y for the pull request". Empty when nothing human is outstanding, which is
    /// what restores that arm's original all-clear line rather than leaving a hedge behind.
    /// <para>
    /// <paramref name="suppressAnonymousApproval"/> drops the anonymous fallback clause when the
    /// caller is about to name the same wait by login: "human approval" exists for the shape with
    /// nobody to name, and it is not that shape once somebody is named.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> NamedHumanReviewWaits(
        HumanReviewWait wait, bool suppressAnonymousApproval) =>
        [
            .. wait.Outstanding.Count > 0 ? (string[])[$"review from {Logins(wait.Outstanding)}"] : [],
            .. wait.ChangesRequestedBy.Count > 0
                ? (string[])[$"the changes {Logins(wait.ChangesRequestedBy)} requested"]
                : wait.ChangesRequested ? (string[])["the changes requested on the pull request"] : [],
            .. wait.UnnamedApprovalRequired && !suppressAnonymousApproval
                ? (string[])["human approval"]
                : [],
        ];

    /// <summary>
    /// The two gates <see cref="PreApprovalMode.AfterHumanReview"/> adds on top of GitHub's own,
    /// in the same voice as everything else the merge is waiting on. Empty once both are clear,
    /// which is what lets the caller fall through to its own all-clear line.
    /// <para>
    /// Never observed is its own answer, not folded into "no reviewer requested": the merge holds
    /// either way, but only one of the two is a fact about GitHub, and the remedy differs (add a
    /// reviewer, versus wait for a sweep to look). Neither clause names the pull request itself,
    /// because the caller's host sentence already ends in "for the pull request" and a clause that
    /// named it too would double the phrase (conformance review, cycle 1).
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> AfterHumanReviewWaits(
        TaskListItem task, RunDetails run, HumanReviewWait wait) =>
        run.ExternalHumanReviewEverRequested switch
        {
            null =>
            [
                "a closeout sweep to observe whether a human review has been requested",
            ],
            false =>
            [
                "a human reviewer to be requested at all — you add one in GitHub, or switch this task to "
                    + $"plain pre-approval with h9k task set-pre-approved {TaskListCommand.ShortId(task.Id)} on",
            ],
            true => AwaitingApprovalWaits(run, wait),
        };

    /// <summary>
    /// Who the after-human-review gate is holding for, minus anyone the caller's own line has
    /// already named. A reviewer whose request is still outstanding is, by construction, also
    /// awaiting approval, and so is one whose verdict requests changes — both are already named by
    /// <see cref="NamedHumanReviewWaits"/>, so repeating them here said the same login twice in one
    /// sentence (conformance review, cycle 1). Empty when everyone left is already named, which is
    /// correct rather than an all-clear: the caller's list is non-empty precisely because it named
    /// them.
    /// </summary>
    private static IReadOnlyList<string> AwaitingApprovalWaits(RunDetails run, HumanReviewWait wait)
    {
        IReadOnlyList<string> unnamed =
        [
            .. run.ExternalHumanReviewersAwaitingApprovalLogins.Where(login =>
                !wait.Outstanding.Contains(login, StringComparer.OrdinalIgnoreCase)
                && !wait.ChangesRequestedBy.Contains(login, StringComparer.OrdinalIgnoreCase)),
        ];

        return unnamed.Count > 0 ? [$"approval of the current head from {Logins(unnamed)}"] : [];
    }

    /// <summary>One login, or several, in the backtick style the rest of these causes quote a branch or a ref in.</summary>
    private static string Logins(IReadOnlyList<string> logins) =>
        string.Join(", ", logins.Select(login => $"`{login}`"));

    /// <summary>
    /// The named-reviewer wait on the UNFLAGGED path (task: the people a pull request is waiting on
    /// are named), which outranks every Copilot reading below it: while a requested human reviewer
    /// is outstanding, or a review's verdict requests changes, "the merge is yours" is the wrong
    /// sentence whatever Copilot has or has not done — it would hand the owner a merge that lands
    /// past a review somebody is still owed. Null once nothing human is outstanding, which restores
    /// the original wording exactly.
    /// <para>
    /// The level splits on whether the ball is actually in the owner's court, which is not the same
    /// question as who owns the merge: an outstanding request is a handled wait (somebody was asked
    /// and has not answered), a <see cref="AttentionLevel.NeedsYou"/> marker beside an "awaiting
    /// review" sentence being exactly the word-versus-marker contradiction this file already
    /// corrects elsewhere; a <c>CHANGES_REQUESTED</c> verdict that survived thread triage is
    /// NeedsYou, because the reviewer has spoken and nothing in the platform will answer them.
    /// </para>
    /// </summary>
    private static TaskAttention? NamedHumanReviewWaitAttention(RunDetails run)
    {
        HumanReviewWait wait = ReadHumanReviewWait(run);

        // UnnamedApprovalRequired is deliberately NOT a wait on this path: a branch rule demanding
        // an approval nobody has been asked for is exactly the state where the owner's own review
        // and merge is the answer, so the original wording — which sends them to the pull request —
        // is already right, and there is nobody to name anyway.
        IReadOnlyList<string> outstanding = wait.Outstanding;
        IReadOnlyList<string> changesRequestedBy = wait.ChangesRequestedBy;
        if (outstanding.Count == 0 && !wait.ChangesRequested)
        {
            return null;
        }

        string cause = (outstanding.Count, changesRequestedBy.Count) switch
        {
            ( > 0, > 0) =>
                $"awaiting review from {Logins(outstanding)} (request still outstanding) and "
                + $"{Logins(changesRequestedBy)} (requested changes) — nudge them, or settle it on GitHub "
                + "yourself by dismissing the review or withdrawing the request",
            ( > 0, 0) when wait.ChangesRequested =>
                $"awaiting review from {Logins(outstanding)}, whose request is still outstanding, and the "
                + "review decision requests changes — the last observation does not name whose verdict "
                + "that is, so read it on GitHub",
            ( > 0, 0) =>
                $"awaiting review from {Logins(outstanding)} — their review request is still outstanding, so "
                + "this is not a settled merge yet; nudge them, or withdraw the request on GitHub",
            (0, > 0) =>
                $"awaiting review from {Logins(changesRequestedBy)} — their review requests changes, and that "
                + "verdict stands until they look again; answer them, or dismiss the review on GitHub",
            // CHANGES_REQUESTED with no author recorded. Several shapes reach here and the display
            // cannot tell them apart, so it claims none of them: an observation from a build
            // before the author was collected, a verdict whose review fell outside the capped
            // verdict read, or one authored by an account the human filter excludes. The verdict
            // is stated without a login, or a reason for its absence, being invented for it
            // (AGENTS.md, never guess at unobserved facts; adversarial review, cycle 1).
            _ =>
                "awaiting review — the review decision requests changes, and the last observation does not "
                + "name whose verdict that is; read it on GitHub",
        };

        // The caveat every arm below this one already carries, kept rather than dropped along with
        // the wording it replaces: this cause pre-empts the "read its checks first" hedge, so on a
        // row where the CI picture was still incomplete as of the last observation it has to carry
        // that hedge itself, or a reader who settles the review side would merge on an unread CI
        // result (the same defect the Landed and None arms below were each corrected for).
        string checksCaveat = run.ExternalReviewChecksPending
            ? "; its checks may still be reporting too"
            : string.Empty;

        // NeedsYou only when a verdict is actually waiting on the owner. An outstanding request is
        // somebody else's turn — the same reading the Copilot arm below already gives its own
        // pending request ("not the human's turn yet, so it renders waiting-but-handled rather than
        // red"), and the same one the pre-approved arm gives this identical fact, so the two paths
        // do not contradict each other about who is holding the merge. A CHANGES_REQUESTED verdict
        // with the threads already resolved is the opposite: the reviewer HAS spoken, nothing in
        // the platform will move it, and the owner is the one who answers them or dismisses it.
        AttentionLevel level = wait.ChangesRequested ? AttentionLevel.NeedsYou : AttentionLevel.WaitingHandled;
        return new TaskAttention(level, cause + checksCaveat, run.PullRequestUrl ?? string.Empty);
    }

    private static TaskAttention AwaitingReviewAttention(RunDetails run) =>
        NamedHumanReviewWaitAttention(run) ?? CopilotAwaitingReviewAttention(run);

    private static TaskAttention CopilotAwaitingReviewAttention(RunDetails run) => run.ExternalReviewState.Value switch
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
    /// Mirrors <c>CloseoutEngine.PollOnceAsync</c>'s own orphan candidate filter's two document
    /// fields — a recorded pull request number, and a failure reason that is not <see
    /// cref="RunDetails.PullRequestClosedWithoutMerge"/> (the sweep already learned everything an
    /// inspection there could tell it and excludes that row from its own candidate set) — so this
    /// pane and <see cref="TaskPhaseComposer"/>'s phase line never claim a pull request is
    /// unwatched when the sweep's query would actually match this row. Deliberately not the
    /// sweep's own <c>r.NodeId == nodeId</c> scoping: a run dispatched from another node is a
    /// candidate here even though no sweep on this machine will ever match it, so a multi-node
    /// install can still see this claim outlive the node that would have to act on it.
    /// <para>
    /// Also mirrors <c>CloseoutEngine.NeedsMissingRunSweep</c>'s other admitted shape and the five
    /// guards <c>InspectMissingRunAsync</c> revalidates before acting on it (independent pre-PR
    /// review, cycle 1, both lenses): a terminal, not-Completed run carrying no pull-request number
    /// of its own, whose owning task nonetheless recorded one, is not a <c>pr-review</c> task (that
    /// type's own <c>PullRequestUrl</c> names the pull request it reviewed, never one of its own,
    /// per <c>TaskResolveCommand</c>'s identical guard), that recorded URL actually parses to a
    /// pull-request number (<see cref="PullRequestUrls.ParseNumber"/> — the one half of
    /// <c>InspectMissingRunAsync</c>'s own <see cref="PullRequestUrls.IsSafePullRequestUrl"/> check
    /// this composer can apply without a project's repository URL, which it has no way to load; the
    /// repository-match half stays the sweep's alone to enforce), and the run's own failure reason
    /// is not already <see cref="RunDetails.PullRequestClosedWithoutMerge"/> — the same exclusion
    /// the first arm carries, needed here too because <c>InspectMissingRunAsync</c>'s own
    /// <c>RecordClosedAsync</c> call can record exactly that reason onto this intact run's stream,
    /// which permanently excludes it from <c>NeedsMissingRunSweep</c> without ever setting a pull-
    /// request number (independent pre-PR review, cycle 1, both lenses). The missing-run sweep completes
    /// that shape's closeout directly against the intact record — it is watched, even though
    /// <paramref name="run"/>'s own <c>PullRequestNumber</c> reads null — so without this second arm
    /// this pane told the reader nothing was watching a pull request the daemon was about to close
    /// out on its own within one poll interval. Without the number guard, though, the same arm
    /// claimed the opposite lie for a <c>--pr</c> URL that names no pull request at all (a non-
    /// pull-request URL such as an issue): <c>TaskResolveCommand</c> records such a URL onto the
    /// task stream verbatim once no run stream exists to protect (<c>RunLauncher.cs</c>'s own doc
    /// block), and its own stderr already tells the operator closeout will not watch it — this
    /// pane must not contradict that in the same breath. A foreign-repository <c>--pr</c> URL still
    /// parses to a real number, so this guard alone does not catch it; the repository-match half
    /// stays the sweep's alone to enforce, and this pane has no project repository URL to check
    /// it against.
    /// </para>
    /// </summary>
    internal static bool IsOrphanSweepCandidate(TaskListItem task, RunDetails run) =>
        (run.PullRequestNumber is > 0 && run.FailureReason != RunDetails.PullRequestClosedWithoutMerge)
        || (run.PullRequestNumber is null
            && task.Type != TaskType.PrReview
            && task.PullRequestUrl.IsNotBlank()
            && PullRequestUrls.ParseNumber(task.PullRequestUrl) > 0
            && run.State.IsTerminal
            && run.State != Domain.Features.Run.RunState.Completed
            && run.FailureReason != RunDetails.PullRequestClosedWithoutMerge);

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
    /// The review-parked row's lever, honest about the parks where one of the two verdicts
    /// is refused (<c>ReviewResolveCommand</c>'s own guards, never advertised past their refusal)
    /// or accepted but useless. A disputed rebase conflict raised before any review pass ran
    /// (<c>task.FollowUpKind == Rebase &amp;&amp; run.ReviewCycle == 0</c>), or a mandatory
    /// final-pass pre-flight rebase's own disputed conflict at any cycle
    /// (<see cref="RunDetails.ParkedOnRebaseRecoveryDispute"/>, task: a run rebases its branch
    /// onto the current base branch), has nothing to call
    /// "ready" — nothing has been rebased yet — so only --needs-fixes applies. A pr-review task's
    /// own park (<c>ResolvePrReviewAsync</c>) is the opposite refusal: there is no diff of its own
    /// to fix, so only --merge-ready applies. A cap-0 takeover park or the lifetime-budget park
    /// (<see cref="RunDetails.ParkedNeedsFixesOffersNoProgress"/>) accepts --needs-fixes rather
    /// than refusing it, but a grant never clears this particular park — a per-track cap-0 never
    /// even dispatches a fix session, while a final-full-pass cap-0 or the lifetime-budget park
    /// dispatch one but re-park right behind it, since neither the cap nor the budget resets — so
    /// the lever does not claim more than "will not clear this park" and does not advertise the
    /// command as though it settled anything (independent pre-PR review, cycle 5, adversarial
    /// lens: offering it anyway contradicted the reason printed directly above it; cycle 1,
    /// adversarial lens: the original wording overclaimed "no progress at all", which is false
    /// wherever a fix session genuinely dispatches before the identical re-park).
    /// </summary>
    private static string ReviewParkLever(TaskListItem task, RunDetails run, string id) =>
        // Interactive mode's own routine boundary gate (task: interactive mode becomes a recorded
        // property of the task) checked first: it is never a dispute or a cap/budget park, so none
        // of the refinements below apply — h9k review proceed is the lever, with h9k review resolve
        // named alongside it for whenever the human wants to redirect the boundary instead.
        run.ParkedIsInteractiveGate
            // h9k review fixed is named without claiming this particular park is the one it
            // applies to (task: a human at the wheel takes the fix role herself): only the
            // review-verdict-to-fix boundary takes it, and which of the four boundaries this park
            // is is not a fact this projection carries — the park reason printed directly above
            // this lever is, and it names exactly the choices that apply. Naming the verb here
            // anyway is what makes it discoverable at all from the attention pane; asserting it
            // applies would be the guess.
            ? $"h9k review proceed {id} (or h9k review resolve {id} --merge-ready / --needs-fixes \"…\" to redirect it, or h9k review fixed {id} once your own fix is committed — the reason above names which apply)"
            // A changes-requested disagreement park (task: a changes-requested pull-request review
            // from a human becomes a fix lap) asks a question none of the arms below do: what the
            // reviewer hears. It is checked ahead of them because the verdict alone is not
            // accepted there — ReviewResolveCommand refuses a resolve that names no reply choice —
            // so a lever offering only --merge-ready/--needs-fixes would be a command that fails.
            : run.ParkedOnReviewDisagreement
                ? $"h9k review resolve {id} --merge-ready (or --needs-fixes \"…\") plus one of --post-reply-as-written / --post-reply \"…\" / --post-nothing — the reviewer has heard nothing yet"
                : task.Type == TaskType.PrReview
                    ? $"h9k review resolve {id} --merge-ready (a pr-review task has no diff of its own for a fix session; direct the findings report by hand first)"
                    : (task.FollowUpKind == FollowUpKind.Rebase && run.ReviewCycle == 0) || run.ParkedOnRebaseRecoveryDispute
                        ? $"h9k review resolve {id} --needs-fixes \"<how to resolve the conflict>\""
                        : run.ParkedNeedsFixesOffersNoProgress
                            ? $"h9k review resolve {id} --merge-ready (--needs-fixes will not clear this park — raise the cap or budget first, per the reason above)"
                            : $"h9k review resolve {id} --merge-ready (or --needs-fixes \"…\")";

    private static string Reason(string? recorded, string absent) =>
        recorded.IsNotBlank() ? recorded : absent;

    /// <summary>
    /// The same "is this session actually elsewhere" question
    /// <see cref="InteractiveSessionLiveness.EnsureNotAttachedElsewhere"/> answers before it
    /// refuses: true only for a recorded <see cref="Domain.Features.Run.ActiveSession.MachineName"/>
    /// that names a real, different machine. A blank name is unobservable rather than elsewhere,
    /// and reads false here exactly as it does there.
    /// <para>
    /// Compares against the caller's own <paramref name="machineName"/> rather than
    /// <see cref="Environment.MachineName"/> directly, so this reads "is this machine" the same
    /// way <see cref="TaskStatusComposer.SessionOnThisMachine"/> does — through the
    /// <see cref="TaskStatusContext.MachineName"/> seam — instead of a second, independent answer
    /// to the identical question that only happens to agree in production (adversarial review,
    /// cycle 1: the two disagreed under a test-supplied machine name, since only production sets
    /// <c>TaskStatusContext.MachineName</c> from <c>Environment.MachineName</c>).
    /// </para>
    /// </summary>
    private static bool IsInteractiveSessionRecordedElsewhere(RunDetails run, string machineName) =>
        run.ActiveSessions.Exists(session =>
            session.Role == Domain.Features.Run.AgentRole.Interactive
            && session.MachineName.IsNotBlank()
            && session.MachineName != machineName);
}
