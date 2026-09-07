using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The model an orchestrator recipe's <see cref="RecipeSettingsDocument"/> is rendered for — the
/// same node-then-platform-default chain every other model resolution in this codebase reads
/// (Decisions Log #33), with a project's own override (when the recipe is for a project window)
/// outranking the node's.
/// </summary>
public static class OrchestratorModel
{
    /// <summary>The node's own default, or the platform fallback when nothing overrides it.</summary>
    public static string ForNode(OperatingSettings settings) =>
        settings.DefaultModel is { Length: > 0 } configured ? configured : AgentModel.PlatformFallback;

    /// <summary>The project's own override, or the node's resolution when the project defers.</summary>
    public static string ForProject(AgentModel projectModel, OperatingSettings settings) =>
        projectModel != AgentModel.Unknown ? projectModel.Value : ForNode(settings);
}
