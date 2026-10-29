using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Project.Rendering;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.ProjectHomes;

/// <summary>
/// Creates, or repairs, a solution's thin home at <see cref="SolutionHomePaths.DefaultFor"/>: the
/// generated <c>AGENTS.md</c>, <c>skills/</c> seeded from the install's canonical set, the
/// <c>.claude/</c> adapter, and <c>recipes/</c> holding the platform-owned launch anchor and
/// settings file plus the seeded orchestrator-recipe-generator. There is no <c>repo/</c>,
/// <c>ideas/</c> or <c>tasks/</c>: a solution owns no code and no work items of its own.
/// <para>
/// Idempotent throughout, so <c>h9k solution init</c> is also the repair command. It reuses the
/// project home's writers (<see cref="LaunchAnchorDocument.WriteStep"/>,
/// <see cref="RecipeSettingsDocument.WriteStep"/>, <see cref="RecipeSkillPublisher.Seed"/>,
/// <see cref="SkillSeeder.Seed"/>) because each takes a bare home path; a solution has no project
/// override, so its settings resolve through the node's orchestrator model and effort alone. The
/// anchor and <c>settings.json</c> are platform-owned and never written by the recipe skill
/// (Decisions Log #147, #155).
/// </para>
/// </summary>
public static class SolutionHomeRecipe
{
    public static async Task<IReadOnlyList<ProjectHomeStep>> BuildAsync(
        string home,
        SolutionMembership membership,
        IReadOnlyList<ProjectDetails> projects,
        Func<Guid, string?> rootFingerprintOf,
        CancellationToken cancellationToken)
    {
        List<ProjectHomeStep> steps = [];

        // Before anything is created: a hand-written AGENTS.md at this path (or another solution's
        // render, when two names slug to one directory) would otherwise cost a half-made home.
        if (SolutionAgentsDocument.IsForeign(home, membership.Key))
        {
            steps.Add(ForeignAgentsFailure(home, membership));
            return steps;
        }

        bool existed = Directory.Exists(home);
        foreach (string directory in SolutionHomePaths.Directories(home))
        {
            Directory.CreateDirectory(directory);
        }

        steps.Add(existed
            ? ProjectHomeStep.AlreadyThere($"home directory {home} already existed, so the shape was completed in place")
            : ProjectHomeStep.Created($"home directory created at {home} (skills/ .claude/ recipes/; no repo/, ideas/ or tasks/)"));

        steps.AddRange(SkillSeeder.Seed(home));
        steps.Add(WriteAgentsStep(home, membership, projects, rootFingerprintOf));

        // TryRead rather than the throwing read: init is itself a repair command, so a malformed
        // config.json must not take out the rest of the home it is in the middle of building.
        ConfigFileReadResult operatingSettingsRead = await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken);
        string orchestratorModel = OrchestratorModel.ForNode(operatingSettingsRead.Settings);
        AgentEffort orchestratorEffort = OrchestratorEffort.ForNode(operatingSettingsRead.Settings);
        if (operatingSettingsRead.Problem is { } settingsProblem)
        {
            steps.Add(ProjectHomeStep.Skipped(
                $"{settingsProblem.Message} The solution recipe's settings.json resolves to {orchestratorModel} "
                + "for its model this pass; fix the file, then re-run h9k solution init to confirm that is still what you expect."));
        }

        steps.Add(LaunchAnchorDocument.WriteStep(ProjectHomePaths.LaunchAnchorFile(home)));
        steps.Add(RecipeSettingsDocument.WriteStep(
            ProjectHomePaths.RecipeSettingsFile(home), orchestratorModel, orchestratorEffort));
        steps.AddRange(RecipeSkillPublisher.Seed(home));

        return steps;
    }

    /// <summary>The AGENTS.md write, reported as a step. Also the first copy <c>h9k solution init</c> leaves for the next sweep to keep current.</summary>
    public static ProjectHomeStep WriteAgentsStep(
        string home,
        SolutionMembership membership,
        IReadOnlyList<ProjectDetails> projects,
        Func<Guid, string?> rootFingerprintOf)
    {
        string path = ProjectHomePaths.AgentsFile(home);
        string rendered = SolutionAgentsDocument.Render(membership, projects, rootFingerprintOf);
        return SolutionAgentsDocument.Write(home, membership.Key, rendered) switch
        {
            SolutionAgentsWriteOutcome.Created => ProjectHomeStep.Created($"AGENTS.md rendered from the solution's membership at {path}"),
            SolutionAgentsWriteOutcome.Rewritten => ProjectHomeStep.Created($"AGENTS.md re-rendered from the solution's membership at {path}"),
            SolutionAgentsWriteOutcome.Unchanged => ProjectHomeStep.AlreadyThere($"AGENTS.md already matches the solution's membership at {path}"),
            _ => ForeignAgentsFailure(home, membership),
        };
    }

    private static ProjectHomeStep ForeignAgentsFailure(string home, SolutionMembership membership) =>
        ProjectHomeStep.Failed(
            $"AGENTS.md at {ProjectHomePaths.AgentsFile(home)} was left alone: it already exists and is not this "
            + $"platform's render for the solution '{membership.Name}' (it lacks the \"{SolutionAgentsDocument.GeneratedMarker}\" "
            + "marker or names another solution's key), so this recipe cannot tell it apart from hand-written content it "
            + $"would otherwise destroy. Move it aside, then run h9k solution init \"{membership.Name}\". Nothing was written.");
}
