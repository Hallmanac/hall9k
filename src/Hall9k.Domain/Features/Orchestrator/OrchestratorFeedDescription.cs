using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The one-line plain sentence an orchestrator reads for one event (idea 89471598, piece 2) —
/// the feed's whole vocabulary, one arm per event type <see cref="OrchestratorFeedInterest"/>
/// admits. Deterministic and model-free: the same record always produces the same sentence, on
/// every node, so a golden test can pin the output and piece 3's courier can compare two drains
/// without a judgment call.
/// <para>
/// Every arm reads facts already on the record and nothing else. Where a record carries a reason
/// a person or an agent wrote, it is quoted rather than summarised — flattened to one line and
/// clipped by <see cref="Quote"/>, because a feed item that wraps for forty lines is not a feed
/// item. Nothing here says which task an item belongs to: the task is the group heading
/// (<see cref="OrchestratorFeedRenderer"/>), which is also the one layer that can name it by its
/// objective rather than by a bare id.
/// </para>
/// </summary>
public static class OrchestratorFeedDescription
{
    /// <summary>
    /// How much of a free-text reason, message body, or idea note one item carries. Long enough
    /// for the sentence a park or a failure actually records, short enough that a drain of fifty
    /// items still reads as a list.
    /// </summary>
    private const int MaximumQuotedLength = 160;

    /// <summary>
    /// The sentence for this record, or null when the feed has no arm for it — which is the same
    /// answer <see cref="OrchestratorFeedInterest.BandOf"/> gives for a type it does not name, so
    /// a reader that filters first never sees a null here.
    /// </summary>
    public static string? Of(object eventData) => eventData switch
    {
        // ─── Parks and disputes ────────────────────────────────────────────────────────────────
        ReviewParked parked => $"the review loop parked for a human: {Quote(parked.Reason)}",
        CloseoutParked parked => $"closeout parked for a human: {Quote(parked.Reason)}",
        ReviewDisagreementParked parked =>
            $"a fix lap disagreed with {Count(parked.Disagreements.Count, "finding")} and drafted a reply "
            + "nobody has sent",
        HumanThreadReplyParked parked =>
            $"a follow-up drafted {Count(parked.Drafts.Count, "reply", "replies")} to a person's own review "
            + "thread and stopped rather than posting",
        ReviewThreadReplyRefused refused =>
            $"a session tried to {DispositionWord(refused.Disposition)} a person's own review thread and was "
            + $"refused: {Quote(refused.Reason)}",
        ReviewFindingRouted routed when routed.DraftTaskId is null =>
            $"a {routed.Severity.Value} review finding at {Field(routed.Location, 60)} could not be routed out "
            + $"of the pull request: {Quote(routed.FailureReason)}",
        ReviewFindingRouted routed =>
            $"a {routed.Severity.Value} review finding at {Field(routed.Location, 60)} was routed onto a draft "
            + "task nobody has published",
        QuestionAsked asked => $"an agent asked and stopped: {Quote(asked.Question)}",

        // ─── Gate and run failures ─────────────────────────────────────────────────────────────
        VerificationFailed failed => $"the verification gates failed: {Join(failed.FailedGates)}",
        SettlingGateRepairCapReached => "the settling-gate repair rounds are spent and the run parked",
        PullRequestChecksFailed failed => $"the pull request's CI checks failed: {Join(failed.FailedChecks)}",
        RunFailed failed => $"the run failed: {Quote(failed.Reason)}",
        RunKilled killed => $"the run was killed ({KillWord(killed.Reason)})",
        RunBudgetExhausted => "the token budget ran dry mid-run; the run parked until the window resets",
        ReviewErrored errored =>
            $"{Field(errored.Reviewer, 40)}'s review came back as an error placeholder rather than a real review",

        // ─── A merge that stays failed ─────────────────────────────────────────────────────────
        PullRequestAutoMergeAttempted attempted =>
            $"the daemon's own merge of the pull request was refused: {Quote(attempted.FailureReason)}",

        // ─── Daemon trouble ────────────────────────────────────────────────────────────────────
        RunSessionErrorRetried retried =>
            $"the {LegWord(retried.Leg)} session errored and the daemon retried it once: "
            + Quote(retried.ObservedMessage),
        RunUncommittedWorkRecoveryAttempted recovery =>
            $"a session ended leaving {Count(recovery.StrandedFiles.Count, "file")} uncommitted; the daemon is "
            + $"recovering the work: {Quote(recovery.Reason)}",
        RunUnattendedExitFlagged flagged =>
            $"a headless start exited with nobody watching: {Quote(flagged.Reason)}",
        RunRecordReconstructed => "the daemon rebuilt a run record for a run that never dispatched",
        RunLaunchHeld => "this node failed to launch a session; the run is held until the launch hold clears",

        // ─── A message from a person or another node's window ──────────────────────────────────
        MessageReceived received => MessageLine(received),

        // ─── Task state changes ────────────────────────────────────────────────────────────────
        TaskPublished => "published and ready to assign",
        // A claim a person made by hand (h9k task work, h9k task start) carries Guid.Empty as its
        // node id — the sentinel TaskAggregate.IsInteractiveClaim reads, deliberately naming no
        // node at all — so these two arms come first: shortening that sentinel would print
        // "node 00000000" as though a node had been observed (AGENTS.md, never guess at unobserved
        // facts). The owner's own root fingerprint is what the record actually names there, and
        // "this install" is not available to say instead: TaskClaimed travels, so a teammate's own
        // hand-made claim reaches this node's log carrying the identical sentinel.
        TaskClaimed claimed when claimed.NodeId == Guid.Empty && claimed.InteractiveMode =>
            $"claimed interactively by {Claimant(claimed)}; a human has the wheel",
        TaskClaimed claimed when claimed.NodeId == Guid.Empty =>
            $"claimed by {Claimant(claimed)} as a deliberate kick-off; a run is starting",
        TaskClaimed claimed when claimed.InteractiveMode =>
            $"claimed interactively by node {DomainId.Short(claimed.NodeId)}",
        TaskClaimed claimed => $"claimed by node {DomainId.Short(claimed.NodeId)}; a run is starting",
        PullRequestOpened opened => $"delivered: pull request #{opened.PullRequestNumber} opened",
        TaskCompleted completed when completed.PullRequestUrl.IsBlank() =>
            "the run finished with no pull request to watch",
        TaskCompleted completed => $"the run finished and pushed its work to {Field(completed.PullRequestUrl, 80)}",
        PullRequestMerged => "done: the pull request merged",
        TaskResolved resolved => $"closed as done by hand: {Quote(resolved.Reason)}",
        TaskFailed failed => $"the task failed: {Quote(failed.Reason)}",
        TaskAbandoned abandoned when abandoned.Reason.IsBlank() => "abandoned",
        TaskAbandoned abandoned => $"abandoned: {Quote(abandoned.Reason)}",

        // ─── Ideas logged or updated ───────────────────────────────────────────────────────────
        IdeaCaptured captured => $"idea logged: {Quote(captured.Text)}",
        IdeaRevised revised => $"idea revised: {Quote(revised.Text)}",
        IdeaAssignedToProject => "an idea was assigned to this project",
        IdeaTaskCut cut => $"a task was cut from an idea: {Quote(cut.Objective)}",
        IdeaConcluded concluded => $"idea concluded: {Quote(concluded.Reason)}",
        IdeaArchived archived => $"idea archived: {Quote(archived.Reason)}",
        IdeaSpikeConcluded spike =>
            $"a spike cut from an idea concluded {spike.Verdict.Value}: {Quote(spike.Reason)}",
        IdeaDiscarded discarded => $"idea discarded: {Quote(discarded.Reason)}",
        IdeaPromoted promoted => $"an idea was promoted into a task: {Quote(promoted.Objective)}",

        // ─── Claims and takeovers involving another node ───────────────────────────────────────
        TaskHolderTakenOver taken =>
            $"node {DomainId.Short(taken.NewHolderNodeId)} took the task over from "
            + (taken.PreviousHolderNodeId is { } previous
                ? $"node {DomainId.Short(previous)}"
                : "an unrecorded holder")
            + $": {Quote(taken.Reason)}",
        TaskTakeRequested requested =>
            $"node {DomainId.Short(requested.RequesterNodeId)} asked for this task: {Quote(requested.Reason)}",
        TaskTakeRefused refused =>
            $"node {DomainId.Short(refused.RequesterNodeId)}'s request for this task was refused: "
            + Quote(refused.Reason),
        TaskHolderReleased released when released.GrantedToNodeId is { } grantee =>
            $"the holder released the task to node {DomainId.Short(grantee)}",
        TaskHolderReleased => "the holder released the task",

        // ─── A run's phase changes ─────────────────────────────────────────────────────────────
        RunDispatched dispatched =>
            $"a {(dispatched.IsFollowUp ? "follow-up run" : "run")} was dispatched on branch "
            + Field(dispatched.Branch, 60),
        RunProcessStarted => "the run's session process started",
        RunResumed => "the run resumed",
        AgentSessionCompleted => "the agent session finished; the gates are next",
        GateStarted started => $"the gate {Field(started.GateName, 60)} started",
        GateEnded => "the gate finished",
        VerificationPassed => "the verification gates passed",
        VerificationSkipped skipped =>
            $"the verification gates were skipped: every changed path "
            + $"({Count(skipped.ChangedPaths.Count, "path")}) matched the non-executable-path set",
        ReviewDispatched review => $"review cycle {review.Cycle} dispatched ({LensWord(review.Lens)})",
        ReviewPassCompleted pass =>
            $"review cycle {pass.Cycle}'s {LensWord(pass.Lens)} pass returned {VerdictWord(pass.Verdict)}",
        ReviewCompleted completed =>
            $"review cycle {completed.Cycle} concluded {VerdictWord(completed.Verdict)}",
        ReviewFixDispatched fix =>
            $"a fix session was dispatched over review cycle {fix.Cycle}'s findings"
            + (fix.Escalated ? ", escalated to the stronger model" : string.Empty),
        ReviewFixCompleted fix =>
            $"the fix session for review cycle {fix.Cycle} finished {OutcomeWord(fix.Outcome)}",
        ReviewParkResolved resolved => $"a human resolved the review park with {VerdictWord(resolved.Verdict)}",
        ReviewBoundaryApproved => "a human approved the review boundary",
        ReviewSettled settled =>
            $"review cycle {settled.Cycle} settled as {settled.Settlement.Value} "
            + $"({settled.ResidualsFixed} fixed, {settled.ResidualsRouted} routed)",
        ReviewFeedbackReceived feedback =>
            $"{Count(feedback.UnresolvedThreadCount, "unresolved review thread")} observed on the pull request",
        PullRequestUpdated updated => $"pull request #{updated.PullRequestNumber} updated with new commits",
        PullRequestConflictObserved => "the pull request conflicts with its base branch",
        PullRequestClosed => "the pull request was closed without merging",
        RunPhaseDelegated delegated =>
            $"the operator delegated this phase to a contractor session: {Quote(delegated.Note)}",
        RunSuperseded superseded =>
            $"the run was superseded by claim generation {superseded.SupersededByGeneration}",
        RunCompleted => "the run completed",

        _ => null,
    };

    /// <summary>
    /// A received message, named by who sent it and what kind it is. A handoff carries no body of
    /// its own — the note travels on the task's stream and this envelope is only the nudge (see
    /// <see cref="MessageKind.Handoff"/>) — so quoting a body there would quote nothing.
    /// </summary>
    private static string MessageLine(MessageReceived received)
    {
        // The sender's own cross-node root fingerprint, falling back to the node id when a message
        // arrived without one — a foreign node's friendly name never replicates, so there is
        // nothing friendlier to print.
        string from = ShortFingerprint(received.FromOwnerFingerprint)
            ?? $"node {DomainId.Short(received.FromNodeId)}";
        return MessageKind.Parse(received.Kind) == MessageKind.Handoff
            ? $"{from} says a task's handoff note changed"
            : $"a message from {from}: {Quote(received.Body)}";
    }

    /// <summary>
    /// Who made a claim that names no node — the owner's own cross-node root fingerprint, or the
    /// honest absence of a name when the claim predates that field.
    /// </summary>
    private static string Claimant(TaskClaimed claimed) =>
        ShortFingerprint(claimed.OwnerRootFingerprint) ?? "somebody the claim does not name";

    /// <summary>
    /// An owner root fingerprint cut to the same short, readable prefix
    /// <c>PublishedFacts.HeldElsewhereFact</c> already uses for a foreign holder, or null when
    /// none was recorded.
    /// </summary>
    private static string? ShortFingerprint(string? fingerprint) =>
        fingerprint.IsBlank() ? null : fingerprint[..Math.Min(12, fingerprint.Length)];

    /// <summary>
    /// A leg's own word in a sentence, with the honest fallback for the value a stream written
    /// before the leg was recorded reads back as.
    /// </summary>
    private static string LegWord(RunSessionLeg leg) =>
        leg == RunSessionLeg.Unknown ? "agent" : leg.Value.ToLowerInvariant();

    private static string LensWord(ReviewLens? lens) =>
        lens is null || lens == ReviewLens.Unknown ? "lens not recorded" : lens.Value.ToLowerInvariant();

    private static string VerdictWord(ReviewVerdict verdict) =>
        verdict == ReviewVerdict.Unknown ? "no parseable verdict" : verdict.Value;

    private static string OutcomeWord(ReviewFixOutcome outcome) =>
        outcome == ReviewFixOutcome.Unknown ? "with no declared outcome" : outcome.Value;

    private static string DispositionWord(ReviewThreadDisposition disposition) =>
        disposition == ReviewThreadDisposition.Unknown
            ? "reply in"
            : disposition.Value.ToLowerInvariant();

    private static string KillWord(KillReason reason) =>
        reason == KillReason.Unknown ? "no reason recorded" : reason.Value;

    /// <summary>A count with its noun pluralised, so no line reads "1 files".</summary>
    private static string Count(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

    /// <summary>A recorded list, or the honest empty rather than a trailing colon with nothing
    /// after it — a gate list can be empty on a stream that recorded no names.</summary>
    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none named" : Field(string.Join(", ", values), MaximumQuotedLength);

    /// <summary>
    /// Free text a person or an agent wrote. Blank says so plainly rather than leaving a sentence
    /// ending in a colon.
    /// </summary>
    private static string Quote(string? value) =>
        value.IsBlank() ? "no reason recorded" : Field(value, MaximumQuotedLength);

    /// <summary>
    /// A recorded value that is not prose — a branch, a gate name, a url, a reviewer. Flattened
    /// to one line and clipped: every newline, carriage return and tab becomes a single space so
    /// a multi-line value cannot break the one-item-per-line shape the whole feed is read by, and
    /// the ellipsis says plainly that there is more rather than the line simply stopping. A blank
    /// reads as not recorded, never as an empty gap the reader has to interpret.
    /// </summary>
    private static string Field(string? value, int maximum)
    {
        if (value.IsBlank())
        {
            return "(not recorded)";
        }

        string flattened = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length <= maximum ? flattened : flattened[..maximum].TrimEnd() + "…";
    }
}
