using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The reasoning effort an orchestrator recipe's <see cref="RecipeSettingsDocument"/> is rendered
/// for (task: the orchestrator window's effort becomes a rendered project and node setting).
/// Deliberately its own chain, the same independence <see cref="OrchestratorModel"/> already has
/// from the agent-dispatch model chain: an orchestrator window's own override
/// (<see cref="OperatingSettings.OrchestratorEffort"/> for the node, a project's own
/// <c>OrchestratorEffort</c> field for a project window) outranks everything else, and no dispatch
/// effort ever feeds it — not a task's own, not a project's or the node's per-role value, and not
/// the node-wide <see cref="OperatingSettings.Effort"/> either. Unlike <see cref="OrchestratorModel"/>,
/// there is no legacy "fall through to the project's own dispatch value" step in the project chain:
/// that step exists for the model only because every window ran on the agent-dispatch model before
/// the orchestrator override existed, and this effort setting carries no such history to preserve.
/// Only when no orchestrator-specific override is set anywhere does this bottom out at Unknown,
/// which leaves the executor's effort key out of the rendered file so the model's own default
/// decides, exactly as it did before this setting existed.
/// </summary>
public static class OrchestratorEffort
{
    /// <summary>
    /// The node's own orchestrator-effort override, or Unknown. Read through
    /// <see cref="AgentEffort.FromInput"/> rather than a bare null check, so a hand-edited
    /// <c>config.json</c> carrying <c>"default"</c> (or whitespace) lands on Unknown here the same
    /// way it does everywhere else <see cref="AgentEffort"/> is resolved.
    /// </summary>
    public static AgentEffort ForNode(OperatingSettings settings) => AgentEffort.FromInput(settings.OrchestratorEffort);

    /// <summary>The project's own orchestrator-effort override, else the node's own resolution.</summary>
    public static AgentEffort ForProject(AgentEffort projectOrchestratorEffort, OperatingSettings settings) =>
        projectOrchestratorEffort.IsWellFormed ? projectOrchestratorEffort : ForNode(settings);
}
