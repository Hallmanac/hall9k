using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
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

        [CommandOption("--reactivate-archived")]
        [Description(
            "When --name already names an archived project (h9k project remove), reactivate it in "
            + "place instead of registering a new one — same id, settings, tasks, ideas, and home; "
            + "the daemon's sweeps resume for it. Selects the 'yes' answer to the interactive prompt "
            + "this collision otherwise asks, so a non-interactive run never hangs on it.")]
        public bool ReactivateArchived { get; init; }

        [CommandOption("--rename-archived-to <NEW_NAME>")]
        [Description(
            "When --name already names an archived project, rename the archived one to NEW_NAME "
            + "first — freeing --name for this registration — then register as normal. Selects the "
            + "'rename' answer to the interactive prompt this collision otherwise asks.")]
        public string? RenameArchivedTo { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        string name = settings.Name;
        ProjectDetails? existing = await session.Query<ProjectDetails>()
            .FirstOrDefaultAsync(p => p.Name == name, cancellationToken);

        if (existing is not null)
        {
            if (!existing.IsArchived)
            {
                throw new DomainConflictException($"A project named '{name}' already exists.");
            }

            if (await HandleArchivedCollisionAsync(session, existing, settings, cancellationToken) is { } exitCode)
            {
                return exitCode;
            }
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

        // Every newly registered project skips permission prompts from its very first run (Windows
        // field report, 2026-08-31: a project that started with prompts live killed every headless
        // dispatch on it, diagnosable only from the transcript). There is deliberately no flag to
        // register with prompts left live — an operator who wants that reverts it the normal way,
        // h9k project set <name> --skip-permissions false, after registration. Amends Decisions Log
        // #9's per-project opt-in to a per-project opt-out at registration (log #181).
        ProjectRegistered registered = ProjectDecider.Register(
            projectId,
            context.OwnerId,
            context.ConnectionId,
            name,
            repositoryPath,
            settings.RepositoryUrl.IsBlank() ? null : GitRemoteUrl.Parse(settings.RepositoryUrl),
            settings.BaseBranch,
            DateTimeOffset.UtcNow,
            home,
            skipPermissions: true);
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
        bool ok = ProjectHomeRecipe.Report(steps);

        AnsiConsole.MarkupLine(OrchestratorPointer.ForProject(name));

        return ok ? ExitCodes.Ok : ExitCodes.Error;
    }

    /// <summary>
    /// A collision with an archived project's name: reactivate it in place, rename it to free the
    /// name, or refuse and name the three choices — a flag selects one non-interactively, and an
    /// interactive session with neither flag is asked (task: a project can be archived, listed as
    /// archived, reactivated, and renamed). Returns an exit code when the caller should stop right
    /// here (reactivation registers nothing new); null means the collision is resolved (a rename
    /// has been appended, unsaved, onto <paramref name="existing"/>'s own stream) and the caller
    /// should carry on registering the new project under the originally requested name.
    /// </summary>
    private static async Task<int?> HandleArchivedCollisionAsync(
        IDocumentSession session, ProjectDetails existing, Settings settings, CancellationToken cancellationToken)
    {
        ProjectAggregate archived = await session.Events.AggregateStreamAsync<ProjectAggregate>(existing.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {existing.Id}.");

        if (settings.ReactivateArchived)
        {
            return await ReactivateInPlaceAsync(session, archived, existing, cancellationToken);
        }

        if (settings.RenameArchivedTo.IsNotBlank())
        {
            await RenameArchivedInPlaceAsync(session, archived, existing, settings.RenameArchivedTo, cancellationToken);
            return null;
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new DomainValidationException(ArchivedCollisionChoices(existing));
        }

        AnsiConsole.MarkupLine(
            $"[yellow]A project named '{existing.Name.EscapeMarkup()}' is archived[/] "
            + $"(since {existing.ArchivedAt:g}).");
        if (AnsiConsole.Confirm("Reactivate it instead of registering a new one?", defaultValue: false))
        {
            return await ReactivateInPlaceAsync(session, archived, existing, cancellationToken);
        }

        if (AnsiConsole.Confirm("Rename the archived project so this name is free for the new one?", defaultValue: false))
        {
            string newName = AnsiConsole.Ask<string>("New name for the archived project:");
            await RenameArchivedInPlaceAsync(session, archived, existing, newName, cancellationToken);
            return null;
        }

        throw new DomainValidationException(
            $"Register the new project under a different --name than '{existing.Name}'.");
    }

    private static string ArchivedCollisionChoices(ProjectDetails existing) =>
        $"A project named '{existing.Name}' is archived (since {existing.ArchivedAt:g}). Choose one: "
        + $"reactivate it (h9k project add --name {existing.Name} --reactivate-archived, or "
        + $"h9k project reactivate {existing.Name}), rename the archived one to free this name "
        + $"(h9k project add --name {existing.Name} --rename-archived-to <NEW_NAME>, or "
        + $"h9k project rename {existing.Name} <NEW_NAME>), or register the new project under a "
        + "different --name.";

    private static async Task<int> ReactivateInPlaceAsync(
        IDocumentSession session, ProjectAggregate archived, ProjectDetails existing, CancellationToken cancellationToken)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(existing.Id, ProjectDecider.Reactivate(archived, DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-reactivated:{existing.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Project '{existing.Name.EscapeMarkup()}' reactivated.[/] Same id, settings, tasks, "
            + "ideas, and home; the daemon's sweeps resume for it. This is this install's own record — "
            + "a registration of the same repository on another node is unaffected.");
        ProjectHomeDirectoryStatus.Report(existing);
        return ExitCodes.Ok;
    }

    private static async Task RenameArchivedInPlaceAsync(
        IDocumentSession session, ProjectAggregate archived, ProjectDetails existing, string newName,
        CancellationToken cancellationToken)
    {
        await ProjectNameUniqueness.CheckAsync(session, newName, excludingProjectId: existing.Id, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(
            existing.Id, ProjectDecider.Rename(archived, newName, DateTimeOffset.UtcNow, context.OwnerId));

        AnsiConsole.MarkupLine(
            $"[dim]Archived project '{existing.Name.EscapeMarkup()}' renamed to '{newName.EscapeMarkup()}' "
            + "— the home directory on disk keeps its old folder name.[/]");
    }

}
