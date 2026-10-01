using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The refresh <c>h9k install</c> and <c>h9k update</c> launch through the newly installed binary
/// after the swap, so the anchors they write are the installed build's rendering and not that of the
/// build that ran the command (see <see cref="LaunchAnchorRefresh"/>). Safe to run by hand: it
/// rewrites platform-owned files outright and never fails on a registry or write problem.
/// </summary>
public sealed class OrchestratorRefreshAnchorsCommand : Hall9kAsyncCommand<OrchestratorRefreshAnchorsCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        LaunchAnchorRefresh.RunAsync(ReadRegistryAsync, cancellationToken);

    private static async Task<IReadOnlyList<RegisteredProjectHome>> ReadRegistryAsync(CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        return [.. projects
            .Where(project => project.HomeDirectory.HasValue)
            .Select(project => new RegisteredProjectHome(project.Name, project.HomeDirectory.Value))];
    }
}
