using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The claim gate as the CLI doors use it (idea 64c75e43): <c>h9k task queue</c> and
/// <c>h9k task publish --queue</c>, which warn and queue anyway, and
/// <c>h9k task drive</c>/<c>h9k task start</c>, which refuse with the identical sentence. One
/// place, so the two behaviours cannot drift into two different readings of the same tracker
/// answer, and so the refusal an agent has to self-correct from is worded once.
/// <para>
/// The dispatcher's own door is <c>DispatchEngine.TryClaimAsync</c> rather than this: it has a
/// node, a published hold to write, and a re-read cadence to keep, none of which a one-shot
/// command has. What all of them share is <see cref="TrackerClaimGate"/> itself, which is where
/// the rule and the reading actually live.
/// </para>
/// </summary>
internal static class TrackerClaimCheck
{
    /// <summary>
    /// One fresh check for this task, or <see cref="TrackerClaimDecision.NotGated"/> when the
    /// project's gate is off or the task carries no gated item. Reads the tracker; writes nothing
    /// to it.
    /// </summary>
    /// <param name="gate">
    /// The gate itself, or null for the real one. A seam rather than a construction, the same
    /// <c>ProcessRunner? runner = null</c> idiom every connector in this codebase carries: the
    /// refusals and the warnings here are exactly what a test has to pin, and they are the ones
    /// hardest to arrange against a live tracker.
    /// </param>
    public static Task<TrackerClaimDecision> ForTaskAsync(
        IDocumentStore store,
        ProjectDetails project,
        string? externalReference,
        TrackerClaimGate? gate,
        CancellationToken cancellationToken) =>
        (gate ?? new TrackerClaimGate(new ProjectScopedGitHubRunner(store).Runner)).CheckAsync(
            store,
            project.ClaimGate,
            Reference(externalReference),
            project.RepositoryPath,
            cancellationToken);

    /// <summary>
    /// The gate applied at a door that starts work now — <c>h9k task drive</c> and
    /// <c>h9k task start</c>. A refusal is a <see cref="DomainBusinessRuleException"/>, so it
    /// exits 70: the command line was fine and the task is fine, and what refused is a standing
    /// project rule about who may claim this item, which is exactly what that code means
    /// (Program.cs's own mapping).
    /// <para>
    /// A pass returns its <see cref="TrackerAssignmentObserved"/> for the caller to append rather
    /// than appending it here: both callers write the claim with an explicit expected version
    /// covering every event in the same call, so the evidence and the claim land in one
    /// transaction — together or not at all — and the arithmetic stays where it can be read.
    /// Null when nothing was observed, which is a project with the gate off or a task with no
    /// gated item.
    /// </para>
    /// </summary>
    public static async Task<TrackerAssignmentObserved?> RefuseOrEvidenceAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerClaimGate? gate,
        CancellationToken cancellationToken)
    {
        TrackerClaimDecision decision = await ForTaskAsync(
            store, project, externalReference, gate, cancellationToken);
        return decision.Holds
            ? throw new DomainBusinessRuleException(decision.RefusalLine)
            : Evidence(taskId, externalReference, decision);
    }

    /// <summary>
    /// The gate applied at a door that only queues work — <c>h9k task queue</c> and
    /// <c>h9k task publish --queue</c>. It never refuses: the tracker's assignment is the go
    /// signal, so assigning is still the right act — it puts the task in the queue the gate lets
    /// it out of the moment the item is assigned. What this adds is that the human is told, now,
    /// that the task will sit there until then, and what to do about it.
    /// <para>
    /// Runs after the assignment has committed, and in its own transaction, for two reasons: the
    /// assignment must not depend on a tracker call that can fail or hang, and on the publish path
    /// the item this reads may not exist until the publish's own backlog step has created it.
    /// The warning goes to stderr — a scripted caller (or an agent) parsing stdout still has to
    /// see the one line saying the queue will not move.
    /// </para>
    /// </summary>
    /// <param name="gate">The gate itself, or null for the real one — see <see cref="ForTaskAsync"/>.</param>
    public static async Task WarnAndRecordAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerClaimGate? gate,
        CancellationToken cancellationToken)
    {
        // Everything here is caught, and that is the whole reason it runs after the commit: the
        // assignment has already landed, so a tracker that hangs or a connector that throws must
        // not make this command exit non-zero for a change that succeeded (self-review, round one
        // — the same reasoning DispatchEngine.PublishLoadAsync's own catch documents). A database
        // hiccup on the observation append falls under the identical invariant and is caught one
        // level down, by the overload below, which can name which half failed rather than blaming
        // the read. The failure is still said out loud, because a human who was promised a gate
        // check and did not get one needs to know.
        try
        {
            TrackerClaimDecision decision = await ForTaskAsync(
                store, project, externalReference, gate, cancellationToken);
            await WarnAndRecordAsync(store, taskId, externalReference, decision, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                "  The assignment landed, but this project's claim gate could not be read to say whether "
                + $"the tracker will let it dispatch: {exception.Message} The dispatcher reads the same "
                + "gate again before anything claims, and h9k status names the hold if there is one.");
        }
    }

    /// <summary>
    /// The same after-the-commit half, for a caller that has <em>already</em> read the gate: the
    /// interactive assign door, whose offer to take the item (<see cref="OfferOrTakeAsync"/>) needed
    /// the read before the assignment, and must not pay for a second one to say the identical
    /// sentence about the identical answer afterwards.
    /// <para>
    /// It owns the catch for its own half rather than leaning on a caller's, because the invariant
    /// the overload above states — nothing after the commit may make this command exit non-zero for
    /// a change that succeeded — is not a caller's to remember. An earlier draft of this overload
    /// left the append bare on the reasoning that every decision-handing caller was already inside
    /// that overload's try, and <see cref="TaskAssignCommand"/>'s own post-commit report was not:
    /// a transient Postgres failure appending the observation escaped to Program.cs, which ran the
    /// database doctor and exited non-zero for an assignment that had already landed and already
    /// been announced (independent pre-PR review, cycle 1). Keeping the try here is also what lets
    /// the sentence name the observation rather than the read, which is what the overload above
    /// would have blamed.
    /// </para>
    /// </summary>
    public static async Task WarnAndRecordAsync(
        IDocumentStore store,
        Guid taskId,
        string? externalReference,
        TrackerClaimDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Holds)
        {
            await Console.Error.WriteLineAsync($"  {decision.RefusalLine}");
            return;
        }

        if (Evidence(taskId, externalReference, decision) is not { } observed)
        {
            return;
        }

        try
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Events.Append(taskId, observed);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                "  The assignment landed and this project's claim gate passes, but the tracker "
                + "assignment it rests on could not be recorded on this task's stream: "
                + $"{exception.Message} Nothing waits on that record — the dispatcher reads the same "
                + "gate again before anything claims — so the only thing lost is this task's own "
                + "evidence of what was observed just now.");
        }
    }

    /// <summary>
    /// The claim gate at the one door that can <em>satisfy</em> it rather than only report it
    /// (idea 64c75e43, Decisions Log #143): <c>h9k task queue --take</c> reads the linked item
    /// fresh and, when the tracker shows it assigned to nobody, writes this install's own tracker
    /// identity into its assignee field so the gate then passes on its own.
    /// <para>
    /// Runs <em>before</em> the assignment, unlike
    /// <see cref="WarnAndRecordAsync(IDocumentStore, Guid, ProjectDetails, string?, TrackerClaimGate?, CancellationToken)"/>,
    /// and refuses rather than warns: a take that could not have the item leaves the task exactly
    /// as it was, which is the whole of the acceptance criterion and also the only outcome a human
    /// can re-run from safely. A <see cref="DomainBusinessRuleException"/>, so it exits 70 — the
    /// command line was fine and the task is fine, and what refused is a standing project rule
    /// about who may hold this item (Program.cs's own mapping).
    /// </para>
    /// <para>
    /// A successful take is recorded in its own transaction, right here, rather than folded into
    /// the assignment the caller is about to append: the tracker has already been written to by
    /// then, and an event recording an external act that actually happened must not be lost to a
    /// later append's failure. The order is deliberate too — the record lands before the
    /// assignment, so the stream reads in the order the world did.
    /// </para>
    /// <para>
    /// That separate transaction cuts the other way too, and <see cref="RecordAsync"/> owns the
    /// consequence: once the tracker has been written to, a failure to record it must not abort
    /// the assignment it was taken for. The take is the one part of this door that runs before
    /// the commit, so an exception escaping here ends the command with somebody's Jira card or
    /// GitHub issue assigned to this install and the Hall9k task still unassigned — the tracker
    /// and the board pulled apart, which is the exact failure <c>--take</c> exists to prevent, and
    /// far worse than losing an audit event (Copilot review, PR #262). So the record is
    /// best-effort and says so out loud, the same invariant
    /// <see cref="WarnAndRecordAsync(IDocumentStore, Guid, string?, TrackerClaimDecision, CancellationToken)"/>
    /// keeps on the other side of the commit. What is still allowed to throw is a refusal from the
    /// take itself: that one is the answer, and it happens before anything is written.
    /// </para>
    /// </summary>
    /// <param name="take">The taker itself, or null for the real one — the same seam idiom <see cref="ForTaskAsync"/>'s own <c>gate</c> parameter documents.</param>
    /// <param name="reassign">
    /// The reassignment offer for an item somebody else holds, or null when nobody attended is there to
    /// answer it, which is every unattended run and every caller that never reassigns (the cooperative
    /// take). Null refuses a held item exactly as it always did.
    /// </param>
    /// <returns>The take, for the caller to announce once its own assignment has landed.</returns>
    public static async Task<TrackerTake> TakeOrRefuseAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerAssignmentTake? take,
        ReassignDoor? reassign,
        CancellationToken cancellationToken)
    {
        TrackerAssignmentTake taker = take ?? new TrackerAssignmentTake(new ProjectScopedGitHubRunner(store).Runner);
        TrackerTake taken = await taker.TakeAsync(
            store,
            project.ClaimGate,
            Reference(externalReference),
            project.RepositoryPath,
            cancellationToken);

        // A held item is the one verdict a terminal can overrule: the operator is shown who holds it
        // and asked. Declining, or any run with nobody to ask, leaves the refusal below untouched.
        if (taken.Verdict == TrackerTakeVerdict.HeldByOther && reassign is { } door)
        {
            taken = door.HeldByAnotherNode
                ? taken with { Decision = taken.Decision with { HeldElsewhere = HeldElsewhere(door, taskId) } }
                : door.Offer(taken.Decision)
                    ? await taker.ReassignAsync(
                        store, project.ClaimGate, Reference(externalReference), project.RepositoryPath,
                        taken.Decision.Holders, cancellationToken)
                    : taken;
        }

        if (!taken.Passes)
        {
            throw new DomainBusinessRuleException(taken.RefusalLine);
        }

        await RecordAsync(store, taskId, externalReference, taken, cancellationToken);
        return taken;
    }

    /// <summary>
    /// What an attended door needs to offer a reassignment: the question itself, and the ledger facts
    /// that decide whether it may be asked at all. A <see cref="TakeOffer"/> is the same seam the take
    /// offer uses, so a test answers it without a console.
    /// </summary>
    /// <param name="Offer">Asks the human at the terminal, shown who holds the item.</param>
    /// <param name="ThisNodeId">This install's node.</param>
    /// <param name="HolderNodeId">The node the task's ledger holder names, null when it names none.</param>
    internal sealed record ReassignDoor(TakeOffer Offer, Guid ThisNodeId, Guid? HolderNodeId)
    {
        /// <summary>
        /// A task whose ledger holder is another node is somebody's work in progress, and moving the
        /// tracker item under it would be the one thing the cooperative take exists to ask first.
        /// </summary>
        public bool HeldByAnotherNode => HolderNodeId is { } holder && holder != ThisNodeId;
    }

    /// <summary>The sentence a held item's refusal gains when the ledger says another node holds the task, so no reassignment is offered.</summary>
    private static string HeldElsewhere(ReassignDoor door, Guid taskId) =>
        $"This task is held by node {TaskListCommand.ShortId(door.HolderNodeId ?? Guid.Empty)} in the ledger, so no "
        + "reassignment is offered here: ask that node to give it up with "
        + $"h9k task take {TaskListCommand.ShortId(taskId)} --reason \"<why>\".";

    /// <summary>
    /// Whether the human at the terminal wants the linked item taken. A delegate rather than a
    /// direct <see cref="AnsiConsole.Confirm(string, bool)"/> call for the reason every seam in
    /// this codebase is one: the offer and its decline are exactly what a test has to pin, and a
    /// console prompt is the hardest thing in a CLI to arrange from a test host. It doubles as the
    /// answer to "is there anybody to ask" — <see cref="OfferOrTakeAsync"/> is only called with one
    /// when there is, so the terminal check stays at the single place that owns the terminal.
    /// </summary>
    internal delegate bool TakeOffer(TrackerClaimDecision decision);

    /// <summary>
    /// The same door without <c>--take</c>, on a session that has a human in front of it: read the
    /// item, and when the tracker shows it assigned to nobody, <em>offer</em> to take it before
    /// assigning. A non-interactive run is never given an <paramref name="offer"/> and so never
    /// reaches here — it warns and proceeds exactly as it did before this flag existed, which is
    /// the point: the platform never writes to somebody's tracker without being told to, and an
    /// unattended process cannot be told.
    /// <para>
    /// A declined offer, an item somebody else holds when no reassignment is offered for it, and a
    /// tracker that could not be read all return their own read for the caller to warn with after the
    /// assignment lands, so the answer is read once and spoken once. An accepted offer goes through
    /// <see cref="TakeOrRefuseAsync"/>'s own path, or for a held item through the reassignment,
    /// including its refusal: the human said take it, and if it could not be taken, leaving the task
    /// untouched is the honest outcome rather than assigning it with a warning they did not ask for.
    /// </para>
    /// <para>
    /// Best-effort about the read: a tracker that hangs or a connector that throws must not fail an
    /// assignment nobody has asked to gate on it, so an unforeseen failure here degrades to the
    /// post-commit path — which reads again, and whose own catch says so out loud. What is
    /// deliberately <em>not</em> caught is a refusal from the take itself: that one is the answer,
    /// not a failure to get one.
    /// </para>
    /// </summary>
    /// <param name="reassign">The reassignment offer for a held item, or null when nobody attended is there to answer it.</param>
    public static async Task<(TrackerTake? Take, TrackerClaimDecision? Decision)> OfferOrTakeAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerAssignmentTake? take,
        TakeOffer offer,
        ReassignDoor? reassign,
        CancellationToken cancellationToken)
    {
        TrackerAssignmentTake taker = take ?? new TrackerAssignmentTake(new ProjectScopedGitHubRunner(store).Runner);
        TrackerClaimDecision decision;
        try
        {
            decision = await taker.ReadAsync(
                store,
                project.ClaimGate,
                Reference(externalReference),
                project.RepositoryPath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                "  This project's claim gate could not be read to offer taking the item, so the "
                + $"assignment proceeds without the offer: {exception.Message}");
            return (null, null);
        }

        // An item the tracker shows assigned to nobody is offered as a take. One somebody else holds is
        // offered as a reassignment, but only to a door with an attended human behind it and only when
        // the ledger does not say another node holds the task; every other door, and an unreadable
        // tracker, fall through to the warning the door has always printed.
        if (decision.Verdict == TrackerClaimVerdict.HeldByOther && reassign is { } door)
        {
            return door.HeldByAnotherNode
                ? (null, decision with { HeldElsewhere = HeldElsewhere(door, taskId) })
                : door.Offer(decision)
                    ? (await ReassignOrRefuseAsync(store, taskId, project, externalReference, taker, decision, cancellationToken), null)
                    : (null, decision);
        }

        return decision.Verdict == TrackerClaimVerdict.Unassigned && offer(decision)
            ? (await TakeOrRefuseAsync(store, taskId, project, externalReference, taker, reassign: null, cancellationToken), null)
            : (null, decision);
    }

    /// <summary>
    /// The reassignment the operator confirmed, written and recorded, or refused with the tracker's own
    /// words and the task left as it was. The refusal is a <see cref="DomainBusinessRuleException"/>,
    /// exit 70, like every other claim-gate refusal: the command line was fine, and what refused is a
    /// standing rule about who may hold this item.
    /// </summary>
    private static async Task<TrackerTake> ReassignOrRefuseAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerAssignmentTake taker,
        TrackerClaimDecision held,
        CancellationToken cancellationToken)
    {
        TrackerTake taken = await taker.ReassignAsync(
            store, project.ClaimGate, Reference(externalReference), project.RepositoryPath, held.Holders,
            cancellationToken);
        if (!taken.Passes)
        {
            throw new DomainBusinessRuleException(taken.RefusalLine);
        }

        await RecordAsync(store, taskId, externalReference, taken, cancellationToken);
        return taken;
    }

    /// <summary>
    /// The offer on a task that is already queued for this operator: the one place an item somebody
    /// else holds can be reassigned without the task being assigned again, so it keeps its rank and its
    /// placement and neither <c>TaskAssigned</c> nor <c>TaskUnassigned</c> is appended. Null means
    /// nothing was offered or the operator declined, and the caller refuses the queueing as it always did
    /// for a task already in the queue.
    /// <para>
    /// Reads the gate first and offers only on a held item whose ledger holder, if any, is this node;
    /// on a held item another node holds it prints the sentence naming that node on stderr instead.
    /// Best-effort about that read, like <see cref="OfferOrTakeAsync"/>: a tracker that cannot be read
    /// degrades to today's refusal rather than a new failure.
    /// </para>
    /// </summary>
    public static async Task<TrackerTake?> OfferReassignOnQueuedAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerAssignmentTake? take,
        ReassignDoor door,
        CancellationToken cancellationToken)
    {
        TrackerAssignmentTake taker = take ?? new TrackerAssignmentTake(new ProjectScopedGitHubRunner(store).Runner);
        TrackerClaimDecision decision;
        try
        {
            decision = await taker.ReadAsync(
                store, project.ClaimGate, Reference(externalReference), project.RepositoryPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                "  This project's claim gate could not be read to offer reassigning the item, so the "
                + $"queueing is refused as it would be without the offer: {exception.Message}");
            return null;
        }

        if (decision.Verdict != TrackerClaimVerdict.HeldByOther)
        {
            return null;
        }

        // Another node holding a queued task (a requeue does not clear the ledger holder) is the case
        // the cooperative take exists for, so the operator is told which node and how to ask it before
        // the caller's own already-queued refusal follows.
        if (door.HeldByAnotherNode)
        {
            await Console.Error.WriteLineAsync($"  {HeldElsewhere(door, taskId)}");
            return null;
        }

        return door.Offer(decision)
            ? await ReassignOrRefuseAsync(store, taskId, project, externalReference, taker, decision, cancellationToken)
            : null;
    }

    /// <summary>
    /// What a take actually observed, on the task's own stream: the write this install made
    /// (<see cref="TrackerAssignmentWritten"/>), the write that replaced other people
    /// (<see cref="TrackerAssignmentReassigned"/>, in place of the first for that write), or the
    /// assignment it merely found already there (<see cref="TrackerAssignmentObserved"/>). Separate
    /// events, because a stream that spelt them the same could not answer "who put this person on the
    /// card" at all — and none is recorded for a verdict that observed nothing (AGENTS.md, never guess
    /// at unobserved facts).
    /// <para>
    /// Best-effort, for the reason <see cref="TakeOrRefuseAsync"/>'s own summary gives: this runs
    /// after the tracker write has already landed and before the assignment commits, so throwing
    /// would strand the item assigned on the tracker with the task unassigned here. The failure is
    /// still said out loud, and the sentence names what was actually lost — for a take, the
    /// evidence that <em>this install</em> put the identity on the item, which is the whole reason
    /// <see cref="TrackerAssignmentWritten"/> is a separate event and is not recoverable by
    /// reading the tracker again.
    /// </para>
    /// </summary>
    internal static async Task RecordAsync(
        IDocumentStore store,
        Guid taskId,
        string? externalReference,
        TrackerTake taken,
        CancellationToken cancellationToken)
    {
        if (taken.Assignee is not { } assignee)
        {
            return;
        }

        string reference = externalReference ?? string.Empty;
        object recorded = taken.Verdict switch
        {
            TrackerTakeVerdict.Reassigned => new TrackerAssignmentReassigned(
                taskId,
                reference,
                [.. taken.Replaced.Select(replaced => new ReplacedTrackerAssignee(replaced.Identity, replaced.Name))],
                assignee.Identity,
                assignee.Name,
                taken.ObservedAt),
            TrackerTakeVerdict.Taken => new TrackerAssignmentWritten(
                taskId, reference, assignee.Identity, assignee.Name, taken.ObservedAt),
            _ => new TrackerAssignmentObserved(
                taskId, reference, assignee.Identity, assignee.Name, taken.ObservedAt),
        };

        try
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Events.Append(taskId, recorded);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"  {taken.Decision.Tracker} now shows {taken.Decision.Item} assigned to you and the "
                + "assignment here proceeds, but what the tracker answered could not be recorded on this "
                + $"task's stream: {exception.Message} Nothing waits on that record — the dispatcher reads "
                + "the gate itself again before anything claims — but this task's stream will not say "
                + (taken.Wrote
                    ? "that this install was what assigned the item, and reading the tracker again cannot "
                        + "recover that."
                    : "what was observed just now."));
        }
    }

    /// <summary>The task's recorded reference as the gate and the taker both take it, or null when it carries none.</summary>
    private static ExternalReference? Reference(string? externalReference) =>
        externalReference.IsBlank() ? null : ExternalReference.Parse(externalReference);

    /// <summary>
    /// The evidence a passing check rested on, or null when there is none to record. Only a check
    /// that actually read the tracker records anything: a project with the gate off, or a task
    /// with no gated item, observed nothing, and a stream that claimed otherwise would assert a
    /// fact nobody looked for (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    private static TrackerAssignmentObserved? Evidence(
        Guid taskId, string? externalReference, TrackerClaimDecision decision) =>
        decision.Verdict == TrackerClaimVerdict.Assigned && decision.Assignee is { } assignee
            ? new TrackerAssignmentObserved(
                taskId, externalReference ?? string.Empty, assignee.Identity, assignee.Name, decision.ObservedAt)
            : null;
}
