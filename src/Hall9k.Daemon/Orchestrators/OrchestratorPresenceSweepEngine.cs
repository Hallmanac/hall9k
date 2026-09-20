using Hall9k.Domain.Features.Orchestrator;
using Marten;

namespace Hall9k.Daemon.Orchestrators;

/// <summary>How many registered windows this tick found gone, for the loop's own log line.</summary>
public sealed record OrchestratorPresenceSweepResult(int Lost);

/// <summary>
/// The only place <see cref="OrchestratorLost"/> is ever appended (idea 89471598, piece 1): the
/// daemon looks at every orchestrator window still registered on this node and records the ones
/// whose process is no longer there.
/// <para>
/// A window that closes normally deregisters itself, so this sweep exists for the ways a window
/// ends without saying so: a terminal closed, a process killed, a machine that crashed. Only
/// this node's own registrations are swept, and that is not a scoping convenience but the whole
/// constraint — a process table is readable from the machine it belongs to and nowhere else, so
/// another node's registration is not something this daemon could rule on, whatever the
/// database shows.
/// </para>
/// <para>
/// The probe is injected rather than constructed here so the loss transition is unit-testable
/// against a fake process table; <see cref="OrchestratorProcessTableProbe"/> is the real one.
/// </para>
/// </summary>
public sealed class OrchestratorPresenceSweepEngine(
    IDocumentStore store, NodeContext node, IOrchestratorProcessProbe probe,
    ILogger<OrchestratorPresenceSweepEngine> logger)
{
    public async Task<OrchestratorPresenceSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        await using IDocumentSession session = store.LightweightSession();

        IReadOnlyList<OrchestratorPresenceDetails> registered = await session.Query<OrchestratorPresenceDetails>()
            .Where(presence => presence.NodeId == nodeId && presence.Registered)
            .ToListAsync(cancellationToken);

        int lost = 0;
        foreach (OrchestratorPresenceDetails presence in registered)
        {
            // Re-aggregated rather than ruled on straight off the projection: this sweep and a
            // window's own h9k orchestrator register/deregister write the same stream, and the
            // document read above can be a moment stale. The aggregate is the state the decider
            // is written against, and it costs one short stream replay per registered window,
            // of which a node has at most one per project.
            //
            // Unfenced on purpose, unlike the register this races: the close-a-window-and-start-
            // another case lands a registration between this read and the save below often enough
            // to matter, and the answer is not to fail the sweep but to make the loss harmless.
            // OrchestratorPresenceAggregate's own superseded-ending guard ignores a loss that
            // names a process the stream has already moved past, so a loss committed behind a
            // newer window's launch is recorded as history and changes nothing.
            OrchestratorPresenceAggregate? aggregate = await session.Events
                .AggregateStreamAsync<OrchestratorPresenceAggregate>(presence.Id, token: cancellationToken);
            if (aggregate is null)
            {
                continue;
            }

            if (OrchestratorPresenceDecider.Lose(aggregate, probe, DateTimeOffset.UtcNow) is not { } lostEvent)
            {
                continue;
            }

            session.Events.Append(presence.Id, lostEvent);
            lost++;
            logger.LogInformation(
                "Orchestrator '{SessionName}' (pid {ProcessId}) for project {ProjectId} is gone; recorded as lost",
                lostEvent.SessionName, lostEvent.ProcessId, lostEvent.ProjectId);
        }

        if (lost > 0)
        {
            await session.SaveChangesAsync(cancellationToken);
        }

        return new OrchestratorPresenceSweepResult(lost);
    }
}
