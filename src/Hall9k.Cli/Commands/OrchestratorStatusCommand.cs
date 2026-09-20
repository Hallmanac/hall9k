using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Whether an orchestrator window is up for a project on this machine, and which one (idea
/// 89471598, piece 1). The same sentence the <c>h9k status</c> header carries, printed on its
/// own so a script, a courier, or an operator can ask the question directly.
/// <para>
/// Read-only, unlike <c>h9k orchestrator register</c>: it never bootstraps this node's own
/// record, because a node that has never been registered has never had a window registered
/// against it either, and the honest answer is the same one.
/// </para>
/// </summary>
public sealed class OrchestratorStatusCommand : Hall9kAsyncCommand<OrchestratorStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project to report on: its name, an unambiguous fragment, or its full id. Omit it and "
            + "every registered project is reported, one line each.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ProjectDetails> projects;
        if (settings.Project.IsNotBlank())
        {
            projects = [await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)];
        }
        else
        {
            projects = [.. (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)];
            if (projects.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "[dim]No projects registered.[/] Register one: h9k project add --name <name> --repo <path>");
                return ExitCodes.Ok;
            }
        }

        Guid? nodeId = await ThisNodeIdAsync(session, cancellationToken);
        OrchestratorProcessTableProbe probe = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (ProjectDetails project in projects)
        {
            OrchestratorPresenceDetails? presence = nodeId is { } node
                ? await session.LoadAsync<OrchestratorPresenceDetails>(
                    OrchestratorPresenceStreamId.For(node, project.Id), cancellationToken)
                : null;

            string line = OrchestratorPresenceLine.Describe(presence, probe, now);
            if (projects.Count > 1)
            {
                AnsiConsole.MarkupLineInterpolated($"{project.Name}: {line}");
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"{line}");
            }
        }

        AnsiConsole.MarkupLine(
            "[dim]A window registers itself at launch (the launch anchor's first step) and deregisters when it "
            + "closes or restarts; the daemon records a registered window whose process is gone as lost.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// This machine's own node record, or <see langword="null"/> when nothing has ever bootstrapped
    /// one here. Resolved by machine name, the same key <c>NodeBootstrap.EnsureAsync</c> itself
    /// uses, so this read and a register on the same machine can never name different nodes.
    /// </summary>
    internal static async Task<Guid?> ThisNodeIdAsync(IQuerySession session, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(record => record.MachineName == machineName)
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        return node?.Id;
    }
}
