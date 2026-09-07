using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

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
            externalReference.IsBlank() ? null : ExternalReference.Parse(externalReference),
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
        // assignment has already landed, so a tracker that hangs, a connector that throws, or a
        // database hiccup on the observation append must not make this command exit non-zero for a
        // change that succeeded (self-review, round one — the same reasoning
        // DispatchEngine.PublishLoadAsync's own catch documents). The failure is still said out
        // loud, because a human who was promised a gate check and did not get one needs to know.
        try
        {
            TrackerClaimDecision decision = await ForTaskAsync(
                store, project, externalReference, gate, cancellationToken);
            if (decision.Holds)
            {
                await Console.Error.WriteLineAsync($"  {decision.RefusalLine}");
                return;
            }

            if (Evidence(taskId, externalReference, decision) is { } observed)
            {
                await using IDocumentSession session = store.LightweightSession();
                session.Events.Append(taskId, observed);
                await session.SaveChangesAsync(cancellationToken);
            }
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
