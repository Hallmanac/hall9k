using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>
/// <c>h9k doctor</c>'s probe for the tool list the generated project <c>AGENTS.md</c> already
/// promises is checked up front (<see cref="ProjectAgentsDocument.ToolDependencies"/>): <c>git</c>
/// always, and <c>gh</c> only when a registered project's remote is GitHub — the identical
/// <see cref="ProjectAgentsDocument.NeedsGitHubCli"/> fact the render itself derives from, so this
/// probe and that render can never name two different lists. There is no Atlassian CLI leg:
/// Decisions Log #114 moved Jira writes onto hall9k's own REST client, and <see cref="JiraDoctor"/>
/// already probes that connection's credentials — a local tool was never the thing that could be
/// missing there.
/// <para>
/// Runs before the database section of <c>h9k doctor</c> and needs no daemon: it reads
/// <see cref="ProjectDetails"/> straight off Postgres when it can, and degrades to the git-only
/// check when it cannot — an unconfigured or unreachable database is exactly the situation the
/// database section diagnoses next, not a reason to skip the tool check that runs before it. That
/// read is bounded to <see cref="ProjectReadTimeout"/> and never creates schema itself (it opens
/// through <see cref="CliStore.Open(string, JasperFx.AutoCreate)"/> with
/// <see cref="AutoCreate.None"/>, and only after confirming the schema is already there, so the
/// same pre-generated storage code every other command reuses answers this read too instead of a
/// fresh dynamic compile): the database section below is the one place <c>h9k doctor</c> is
/// allowed to write to an unconfigured database, and only after asking.
/// </para>
/// <para>
/// Presence only, never auth: a present tool is reported quietly, and a missing one gets a
/// teaching message naming the install (and, for <c>gh</c>, the login) fix, per the CLI command
/// standards. Nothing here probes whether an installed tool is actually authenticated — that is
/// a different question, answered the moment the tool is actually used, or in <c>gh</c>'s case
/// during ordinary use of GitHub-backed commands.
/// </para>
/// </summary>
public static class ToolDoctor
{
    /// <summary>
    /// How long the project read (reachability, schema check, and the query together) gets before
    /// giving up and reporting the <c>gh</c> requirement as unconfirmed — the same budget
    /// <see cref="DatabaseReachability"/>'s own probe uses, so an unreachable database is named
    /// quickly here too instead of stalling ahead of the database section's own diagnosis of it.
    /// </summary>
    private static readonly TimeSpan ProjectReadTimeout = TimeSpan.FromSeconds(3);

    public static Task RunAsync(CancellationToken cancellationToken) =>
        RunAsync(ExternalProcess.Runner, cancellationToken);

    internal static async Task RunAsync(ProcessRunner runner, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]Tools[/]");

        await ProbeAsync(runner, "git", MissingGitMessage(), cancellationToken);

        switch (await GitHubCliRequirementAsync(cancellationToken))
        {
            case GitHubCliRequirement.Needed:
                await ProbeAsync(runner, "gh", MissingGhMessage(), cancellationToken);
                break;
            case GitHubCliRequirement.Unknown:
                AnsiConsole.MarkupLine(
                    "[yellow]  Could not confirm whether gh needs checking[/]: the registered projects "
                    + "could not be read, most likely because no connection string is configured yet, "
                    + "the database is not reachable right now, or the platform config file naming it "
                    + "is broken. The database section below diagnoses that; once it is fixed, run "
                    + "h9k doctor again to get gh's own check.");
                break;
            case GitHubCliRequirement.NotNeeded:
                break;
        }

        AnsiConsole.WriteLine();
    }

    private enum GitHubCliRequirement
    {
        /// <summary>Confirmed: the database is reachable, its schema is there (or has never been
        /// created, which means nothing could have been registered yet), and no registered project
        /// binds a GitHub remote.</summary>
        NotNeeded,

        /// <summary>Confirmed: at least one registered project binds a GitHub remote.</summary>
        Needed,

        /// <summary>The registered projects could not be read (no connection string configured, an
        /// unreachable database, a timeout, a broken platform config file, or any other failure) —
        /// there might be a project that needs gh, but this pass could not say either way.</summary>
        Unknown,
    }

    /// <summary>
    /// Reads every registered, non-archived project's <see cref="ProjectDetails"/> to ask whether
    /// any of them binds a GitHub remote, bounded to <see cref="ProjectReadTimeout"/> and never
    /// creating schema. An archived project is excluded the same way
    /// <see cref="Hall9k.Cli.Commands.ProjectListCommand"/> and task start/assign/publish already
    /// treat it as removed: nothing active needs gh on its account.
    /// A database that is reachable, with its schema already there, but with zero registered
    /// projects, is the only case where <see cref="GitHubCliRequirement.NotNeeded"/> is an observed
    /// fact rather than a guess — including a schema that has never been created yet, which means
    /// nothing could have been registered there. Every other case where the fact cannot actually be
    /// read — no connection string configured at all (<see cref="ConnectionStringOrigin.None"/>), a
    /// platform config file that exists but is broken
    /// (<see cref="ConnectionStringOrigin.PlatformConfigFileMalformed"/> or
    /// <see cref="ConnectionStringOrigin.PlatformConfigFileUnreadable"/>), or an unreachable
    /// database — reports <see cref="GitHubCliRequirement.Unknown"/> instead: install deliberately
    /// leaves a machine unconfigured when something is already listening on 5432
    /// (<see cref="InstallCommand"/>), and <see cref="DatabaseDoctor.DiagnoseNotConfiguredAsync"/>
    /// can find a running <c>hall9k-postgres</c> container full of already-registered projects from
    /// that same unconfigured state, so "no connection string resolves" is never proof that nothing
    /// was ever registered. AGENTS.md's "never guess at unobserved facts" rule applies here the same
    /// as anywhere else.
    /// </summary>
    private static async Task<GitHubCliRequirement> GitHubCliRequirementAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConnectionStringResolution resolution = Hall9kDatabase.Resolve();
            if (resolution.Origin is ConnectionStringOrigin.PlatformConfigFileMalformed
                or ConnectionStringOrigin.PlatformConfigFileUnreadable)
            {
                return GitHubCliRequirement.Unknown;
            }

            if (resolution.Value is not { } connectionString)
            {
                // Origin.None: nothing resolved anywhere in the precedence chain. That is not
                // proof nothing was ever registered — install deliberately leaves a machine
                // unconfigured when something is already listening on 5432, and doctor's own
                // database section can find a running hall9k-postgres container full of
                // already-registered projects from exactly this state.
                return GitHubCliRequirement.Unknown;
            }

            using CancellationTokenSource timeout = new(ProjectReadTimeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            ReachabilityReport reachability = await DatabaseReachability.ProbeAsync(connectionString, linked.Token);
            if (reachability.Status != ReachabilityStatus.Reachable)
            {
                return GitHubCliRequirement.Unknown;
            }

            if (!await DatabaseReachability.SchemaPresentAsync(connectionString, linked.Token))
            {
                // Reachable, but Hall9k's schema has never been created — nothing has ever
                // registered a project here, so there is nothing that could need gh yet. Opening a
                // store here (even to just read) would be the write this method must not make.
                return GitHubCliRequirement.NotNeeded;
            }

            using DocumentStore store = CliStore.Open(connectionString, AutoCreate.None);
            await using IDocumentSession session = store.LightweightSession();
            IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(linked.Token);
            return projects.Where(project => !project.IsArchived).Any(ProjectAgentsDocument.NeedsGitHubCli)
                ? GitHubCliRequirement.Needed
                : GitHubCliRequirement.NotNeeded;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The 3s project-read timeout fired, not the caller's own token.
            return GitHubCliRequirement.Unknown;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return GitHubCliRequirement.Unknown;
        }
    }

    private static async Task ProbeAsync(
        ProcessRunner runner, string tool, string missingMessage, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await runner(tool, ["--version"], Directory.GetCurrentDirectory(), cancellationToken);
            if (result.ExitCode != 0)
            {
                // The binary exists but failed to answer — e.g. a bare-stub git on a fresh Mac
                // without the Xcode Command Line Tools, which exits non-zero rather than throwing.
                // RepoMaterialiser.GitAbsentAsync already treats this the same way for git alone.
                AnsiConsole.MarkupLine(
                    $"[red]  {tool} did not report its version successfully[/] (exit code {result.ExitCode}): "
                    + $"{missingMessage.EscapeMarkup()}");
                return;
            }

            AnsiConsole.MarkupLine($"[dim]  {tool} is installed.[/]");
        }
        catch (Win32Exception)
        {
            // .NET reports a missing binary this way regardless of platform (the same fact
            // CouldNotStartGh already leans on for gh specifically) — the tool is not on PATH.
            AnsiConsole.MarkupLine($"[red]  {tool} is not installed or not on PATH[/]: {missingMessage.EscapeMarkup()}");
        }
        catch (TimeoutException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Could not confirm {tool} is installed[/]: the version check did not answer "
                + "in time. Retry once it is answering reliably.");
        }
    }

    private static string MissingGitMessage() =>
        OperatingSystem.IsWindows()
            ? "install it (winget: winget install Git.Git, or the installer at "
                + "https://git-scm.com/download/win), then try again."
            : "install it (Homebrew: brew install git; apt: sudo apt install git), then try again.";

    private static string MissingGhMessage() =>
        "a registered project's remote is GitHub, and the platform's pull-request and issue work "
        + "goes through gh. Install it from https://cli.github.com, then run `gh auth login` and "
        + "`gh auth setup-git` so clones and pushes can authenticate.";
}
