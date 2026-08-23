using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The adopt path: gives a registered project the home it does not have yet, and repairs one
/// that is incomplete. Same recipe <c>h9k project add</c> runs, same shape, so a project
/// registered before homes existed ends up indistinguishable from one registered today.
/// <para>
/// It always materialises <c>repo/</c> fresh from the recorded remote. A clone that already
/// exists elsewhere on this machine is inconsequential — git is distributed, including across
/// one disk — and moving work out of an old location is a one-time, agent-assisted migration
/// rather than anything this recipe should attempt (ruled at the project-home discovery).
/// </para>
/// </summary>
public sealed class ProjectInitCommand : Hall9kAsyncCommand<ProjectInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description(
            "Project to give a home: its name, an unambiguous fragment of it, or its full id "
            + "(h9k project list shows them all)")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--home <PATH>")]
        [Description(
            "Where to create it (default: the project's recorded home, or ~/.hall9k/projects/<name> "
            + "when it has none). Passing a different path here records the new location — it does "
            + "not move anything out of the old one.")]
        public string? Home { get; init; }

        [CommandOption("--keep-repo-path")]
        [Description(
            "Leave the project's recorded repository path alone. By default, once repo/ is "
            + "materialised the project is re-pointed at it, because a home whose repo/ nothing "
            + "dispatches against is decoration. Pass this while worktrees are live under the old "
            + "path and you intend to relocate them yourself.")]
        public bool KeepRepositoryPath { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);

        ProjectHome home = ProjectHome.Parse(settings.Home.IsNotBlank()
            ? Path.GetFullPath(settings.Home)
            : project.HomeDirectory.HasValue
                ? project.HomeDirectory.Value
                : ProjectHomePaths.DefaultFor(project.Name));

        // Before anything is recorded or created: two projects in one home share a bare clone and
        // a generated AGENTS.md, and the recipe reports every step of that as a successful repair.
        await ProjectHomeClaims.EnsureUnclaimedAsync(
            session,
            project.Id,
            home.Value,
            ProjectHomePaths.BareRepository(home.Value, project.Name),
            cancellationToken);

        if (home.Value != project.HomeDirectory.Value)
        {
            project = await RecordAsync(
                session, project, homeDirectory: Optional<ProjectHome>.Of(home), cancellationToken: cancellationToken);
            AnsiConsole.MarkupLine($"[green]Home recorded:[/] {home.Value.EscapeMarkup()}");
        }

        bool succeeded = ProjectHomeRecipe.Report(
            await ProjectHomeRecipe.BuildAsync(home.Value, project, cancellationToken));
        if (!succeeded)
        {
            return ExitCodes.Error;
        }

        ProjectDetails repointed = await PointAtTheHomesRepositoryAsync(session, project, home, settings, cancellationToken);
        if (!ReferenceEquals(repointed, project))
        {
            // The recipe rendered AGENTS.md from the project as it stood a moment ago; the
            // re-point is a fact that render was written before, so it gets rendered again.
            ProjectHomeRecipe.Report([ProjectAgentsDocument.Write(home.Value, repointed)]);
        }

        AnsiConsole.MarkupLine(
            $"[dim]Start a session in {home.Value.EscapeMarkup()} — its AGENTS.md names the layout, "
            + "the tools, and the commands.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The adopt half. A materialised <c>repo/</c> that nothing dispatches against is decoration,
    /// so once the bare clone is really there the project is re-pointed at it — loudly, naming
    /// the path it used to hold, because nothing is moved out of that path. Relocating live
    /// worktrees is the cutover chore's agent-assisted job (backlog 52), and
    /// <c>--keep-repo-path</c> is how somebody mid-cutover says "not yet".
    /// </summary>
    private static async Task<ProjectDetails> PointAtTheHomesRepositoryAsync(
        IDocumentSession session,
        ProjectDetails project,
        ProjectHome home,
        Settings settings,
        CancellationToken cancellationToken)
    {
        string bare = ProjectHomePaths.BareRepository(home.Value, project.Name);
        if (project.RepositoryPath == bare || !Directory.Exists(Path.Combine(bare, "objects")))
        {
            return project;
        }

        if (settings.KeepRepositoryPath)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Repository path left at {project.RepositoryPath.EscapeMarkup()}[/] (--keep-repo-path) — "
                + $"worktrees are still cut there, not from {bare.EscapeMarkup()}.");
            return project;
        }

        string previous = project.RepositoryPath;
        ProjectDetails repointed = await RecordAsync(
            session, project, repositoryPath: Optional<string>.Of(bare), cancellationToken: cancellationToken);
        AnsiConsole.MarkupLine(
            $"[green]Repository path now {bare.EscapeMarkup()}[/] — worktrees are cut from the home's clone "
            + $"from now on. Nothing was moved out of {previous.EscapeMarkup()}; anything live there is yours "
            + "to relocate.");
        return repointed;
    }

    /// <summary>
    /// Appends one settings change and re-reads the projection. Re-reading rather than patching
    /// the copy in hand is deliberate: the render below is a render of what the store holds, and
    /// building it from a locally mutated view would render what this process hoped it held.
    /// </summary>
    private static async Task<ProjectDetails> RecordAsync(
        IDocumentSession session,
        ProjectDetails project,
        CancellationToken cancellationToken,
        Optional<ProjectHome> homeDirectory = default,
        Optional<string> repositoryPath = default)
    {
        ProjectAggregate aggregate =
            (await session.Events.AggregateStreamAsync<ProjectAggregate>(project.Id, token: cancellationToken))!;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            aggregate,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            DateTimeOffset.UtcNow,
            context.OwnerId,
            homeDirectory: homeDirectory,
            repositoryPath: repositoryPath);
        session.Events.Append(project.Id, changed);
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-changed:{project.Id}", cancellationToken);

        return (await session.LoadAsync<ProjectDetails>(project.Id, cancellationToken))!;
    }
}
