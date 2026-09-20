using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The read view of one project's orchestrator presence on one node (idea 89471598, piece 1):
/// what <c>h9k orchestrator status</c>, the <c>h9k status</c> header line, and the daemon's own
/// presence sweep all read, so none of them replays the stream to answer a one-line question.
/// <para>
/// <see cref="Registered"/> is only half the answer — it records what the window said, and a
/// window that was killed said nothing. Pair it with
/// <see cref="OrchestratorLiveness.IsStillRunning"/> against this same node's process table for
/// the live answer; the sweep is what eventually turns a registered-but-gone window into
/// <see cref="OrchestratorLost"/> on the stream itself.
/// </para>
/// </summary>
public sealed class OrchestratorPresenceDetails
{
    public Guid Id { get; set; }
    public Guid NodeId { get; set; }
    public Guid ProjectId { get; set; }
    public bool Registered { get; set; }
    public string SessionName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string Cli { get; set; } = string.Empty;
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public DateTimeOffset? LaunchedAt { get; set; }

    /// <summary>When a window last left on purpose, if one ever has — what "none live" reports.</summary>
    public DateTimeOffset? ShutDownAt { get; set; }

    /// <summary>When a sweep last found a registered window gone, if one ever has — the other thing "none live" reports.</summary>
    public DateTimeOffset? LostAt { get; set; }
}

public sealed class OrchestratorPresenceDetailsProjection : SingleStreamProjection<OrchestratorPresenceDetails, Guid>
{
    public OrchestratorPresenceDetails Create(IEvent<OrchestratorLaunched> @event) => new()
    {
        Id = @event.StreamId,
        NodeId = @event.Data.NodeId,
        ProjectId = @event.Data.ProjectId,
        Registered = true,
        SessionName = @event.Data.SessionName,
        ProcessId = @event.Data.ProcessId,
        Cli = @event.Data.Cli,
        ProcessStartedAt = @event.Data.ProcessStartedAt,
        LaunchedAt = @event.Data.LaunchedAt,
    };

    public void Apply(IEvent<OrchestratorLaunched> @event, OrchestratorPresenceDetails view)
    {
        view.Registered = true;
        view.SessionName = @event.Data.SessionName;
        view.ProcessId = @event.Data.ProcessId;
        view.Cli = @event.Data.Cli;
        view.ProcessStartedAt = @event.Data.ProcessStartedAt;
        view.LaunchedAt = @event.Data.LaunchedAt;
    }

    public void Apply(IEvent<OrchestratorShutDown> @event, OrchestratorPresenceDetails view)
    {
        if (!IsAboutTheCurrentWindow(view, @event.Data.ProcessId))
        {
            return;
        }

        view.Registered = false;
        view.ShutDownAt = @event.Data.ShutDownAt;
    }

    public void Apply(IEvent<OrchestratorLost> @event, OrchestratorPresenceDetails view)
    {
        if (!IsAboutTheCurrentWindow(view, @event.Data.ProcessId))
        {
            return;
        }

        view.Registered = false;
        view.LostAt = @event.Data.LostAt;
    }

    /// <summary>
    /// Mirrors <see cref="OrchestratorPresenceAggregate"/>'s own superseded-ending guard exactly,
    /// and has to: this view and that aggregate replay the same stream, and a disagreement
    /// between them is a <c>h9k status</c> line that contradicts what the register refusal just
    /// decided. See that method for why an ending only ends the window it names.
    /// </summary>
    private static bool IsAboutTheCurrentWindow(OrchestratorPresenceDetails view, int processId) =>
        view.Registered && processId == view.ProcessId;
}
