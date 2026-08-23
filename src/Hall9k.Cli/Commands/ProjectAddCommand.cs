using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
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
        [Description(
            "Local repository path the daemon creates worktrees from. Optional, and rarely what you "
            + "want: with --repo-url the project's home materialises its own bare clone and records "
            + "that. Pass this only to register against a repository that already exists here, which "
            + "leaves the home's repo/ unmaterialised.")]
        public string? RepositoryPath { get; init; }

        [CommandOption("--repo-url <URL>")]
        [Description("Remote git URL (provider-agnostic). The home's repo/ is bare-cloned from it.")]
        public string? RepositoryUrl { get; init; }

        [CommandOption("--base-branch <BRANCH>")]
        [Description("Branch task branches are created from, and the branch repo/dev is checked out on (default: main)")]
        public string? BaseBranch { get; init; }

        [CommandOption("--home <PATH>")]
        [Description(
            "Where this project lives on disk — the directory holding the generated AGENTS.md, "
            + "repo/, ideas/, tasks/ and skills/ (default: ~/.hall9k/projects/<name>). The location "
            + "is yours to choose; the shape inside it is the platform's and is identical on every "
            + "machine, which is what lets a session started in it bootstrap itself.")]
        public string? Home { get; init; }

        [CommandOption("--no-home")]
        [Description(
            "Register the project without creating a home directory. For a project whose files are "
            + "somewhere this recipe should not touch, so it requires --repo: with no home there is "
            + "nowhere for a bare clone to go, and nothing to cut worktrees from. h9k project init "
            + "gives the project a home later.")]
        public bool NoHome { get; init; }
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

        if (settings.RepositoryPath.IsBlank() && settings.RepositoryUrl.IsBlank())
        {
            throw new DomainValidationException(
                "A project needs somewhere to get its code from: pass --repo-url so the home can "
                + "bare-clone it, or --repo to register against a repository that already exists here.");
        }

        // Without a home there is no repo/ for a clone to land in, so the repository path can only
        // come from --repo. Left unchecked, the path would be composed from an empty home and
        // recorded as the relative 'repo/<name>.git', which the daemon resolves against its own
        // working directory rather than against anything that exists.
        if (settings.NoHome && settings.RepositoryPath.IsBlank())
        {
            throw new DomainValidationException(
                "--no-home leaves this project no directory of its own, so there is nowhere for the "
                + "home's repo/ to be cloned into and nothing for the daemon to cut worktrees from. "
                + "Pass --repo <path> to register against a repository that already exists here, or "
                + "drop --no-home and let the home hold the clone.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // The home is resolved before registration because the repository path may come out of
        // it: with a remote and no --repo, the repository the daemon cuts worktrees from IS the
        // bare clone inside the home, and recording anything else would leave the two disagreeing.
        ProjectHome home = settings.NoHome
            ? ProjectHome.None
            : ProjectHome.Parse(settings.Home.IsNotBlank()
                ? Path.GetFullPath(settings.Home)
                : ProjectHomePaths.DefaultFor(name));

        string repositoryPath = settings.RepositoryPath.IsNotBlank()
            ? Path.GetFullPath(settings.RepositoryPath)
            : ProjectHomePaths.BareRepository(home.Value, name);

        await ProjectHomeClaims.EnsureUnclaimedAsync(
            session, projectId: Guid.Empty, home.Value, repositoryPath, cancellationToken);

        Guid projectId = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId,
            context.OwnerId,
            context.ConnectionId,
            name,
            repositoryPath,
            settings.RepositoryUrl.IsBlank() ? null : GitRemoteUrl.Parse(settings.RepositoryUrl),
            settings.BaseBranch,
            DateTimeOffset.UtcNow,
            home);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-added:{projectId}", cancellationToken);

        AnsiConsole.MarkupLine($"[green]Project '{name.EscapeMarkup()}' registered.[/] Id: [dim]{projectId}[/]");

        if (!home.HasValue)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No home created (--no-home). Give it one later:[/] h9k project init {name.EscapeMarkup()}");
            return ExitCodes.Ok;
        }

        // The home is built after the registration lands rather than before it, so a half-made
        // directory never outlives a registration that failed — and so the recipe renders
        // AGENTS.md from the project as recorded rather than from the command line as typed.
        //
        // --repo says the repository to dispatch from already exists elsewhere, and the option
        // promises the home's repo/ is left unmaterialised. A clone made here anyway would be a
        // second copy nothing ever cuts a worktree from, which is the decoration project init
        // exists to avoid leaving behind.
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
        IReadOnlyList<ProjectHomeStep> steps = await ProjectHomeRecipe.BuildAsync(
            home.Value, project, cancellationToken, materialiseRepository: settings.RepositoryPath.IsBlank());

        return ProjectHomeRecipe.Report(steps) ? ExitCodes.Ok : ExitCodes.Error;
    }
}
