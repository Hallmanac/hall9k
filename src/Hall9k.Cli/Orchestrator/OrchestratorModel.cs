using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The model an orchestrator recipe's <see cref="RecipeSettingsDocument"/> is rendered for.
/// Deliberately its own chain, not <c>Hall9k.Domain.Shared.ValueObjects.AgentModel.Resolve</c>'s
/// agent-dispatch one: an orchestrator window's own override
/// (<see cref="OperatingSettings.OrchestratorModel"/> for the node, the project's own
/// <c>OrchestratorModel</c> field for a project window) outranks everything else, precisely so
/// raising or lowering the model dispatched agents run on (<c>DefaultModel</c>/a project's
/// <c>Model</c>) never silently moves the operator's own window, and the reverse (independent
/// pre-PR review, cycle 1). Only when no orchestrator-specific override is set does this fall
/// through to the ordinary agent-dispatch chain, which is what every window rendered before that
/// override existed already carries.
/// </summary>
public static class OrchestratorModel
{
    /// <summary>
    /// The node's own orchestrator override, else its agent-dispatch default, else the platform
    /// fallback.
    /// </summary>
    public static string ForNode(OperatingSettings settings) =>
        settings.OrchestratorModel is { Length: > 0 } orchestratorOverride
            ? orchestratorOverride
            : settings.DefaultModel is { Length: > 0 } configured
                ? configured
                : AgentModel.PlatformFallback;

    /// <summary>
    /// The project's own orchestrator override, else its agent-dispatch model, else the node's
    /// resolution.
    /// </summary>
    public static string ForProject(AgentModel projectOrchestratorModel, AgentModel projectModel, OperatingSettings settings) =>
        projectOrchestratorModel != AgentModel.Unknown
            ? projectOrchestratorModel.Value
            : projectModel != AgentModel.Unknown
                ? projectModel.Value
                : ForNode(settings);
}
