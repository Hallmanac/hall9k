using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The derived-facts line under a Published row (Decisions Log #66). The Status column says
/// Published and stops; everything that distinguishes one published task from another —
/// unassigned, assigned and not yet claimed, waiting on blockers — is a fact composed here.
/// <para>
/// This is what lets the persisted model stay untouched in this pass. Queued and Blocked keep
/// their streams and their transitions; they simply stop being lifecycle words a human reads and
/// become facts on this line. When the ranking model retires them (IDEA-ranking-and-grooming),
/// only this composer changes.
/// </para>
/// <para>
/// <b>The ranking slot.</b> The line is an ordered list of facts rather than a sentence
/// precisely so ranking facts can join it without a second redesign: unranked / ranked /
/// expedited, and available / held, are appended here beside the dispatch facts when the ranking
/// model records them. Nothing about ranking is built now and no unranked count is reported
/// anywhere — the column exists, the facts arrive later.
/// </para>
/// </summary>
internal static class PublishedFacts
{
    private const string QueuePriorityFact =
        "marked queue-first — takes the next free dispatch slot regardless of assignment age";

    /// <summary>
    /// The pre-approval fact, one wording per automatic mode (task: the people a pull request is
    /// waiting on are named, and pre-approval gains a mode that waits for human review): the two
    /// modes promise different things about who has to speak before the merge, so a single sentence
    /// for both would tell an after-human-review task's owner their pull request merges on GitHub's
    /// gates alone. Off produces no fact at all — every caller already gates on
    /// <see cref="PreApprovalMode.MergesAutomatically"/> — and an unrecognized mode says so rather
    /// than being described as one of the two it might have been.
    /// </summary>
    private static string PreApprovedFact(PreApprovalMode mode) => mode.Value switch
    {
        "On" => "pre-approved — the daemon will merge its pull request on its own once GitHub's gates "
            + "are satisfied",
        "AfterHumanReview" => "pre-approved after-human-review — the daemon will merge its pull request on "
            + "its own once GitHub's gates are satisfied AND every human reviewer requested on it has "
            + "approved the current head",
        _ => $"pre-approved, but the recorded mode ({mode.Value}) is not one this build knows",
    };

    /// <summary>
    /// Who holds a HeldElsewhere row's claim, and since when (idea 202383dc, M2a). A foreign
    /// node's own friendly name never replicates (<c>NodeDetails</c> is node-scoped), so the
    /// claiming owner's cross-node root fingerprint is what names the holder — truncated to a
    /// short, readable prefix the same way a task or run id is shortened elsewhere on this board —
    /// falling back to the bare node id on a claim recorded before that fingerprint existed.
    /// </summary>
    private static string HeldElsewhereFact(TaskListItem task, DateTimeOffset now)
    {
        string holder = task.ClaimedByOwnerRootFingerprint.IsNotBlank()
            ? task.ClaimedByOwnerRootFingerprint![..Math.Min(12, task.ClaimedByOwnerRootFingerprint!.Length)]
            : task.ClaimedByNodeId?.ToString() ?? "an unknown node";
        string since = task.ClaimedAt is { } claimedAt
            ? TaskStatusComposer.RelativeAge(now - claimedAt)
            : "an unknown time";
        return $"held by {holder} since {since}";
    }

    /// <summary>
    /// What a Published row is actually waiting for, oldest question first: whether a human has
    /// assigned it at all, then what the platform is waiting on. Empty for any row that is not
    /// Published, which is what keeps the line from appearing where it would say nothing — except
    /// for the queue-first marker (task 45136b29), which is stated wherever it is set, Published
    /// or not, since the decider allows setting it on a currently-Claimed task too.
    /// </summary>
    /// <param name="held">
    /// The measured limit holding this row back — this node's own ceiling, or its project's own
    /// cap (<see cref="QueueHold"/>, Decisions Log #64, #140) — or null when neither was measured
    /// to be full. A queued row says it is ready and stops there without it: the platform cannot
    /// see why the dispatcher has not claimed it yet, and a daemon that is simply stopped is the
    /// commonest reason of all.
    /// </param>
    /// <param name="heldByTracker">
    /// The measurement that says this row is waiting for its project's claim gate — the tracker
    /// does not show the linked item assigned to this install, or could not be read at all (idea
    /// 64c75e43) — or null when nothing is holding it that way. Read off what the dispatcher
    /// published rather than asked of the tracker here, so a row and the daemon say the same
    /// sentence; absent means the platform observed no such wait, never that the tracker was
    /// checked and agreed.
    /// </param>
    /// <param name="heldByLedgerHolder">
    /// The measurement that says this row's own claim last stood down at the ledger record's
    /// holder lock (idea 202383dc, A3b) — another node's own claim, or a write that could not
    /// complete — or null when nothing is holding it that way. Read off what the dispatcher
    /// published, the identical reasoning <paramref name="heldByTracker"/> gives its own hold.
    /// </param>
    public static IReadOnlyList<string> Compose(
        TaskListItem task,
        LifecycleState state,
        QueueHold? held = null,
        TrackerClaimDecision? heldByTracker = null,
        DateTimeOffset now = default,
        TaskHolderClaimHold? heldByLedgerHolder = null)
    {
        if (state != LifecycleState.Published)
        {
            // The marker can be set while the decider allows it — Queued, Blocked, even a
            // currently-Claimed task, for its next turn in the queue (Decisions Log #127) — and
            // a currently-Claimed task reads as LifecycleState.Working, not Published. Without
            // this, a human who marks a running task queue-first sees the marker recorded on the
            // stream but nowhere on the board until it lands back on Queued or Blocked
            // (independent pre-PR review, cycle 1, conformance lens). Pre-approval is stated here
            // too for the identical reason — it is settable on any live non-terminal task, not
            // only a Published one, and the board must not go quiet about it just because the
            // task has moved on to Queued, Blocked, or Working. Three states are carved out.
            // LifecycleState.Done renders only at TRUE closeout (the merge observed), so a task's
            // pre-approval no longer governs anything there — stating it would claim a future
            // merge for a pull request that has already merged (independent pre-PR review, cycle
            // 1, conformance lens). LifecycleState.Draft used to be carved out for the symmetric
            // reason and no longer is: TaskDecider.Publish once re-recorded the flag
            // unconditionally (defaulting false), so a Draft carrying a true claimed a promise a
            // plain republish would silently clear (independent pre-PR review, cycle 1, conformance
            // lens). Publish carries a standing grant forward now (task: a published task's GitHub
            // issue carries the whole task record — an adopted task needs its own answer settable
            // while still a Draft), so the promise survives, and a draft that holds it says so
            // here exactly as h9k task show's own Pre-approved row does.
            // LifecycleState.Archived is the remaining carve-out: TaskAggregate.Apply(TaskAbandoned) leaves
            // PreApproved untouched too, but TaskDecider.SetPreApproved itself refuses to flip the
            // flag on an abandoned task ("there is no future pull request left for pre-approval to
            // govern") — so a stale true surviving abandonment must not go on claiming a merge the
            // platform will never attempt (independent pre-PR review, cycle 1, both lenses).
            return
            [
                .. state == LifecycleState.HeldElsewhere ? (string[])[HeldElsewhereFact(task, now)] : [],
                .. task.QueuePriorityMarked ? (string[])[QueuePriorityFact] : [],
                .. task.EffectivePreApproval.MergesAutomatically
                    && state != LifecycleState.Done
                    && state != LifecycleState.Archived
                    ? (string[])[PreApprovedFact(task.EffectivePreApproval)]
                    : [],
            ];
        }

        IReadOnlyList<string> facts = task.State.Value switch
        {
            "Published" => ["not assigned — nothing will claim it until you assign it"],
            // Ready is all this row can honestly claim on its own: it is assigned, its
            // dependencies are met, and the dispatcher has not claimed it. Why not is a question
            // only a measurement can answer, so the slot line is appended when one exists and
            // omitted when none does, rather than a contention being asserted from the state
            // alone (AGENTS.md, the never-guess rule).
            // The claim-gate hold is stated ahead of the slot line when both apply: a card the
            // tracker says somebody else holds is not going to be claimed here whatever the
            // ceiling does, so it is the more specific answer to "why is this not moving" and the
            // browse surfaces show only the first line (TaskStatusRow.SummaryMarkup).
            // The rank rides inside this same sentence rather than as a separate fact (Decisions
            // Log #188): it is why a row waits behind another one of the same
            // project, so it belongs beside the phrase that already says the row is waiting, not
            // beside the held/tracker reasons that follow, which are a different question — why
            // the dispatcher has not claimed anything of this project's at all.
            "Queued" =>
            [
                $"assigned and ready as {task.Rank.Describe()}; the dispatcher has not claimed it yet",
                .. heldByTracker is not null ? (string[])[heldByTracker.ReasonLine] : [],
                .. heldByLedgerHolder is not null ? (string[])[LedgerHolderFact(heldByLedgerHolder, now)] : [],
                .. held is not null ? (string[])[held.ReasonLine] : [],
            ],
            // A blocker recorded dead is answered before the count, in the same words and the
            // same order its phase-line twin uses (TaskPhaseComposer.BlockedDetail). A death
            // leaves UnmetDependencies untouched — TaskDependencyFailed only appends to the dead
            // list — so the count arm below would report a wait the stream says will never end,
            // and on the browse surfaces that single line is the whole of what the row says.
            // The recorded death itself stays on the attention line, which quotes it whole.
            "Blocked" when task.DependencyFailureReason.IsNotBlank() =>
                ["a blocker will not close out on its own", .. BlockedBy(task)],
            // The remote twin of the line above, in the same words and the same order its
            // phase-line twin uses (TaskPhaseComposer.BlockedDetail): a stacked parent pull request
            // that closed unmerged is a hold nothing will clear (task: a stacked child can stand on
            // a pull request another install owns).
            "Blocked" when task.RemoteStackedParentHoldReason.IsNotBlank() =>
            [
                $"the pull request it is stacked on (#{task.StackedOnPullRequestNumber}) closed without "
                + "merging",
                .. BlockedBy(task),
            ],
            // A remote stacked parent holds with no unmet dependency behind it — there is no local
            // task to name — so it is answered before the arm below, which would otherwise read
            // this as a record disagreeing with itself.
            "Blocked" when task.StackedOnPullRequestNumber is { } remoteParentNumber
                && !task.RemoteStackedParentState.ReleasesChild =>
            [
                $"waiting for pull request #{remoteParentNumber}, which it is stacked on, to be open "
                + $"(last observed {task.RemoteStackedParentState.Describe()})",
                .. BlockedBy(task),
            ],
            // A Blocked task with nothing recorded as unmet is a record disagreeing with itself,
            // so the line says that rather than reporting a wait on zero things.
            "Blocked" when task.UnmetDependencies.Count == 0 =>
                ["blocked, but no unmet dependency is recorded"],
            // "to close out" is the bar for a plain blocked-by edge and the wrong one for a stacked
            // parent still on this task's unmet set: that one releases the task at its Delivered
            // (task: a stacked pull-request edge exists as an explicit opt-in dependency), so the
            // count line names the mixed bar rather than promising the stricter one for both.
            "Blocked" =>
            [
                $"waiting on {task.UnmetDependencies.Count} dependenc"
                    + $"{(task.UnmetDependencies.Count == 1 ? "y" : "ies")} to "
                    + (task.StackedOnTaskId is { } stackedParentId
                        && task.UnmetDependencies.Contains(stackedParentId)
                        ? task.UnmetDependencies.Count == 1 ? "reach Delivered (stacked)" : "clear (one stacked)"
                        : "close out"),
                .. BlockedBy(task),
            ],
            // A published task in a state this build does not recognize says so rather than
            // being described as one of the states it might be.
            _ => [$"published; the recorded state ({task.State.Value}) is not one this build knows"],
        };

        // The marker matters most on a Queued row (it is what the claim query orders on), but
        // it is stated wherever it is set — including Blocked, where it is inert until the
        // blocker clears — so a human never has to guess whether a marker they set survived
        // (task 45136b29, idea fcaded0b's R7 ruling).
        return
        [
            .. facts,
            .. task.TakenOverAt is { } takenOverAt ? (string[])[TakenOverFact(task, takenOverAt, now)] : [],
            .. task.QueuePriorityMarked ? (string[])[QueuePriorityFact] : [],
            .. task.EffectivePreApproval.MergesAutomatically
                ? (string[])[PreApprovedFact(task.EffectivePreApproval)]
                : [],
        ];
    }

    /// <summary>
    /// Who took this task over, from whom, and when (idea 202383dc, item 4) — stated alongside
    /// the Queued/Blocked row a forced take always lands on, the same "stated wherever it is set"
    /// treatment <see cref="QueuePriorityFact"/> already gets, since a taken-over task's own next
    /// claim can be a sweep or two away and a human reading the board in the meantime should not
    /// have to already know to look at <c>h9k task show</c> for it.
    /// </summary>
    private static string TakenOverFact(TaskListItem task, DateTimeOffset takenOverAt, DateTimeOffset now)
    {
        string from = task.TakenOverFromNodeId is { } fromNodeId
            ? $"node {TaskListCommand.ShortId(fromNodeId)}"
            : "an unrecorded previous holder";
        string since = TaskStatusComposer.RelativeAge(now - takenOverAt);
        return task.TakenOverReason.IsNotBlank()
            ? $"taken over from {from} since {since} — {task.TakenOverReason}"
            : $"taken over from {from} since {since}";
    }

    /// <summary>
    /// The same hold in the one-clause voice <see cref="TrackerClaimDecision.ReasonLine"/> gives
    /// the tracker-assignee gate's own wait (idea 202383dc, A3b): the ledger record's holder is
    /// another node's, or its own write could not complete — the identical fact
    /// <c>DispatchEngine</c>'s own log line states, so a board render and the daemon never
    /// disagree about it.
    /// </summary>
    private static string LedgerHolderFact(TaskHolderClaimHold hold, DateTimeOffset now)
    {
        if (hold.HolderNodeId is null)
        {
            return $"waiting for its ledger record's holder to clear — the write failed: {hold.Cause}";
        }

        string since = hold.HolderSince is { } holderSince
            ? TaskStatusComposer.RelativeAge(now - holderSince)
            : "an unknown time";
        return $"waiting for its ledger record's holder to clear — held by {hold.HolderNodeName} since {since}";
    }

    /// <summary>
    /// Which blockers, named as the reader can type them, and nothing at all when none is
    /// recorded — because a dead blocker already cleared out of the unmet list leaves this the
    /// choice between an empty list rendered as "blocked by" and saying nothing.
    /// </summary>
    private static IReadOnlyList<string> BlockedBy(TaskListItem task) =>
        task.UnmetDependencies.Count == 0
            ? []
            : [$"blocked by {string.Join(", ", task.UnmetDependencies.Select(TaskListCommand.ShortId))}"];
}
