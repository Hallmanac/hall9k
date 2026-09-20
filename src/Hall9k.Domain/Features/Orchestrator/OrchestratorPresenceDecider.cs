using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The three transitions an orchestrator window's presence has (idea 89471598, piece 1): it
/// launches and says so, it leaves and says so, or it stops existing and the daemon's own sweep
/// says so for it.
/// </summary>
public static class OrchestratorPresenceDecider
{
    /// <summary>
    /// A window declaring itself live for this project on this node. Three shapes, decided
    /// against what is registered right now and whether that process is still running:
    /// nothing live registers plainly, recording the loss of a still-registered window this very
    /// read found gone; the same process asking again is a no-op (so an anchor that runs twice in
    /// one window never litters the stream); a different live window is refused by name unless
    /// <paramref name="replace"/> takes it over, which records that window as shut down first so
    /// the stream never shows two live at once.
    /// <para>
    /// The refusal is the default because two windows driving one project's board is a genuine
    /// collision — both drain the same feed, both dispatch against the same queue — and the
    /// operator is the only one who knows whether the other one is a terminal they forgot or a
    /// terminal they are using (Brian's call, 2026-09-19).
    /// </para>
    /// </summary>
    public static OrchestratorRegistrationDecision Register(
        OrchestratorPresenceAggregate? presence,
        Guid nodeId,
        Guid projectId,
        string sessionName,
        int processId,
        string cli,
        bool replace,
        IOrchestratorProcessProbe probe,
        DateTimeOffset at)
    {
        if (nodeId == Guid.Empty || projectId == Guid.Empty)
        {
            throw new DomainValidationException(
                "Registering an orchestrator needs both the node it is running on and the project it drives.");
        }

        if (sessionName.IsBlank())
        {
            throw new DomainValidationException(
                "Registering an orchestrator needs the window's own session name — the name the session mesh "
                + "and h9k status address it by.");
        }

        if (cli.IsBlank())
        {
            throw new DomainValidationException(
                "Registering an orchestrator needs the agent CLI it is running under (claude-code, codex, …).");
        }

        if (processId <= 0)
        {
            throw new DomainValidationException(
                $"'{processId}' is not a process id. Register the window's own process: Claude Code sets "
                + "CLAUDE_PID in every Bash tool it spawns.");
        }

        // Refused rather than recorded, for the same reason TaskRegisterSessionCommand refuses a
        // CLAUDE_PID the process table cannot find: a registration nothing can ever check is a
        // record the very next sweep would turn into OrchestratorLost, leaving an audit trail of
        // windows that never existed. The caller is the window itself, so its own process is
        // there by construction; an id that is not is a typo or a stale paste.
        if (probe.Probe(processId) is not { } sighting)
        {
            throw new DomainValidationException(
                $"No process {processId} is running on this machine, so there is no window to register. "
                + "Pass the orchestrator window's own process id (Claude Code sets CLAUDE_PID).");
        }

        OrchestratorLaunched launched = new(
            nodeId, projectId, sessionName.Trim(), processId, LaunchText.NormalizeCli(cli), sighting.StartedAt, at);

        if (presence is null || !OrchestratorLiveness.IsLive(presence, probe))
        {
            // A window still registered here whose process the probe just found gone: this read
            // is the observation, so the loss is recorded now rather than left for the next
            // sweep. Without it the stream reads launch, launch, with no ending for the first
            // window at all — the sweep that would have recorded it only ever looks at the
            // currently registered pid, and that is this new window from here on. The same
            // reason the register refuses an unfindable pid applies: the audit trail must not
            // carry a window with no checkable end. Stamped with this call's own time, which is
            // when it was noticed, never a guess at when the window actually died.
            OrchestratorLost? lost = presence is { Registered: true }
                ? new OrchestratorLost(
                    presence.NodeId, presence.ProjectId, presence.SessionName, presence.ProcessId, at)
                : null;
            return new OrchestratorRegistrationDecision(
                OrchestratorRegistrationOutcome.Registered, null, lost, launched);
        }

        // The same process asking again, matched on the pid alone: a session's display name is
        // Claude Code's own mutable runtime state (an operator can rename a window mid-session),
        // so a name that has moved is this same window re-announcing itself, never a second one
        // to refuse. It still appends, so the recorded name is the current one rather than the
        // one this window happened to carry when the anchor first ran.
        if (presence.ProcessId == processId)
        {
            return presence.SessionName == launched.SessionName && presence.Cli == launched.Cli
                ? new OrchestratorRegistrationDecision(
                    OrchestratorRegistrationOutcome.AlreadyRegistered, null, null, null)
                : new OrchestratorRegistrationDecision(
                    OrchestratorRegistrationOutcome.Registered, null, null, launched);
        }

        if (!replace)
        {
            throw new DomainConflictException(
                $"An orchestrator is already live for this project on this machine: '{presence.SessionName}' "
                + $"({presence.Cli}, pid {presence.ProcessId}, since "
                + $"{presence.LaunchedAt?.ToLocalTime():g}). Close that window, or take it over with "
                + "h9k orchestrator register --replace, which records it as shut down first.");
        }

        OrchestratorShutDown replaced = new(
            presence.NodeId, presence.ProjectId, presence.SessionName, presence.ProcessId, at);
        return new OrchestratorRegistrationDecision(
            OrchestratorRegistrationOutcome.Replaced, replaced, null, launched);
    }

    /// <summary>
    /// A window leaving on purpose, dropping <em>its own</em> claim and only its own. Nothing is
    /// recorded when this project has no registration on this node: deregistering is the last
    /// thing a closing window does and the first thing a restarting one does, so it is idempotent
    /// by design — a close that fails because a sweep already recorded the loss would teach an
    /// operator to stop calling it.
    /// <para>
    /// The caller names itself by process id, and a registration held by a different process is
    /// left standing rather than ended. Deregistering whatever happened to be registered would
    /// make an ordinary close step unregister a live replacement: the recipe's own restart
    /// contract is deregister here, start a new window, let its anchor register it, and the old
    /// window is still an open session the operator closes later, running its close step against
    /// a stream the replacement now owns. That is not the superseded-ending race the aggregate's
    /// own guard covers — the shutdown would name the replacement's own live pid and pass it,
    /// leaving <c>h9k status</c> reporting "none live" for a window that is running.
    /// </para>
    /// </summary>
    public static OrchestratorDeregistrationDecision Deregister(
        OrchestratorPresenceAggregate? presence, int processId, DateTimeOffset at)
    {
        if (processId <= 0)
        {
            throw new DomainValidationException(
                $"'{processId}' is not a process id. Deregister the window's own process, the same one it "
                + "registered: Claude Code sets CLAUDE_PID in every Bash tool it spawns.");
        }

        if (presence is not { Registered: true })
        {
            return new OrchestratorDeregistrationDecision(
                OrchestratorDeregistrationOutcome.NothingRegistered, null);
        }

        return presence.ProcessId == processId
            ? new OrchestratorDeregistrationDecision(
                OrchestratorDeregistrationOutcome.Deregistered,
                new OrchestratorShutDown(
                    presence.NodeId, presence.ProjectId, presence.SessionName, presence.ProcessId, at))
            : new OrchestratorDeregistrationDecision(
                OrchestratorDeregistrationOutcome.HeldByAnotherWindow, null);
    }

    /// <summary>
    /// The daemon's own sweep, one presence at a time: a window still registered whose process is
    /// no longer the one running under its id. <see langword="null"/> when there is nothing to
    /// record — not registered, or still live — so the sweep appends only on the transition.
    /// <paramref name="at"/> is when the sweep noticed, never a guess at when the window actually
    /// died (AGENTS.md, "never guess at unobserved facts").
    /// </summary>
    public static OrchestratorLost? Lose(
        OrchestratorPresenceAggregate presence, IOrchestratorProcessProbe probe, DateTimeOffset at) =>
        presence.Registered && !OrchestratorLiveness.IsLive(presence, probe)
            ? new OrchestratorLost(presence.NodeId, presence.ProjectId, presence.SessionName, presence.ProcessId, at)
            : null;
}
