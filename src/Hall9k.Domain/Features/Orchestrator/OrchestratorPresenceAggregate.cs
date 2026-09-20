namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Whether an orchestrator window is registered for one project on one node, and which one (idea
/// 89471598, piece 1). Keyed by <see cref="OrchestratorPresenceStreamId.For"/>, so every window
/// this project has ever had on this node folds into one standing fact rather than one stream
/// per window.
/// <para>
/// "Registered" is not "live": this aggregate records only what a window said about itself, and
/// a window that was killed said nothing. <see cref="OrchestratorLiveness.IsLive"/> is what pairs
/// this state with a look at the process table; the daemon's own sweep is what turns a registered
/// window whose process is gone into <see cref="OrchestratorLost"/>.
/// </para>
/// </summary>
public sealed class OrchestratorPresenceAggregate
{
    public Guid Id { get; private set; }
    public Guid NodeId { get; private set; }
    public Guid ProjectId { get; private set; }

    /// <summary>Whether the most recent event here was a launch, rather than a shutdown or a loss.</summary>
    public bool Registered { get; private set; }

    /// <summary>The most recently registered window's own session name, kept after it ends so
    /// "none live" can still say which window that was.</summary>
    public string SessionName { get; private set; } = string.Empty;

    public int ProcessId { get; private set; }

    /// <summary>The agent CLI the window runs under ("claude-code", "codex", …).</summary>
    public string Cli { get; private set; } = string.Empty;

    /// <summary>As observed at registration, or <see langword="null"/> when it could not be read — see
    /// <see cref="OrchestratorLaunched.ProcessStartedAt"/>.</summary>
    public DateTimeOffset? ProcessStartedAt { get; private set; }

    public DateTimeOffset? LaunchedAt { get; private set; }

    /// <summary>When a window last left on purpose, if one ever has.</summary>
    public DateTimeOffset? ShutDownAt { get; private set; }

    /// <summary>When a sweep last found a registered window's process gone, if one ever has.</summary>
    public DateTimeOffset? LostAt { get; private set; }

    public void Apply(OrchestratorLaunched @event)
    {
        Id = OrchestratorPresenceStreamId.For(@event.NodeId, @event.ProjectId);
        NodeId = @event.NodeId;
        ProjectId = @event.ProjectId;
        Registered = true;
        SessionName = @event.SessionName;
        ProcessId = @event.ProcessId;
        Cli = @event.Cli;
        ProcessStartedAt = @event.ProcessStartedAt;
        LaunchedAt = @event.LaunchedAt;
    }

    public void Apply(OrchestratorShutDown @event)
    {
        if (!IsAboutTheCurrentWindow(@event.ProcessId))
        {
            return;
        }

        Registered = false;
        ShutDownAt = @event.ShutDownAt;
    }

    public void Apply(OrchestratorLost @event)
    {
        if (!IsAboutTheCurrentWindow(@event.ProcessId))
        {
            return;
        }

        Registered = false;
        LostAt = @event.LostAt;
    }

    /// <summary>
    /// An ending only ends the window it names. Both endings are decided against a read of this
    /// stream and committed a moment later, and in between a new window can have registered:
    /// the daemon's sweep re-aggregates, finds a dead window, and appends its loss — while the
    /// replacement the operator just started is already live on the same stream. Applied blind,
    /// that loss would read as "none live" for a window that is running, which is exactly the
    /// answer the courier and <c>h9k status</c> act on. The same shape covers a slow
    /// <c>h9k orchestrator deregister</c> from a window another has already replaced.
    /// <para>
    /// Matched on the process id, the one identity that is not the operator's to rename mid-
    /// session. This is a superseded-ending guard, not a liveness check: nothing here reads the
    /// process table.
    /// </para>
    /// </summary>
    private bool IsAboutTheCurrentWindow(int processId) => Registered && processId == ProcessId;
}
