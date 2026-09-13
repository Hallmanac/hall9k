using System.ComponentModel;
using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Npgsql;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="ToolDoctor"/> reads which tools a registered project needs off the same
/// <c>ProjectDetails</c> facts <c>ProjectAgentsDocument.NeedsGitHubCli</c> derives the generated
/// <c>AGENTS.md</c>'s <c>gh</c> entry from — this exercises that read against a real Postgres,
/// since the Cli-tier <see cref="Hall9k.Tests.Cli.ToolDoctorTests"/> can only prove the git-only
/// degrade when no database is reachable at all.
/// <para>
/// Each test seeds a database of its own on the fixture's shared container
/// (<see cref="FreshDatabaseAsync"/>), the same isolation <c>DatabaseDoctorTests</c> uses: the
/// probe aggregates across every registered project on whatever install it points at, so two
/// tests sharing one database would see each other's seeded projects.
/// </para>
/// </summary>
// HALL9K_CONNECTION_STRING is process-wide state; sharing the collection serializes this against
// every other test that redirects it.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ToolDoctorProjectFactsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_registered_project_with_a_github_remote_makes_gh_get_probed()
    {
        string database = await FreshDatabaseAsync(CancellationToken.None);
        await SeedProjectAsync(database, new Uri("https://github.com/hallmanac/hall9k"), CancellationToken.None);

        List<string> probed = [];
        ProcessRunner runner = (fileName, _, _, _) =>
        {
            probed.Add(fileName);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        };

        await WithConnectionStringAsync(database, () => ToolDoctor.RunAsync(runner, CancellationToken.None));

        probed.Should().Contain("git").And.Contain("gh",
            "a registered project's remote is GitHub, the same fact the generated AGENTS.md's gh entry reads");
    }

    /// <summary>
    /// The Cli-tier tests can only reach <c>GitHubCliRequirement.Unknown</c> or <c>NotNeeded</c>,
    /// since every one of them runs against a database that cannot be read at all — none of them
    /// ever reaches the <c>Needed</c> branch that actually probes <c>gh</c> and, when it is
    /// missing, renders <see cref="ToolDoctor"/>'s teaching message. Seeding a GitHub-remote
    /// project here is what lets a regression that drops the install URL or either auth command
    /// fail a test instead of shipping quietly.
    /// </summary>
    [Fact]
    public async Task A_missing_gh_is_taught_when_a_registered_project_needs_it()
    {
        string database = await FreshDatabaseAsync(CancellationToken.None);
        await SeedProjectAsync(database, new Uri("https://github.com/hallmanac/hall9k"), CancellationToken.None);

        ProcessRunner runner = (fileName, _, _, _) => fileName == "gh"
            ? throw new Win32Exception("No such file or directory")
            : Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        string output = await WithConnectionStringAsync(
            database, () => CaptureAsync(() => ToolDoctor.RunAsync(runner, CancellationToken.None)));

        output.Should().Contain("gh is not installed");
        output.Should().Contain("https://cli.github.com",
            "a missing gh has to name the install fix, not just that gh is absent");
        output.Should().Contain("gh auth login").And.Contain("gh auth setup-git",
            "a missing gh has to name both login steps so clones and pushes can authenticate");
    }

    /// <summary>
    /// Every other surface treats an archived project as removed (<see cref="ProjectListCommand"/>
    /// hides it, task start/assign/publish refuse it), so the tool check must not send an operator
    /// off to install and authenticate gh for a GitHub remote nothing active still needs.
    /// </summary>
    [Fact]
    public async Task An_archived_projects_github_remote_never_probes_gh()
    {
        string database = await FreshDatabaseAsync(CancellationToken.None);
        Guid id = await SeedProjectAsync(database, new Uri("https://github.com/hallmanac/hall9k"), CancellationToken.None);
        await ArchiveProjectAsync(database, id, CancellationToken.None);

        List<string> probed = [];
        ProcessRunner runner = (fileName, _, _, _) =>
        {
            probed.Add(fileName);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        };

        await WithConnectionStringAsync(database, () => ToolDoctor.RunAsync(runner, CancellationToken.None));

        probed.Should().Contain("git");
        probed.Should().NotContain("gh", "an archived project is treated as removed everywhere else, so its GitHub remote no longer needs gh");
    }

    [Fact]
    public async Task A_registered_project_with_no_github_remote_never_probes_gh()
    {
        string database = await FreshDatabaseAsync(CancellationToken.None);
        await SeedProjectAsync(database, repositoryUrl: null, CancellationToken.None);

        List<string> probed = [];
        ProcessRunner runner = (fileName, _, _, _) =>
        {
            probed.Add(fileName);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        };

        await WithConnectionStringAsync(database, () => ToolDoctor.RunAsync(runner, CancellationToken.None));

        probed.Should().Contain("git");
        probed.Should().NotContain("gh", "nothing registered on this install needs the GitHub CLI");
    }

    /// <summary>
    /// The tool check must never be the thing that creates Hall9k's schema: that offer belongs to
    /// the database section right after it, which asks "Shall I set that up now?" before writing
    /// anything. A reachable-but-untouched database is exactly the shape a fresh install or a
    /// stray connection string pointed at the wrong database presents.
    /// </summary>
    [Fact]
    public async Task An_untouched_reachable_database_is_left_without_a_schema()
    {
        string database = await FreshDatabaseAsync(CancellationToken.None);
        ProcessRunner runner = (_, _, _, _) => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));

        await WithConnectionStringAsync(database, () => ToolDoctor.RunAsync(runner, CancellationToken.None));

        (await DatabaseReachability.SchemaPresentAsync(database, CancellationToken.None)).Should().BeFalse(
            "the tool check reads projects, and only the database section is allowed to create schema, after asking");
    }

    private static async Task WithConnectionStringAsync(string connectionString, Func<Task> action)
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, connectionString);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    private static async Task<T> WithConnectionStringAsync<T>(string connectionString, Func<Task<T>> action)
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, connectionString);
        try
        {
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    /// <summary>Mirrors <c>Hall9k.Tests.Cli.ToolDoctorTests</c>' own capture helper: Spectre
    /// consumes markup tags before they reach the writer, so assertions match the rendered text
    /// a missing tool's teaching message actually prints, not the style tag that colored it.</summary>
    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 4096;
        AnsiConsole.Console = captured;
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private static async Task<Guid> SeedProjectAsync(string connectionString, Uri? repositoryUrl, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            id, DomainId.New(), DomainId.New(), $"project-{id:N}", $"/repos/{id:N}.git",
            repositoryUrl, "main", DateTimeOffset.UtcNow);

        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(id, registered);
        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    private static async Task ArchiveProjectAsync(string connectionString, Guid projectId, CancellationToken cancellationToken)
    {
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(projectId, new ProjectArchived(projectId, null, DateTimeOffset.UtcNow, DomainId.New()));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A database of the caller's own on this fixture's container, the same isolation
    /// <c>DatabaseDoctorTests.FreshDatabaseAsync</c> uses and for the identical reason: a fresh
    /// database is what lets each test's own registered project stand alone.</summary>
    private async Task<string> FreshDatabaseAsync(CancellationToken cancellationToken)
    {
        string name = $"h9k_tool_doctor_{Guid.NewGuid():N}";

        await using (NpgsqlConnection admin = new(postgres.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }
}
