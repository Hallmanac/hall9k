using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The claim gate as the CLI doors use it (idea 64c75e43): <c>h9k task assign</c> and
/// <c>h9k task publish --assign</c>, which warn and assign anyway, and
/// <c>h9k task work</c>/<c>h9k task start</c>, which refuse with the identical sentence. One
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
        (gate ?? new TrackerClaimGate()).CheckAsync(
            store,
            project.ClaimGate,
            Reference(externalReference),
            project.RepositoryPath,
            cancellationToken);

    /// <summary>
    /// The gate applied at a door that starts work now — <c>h9k task work</c> and
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
    /// The gate applied at a door that only queues work — <c>h9k task assign</c> and
    /// <c>h9k task publish --assign</c>. It never refuses: the tracker's assignment is the go
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
    /// (idea 64c75e43, Decisions Log #143): <c>h9k task assign --take</c> reads the linked item
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
    /// <returns>The take, for the caller to announce once its own assignment has landed.</returns>
    public static async Task<TrackerTake> TakeOrRefuseAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerAssignmentTake? take,
        CancellationToken cancellationToken)
    {
        TrackerTake taken = await (take ?? new TrackerAssignmentTake()).TakeAsync(
            store,
            project.ClaimGate,
            Reference(externalReference),
            project.RepositoryPath,
            cancellationToken);

        if (!taken.Passes)
        {
            throw new DomainBusinessRuleException(taken.RefusalLine);
        }

        await RecordAsync(store, taskId, externalReference, taken, cancellationToken);
        return taken;
    }

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
    /// A declined offer, an item somebody else holds, and a tracker that could not be read all
    /// return their own read for the caller to warn with after the assignment lands, so the answer
    /// is read once and spoken once. An accepted offer goes through
    /// <see cref="TakeOrRefuseAsync"/>'s own path — including its refusal: the human said take it,
    /// and if it could not be taken, leaving the task untouched is the honest outcome rather than
    /// assigning it with a warning they did not ask for.
    /// </para>
    /// <para>
    /// Best-effort about the read: a tracker that hangs or a connector that throws must not fail an
    /// assignment nobody has asked to gate on it, so an unforeseen failure here degrades to the
    /// post-commit path — which reads again, and whose own catch says so out loud. What is
    /// deliberately <em>not</em> caught is a refusal from the take itself: that one is the answer,
    /// not a failure to get one.
    /// </para>
    /// </summary>
    public static async Task<(TrackerTake? Take, TrackerClaimDecision? Decision)> OfferOrTakeAsync(
        IDocumentStore store,
        Guid taskId,
        ProjectDetails project,
        string? externalReference,
        TrackerAssignmentTake? take,
        TakeOffer offer,
        CancellationToken cancellationToken)
    {
        TrackerAssignmentTake taker = take ?? new TrackerAssignmentTake();
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

        // Only an item the tracker shows assigned to nobody is offered. One somebody else holds is
        // not an offer this platform has the right to make — there is no flag that takes an item
        // from another person — and an unreadable tracker cannot say whether it would be taking one
        // from anybody at all, so both fall through to the warning the door has always printed.
        return decision.Verdict == TrackerClaimVerdict.Unassigned && offer(decision)
            ? (await TakeOrRefuseAsync(store, taskId, project, externalReference, taker, cancellationToken), null)
            : (null, decision);
    }

    /// <summary>
    /// What a take actually observed, on the task's own stream: the write this install made
    /// (<see cref="TrackerAssignmentWritten"/>) or the assignment it merely found already there
    /// (<see cref="TrackerAssignmentObserved"/>). Two events rather than one, because a stream that
    /// spelt them the same could not answer "who put this person on the card" at all — and neither
    /// is recorded for a verdict that observed nothing (AGENTS.md, never guess at unobserved facts).
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

        object recorded = taken.Wrote
            ? new TrackerAssignmentWritten(
                taskId, externalReference ?? string.Empty, assignee.Identity, assignee.Name, taken.ObservedAt)
            : new TrackerAssignmentObserved(
                taskId, externalReference ?? string.Empty, assignee.Identity, assignee.Name, taken.ObservedAt);

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
