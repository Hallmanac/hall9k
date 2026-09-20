namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// What <see cref="OrchestratorPresenceDecider.Register"/> concluded, for the CLI's own closing
/// line. An enum rather than a closed-vocabulary value object because it is never persisted: the
/// events carry the fact, this only says which of them were produced (AGENTS.md, "enums only for
/// unpersisted in-process outcomes").
/// </summary>
public enum OrchestratorRegistrationOutcome
{
    /// <summary>Nothing was registered here, or what was is no longer live: a plain launch. A
    /// registered window the probe just found gone still gets its own
    /// <see cref="OrchestratorLost"/> recorded alongside, but nothing live was displaced, so the
    /// registration itself is this shape rather than <see cref="Replaced"/>.</summary>
    Registered,

    /// <summary>This very session is already the registered, live one — nothing appended.</summary>
    AlreadyRegistered,

    /// <summary>A different live window was registered and <c>--replace</c> took it over.</summary>
    Replaced,
}

/// <summary>
/// The events a registration produces, together with which shape it was — never more than one
/// launch, and never more than one ending for the window this one displaces:
/// <see cref="Replaced"/> when <c>--replace</c> took a live window over, <see cref="Lost"/> when
/// the window that was registered had already stopped existing and this register was the read
/// that observed it. <see cref="Launched"/> is <see langword="null"/> exactly when
/// <see cref="OrchestratorRegistrationOutcome.AlreadyRegistered"/> made the whole call a no-op.
/// <para>
/// Whichever ending is set is appended ahead of <see cref="Launched"/> in the same save, so the
/// stream never shows two windows live at once and the aggregate's own superseded-ending guard
/// still sees the ending while the old window is the current one.
/// </para>
/// </summary>
public sealed record OrchestratorRegistrationDecision(
    OrchestratorRegistrationOutcome Outcome,
    OrchestratorShutDown? Replaced,
    OrchestratorLost? Lost,
    OrchestratorLaunched? Launched);
