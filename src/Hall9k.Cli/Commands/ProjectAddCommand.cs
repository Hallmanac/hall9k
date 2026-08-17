using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class ProjectAddCommand : Hall9kAsyncCommand<ProjectAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--name <NAME>")]
        [Description("Project name (used to reference the project in task commands)")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--repo <PATH>")]
        [Description("Local repository path the daemon creates worktrees from")]
        public string RepositoryPath { get; init; } = string.Empty;

        [CommandOption("--repo-url <URL>")]
        [Description("Remote git URL (provider-agnostic)")]
        public string? RepositoryUrl { get; init; }

        [CommandOption("--base-branch <BRANCH>")]
        [Description("Branch task branches are created from (default: main)")]
        public string? BaseBranch { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        string name = settings.Name;
        bool duplicate = await session.Query<ProjectDetails>()
            .Where(p => p.Name == name)
            .AnyAsync(cancellationToken);
        if (duplicate)
        {
            throw new DomainConflictException($"A project named '{name}' already exists.");
        }

        BootstrapContext context = await Bootstrap.EnsureAsync(session, cancellationToken);

        Guid projectId = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId,
            context.OwnerId,
            context.ConnectionId,
            name,
            Path.GetFullPath(settings.RepositoryPath),
            settings.RepositoryUrl.IsBlank() ? null : new Uri(settings.RepositoryUrl),
            settings.BaseBranch,
            DateTimeOffset.UtcNow);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-added:{projectId}", cancellationToken);

        AnsiConsole.MarkupLine($"[green]Project '{name.EscapeMarkup()}' registered.[/] Id: [dim]{projectId}[/]");
        return ExitCodes.Ok;
    }
}
