using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The platform's real <see cref="ProcessRunner"/> (idea 202383dc, A2b item 4), against a real
/// Marten/Postgres session — the project lookup by repository path is a real query, not something
/// a fake can stand in for, per Brian's 2026-09-13 testing rule (account resolution itself is
/// already covered this way by <c>ProjectJoinCommandTests</c>; this class is the one new thing this
/// task adds on top of it).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class ProjectScopedGitHubRunnerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-scoped-runner-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ProjectScopedGitHubRunnerTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_git_call_passes_straight_through_without_touching_the_project_account()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        RecordingProcessRunner passthrough = RecordingProcessRunner.Succeeding("on main");
        ProjectScopedGitHubRunner runner = new(_postgres.Store, passthrough: passthrough.Runner);

        ProcessResult result = await runner.Runner("git", ["status"], "/anywhere/at/all", cts.Token);

        result.StandardOutput.Should().Be("on main");
        passthrough.Calls.Single().Arguments.Should().ContainInOrder("status");
    }

    [Fact]
    public async Task A_gh_call_pins_the_token_of_the_project_whose_repository_lives_at_the_working_directory()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectAsync("/repos/hall9k", cts.Token);
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-test-user\n");
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, name => null);
        ProjectScopedGitHubRunner runner = new(_postgres.Store, client);

        await runner.Runner("gh", ["repo", "view"], project.RepositoryPath, cts.Token);

        tokenRunner.Calls.Single().Arguments.Should().ContainInOrder("auth", "token", "--user", "test-user");
        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("token-for-test-user");
        ghRunner.Calls.Single().Arguments.Should().ContainInOrder("repo", "view");
    }

    [Fact]
    public async Task A_gh_call_from_a_directory_no_registered_project_owns_refuses_rather_than_guessing_an_account()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync("/repos/hall9k", cts.Token);
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectScopedGitHubRunner runner = new(_postgres.Store, new ProjectGitHubClient(ghRunner.Runner));

        Func<Task> run = () => runner.Runner("gh", ["repo", "view"], "/somewhere/unregistered", cts.Token);

        (await run.Should().ThrowAsync<DomainNotFoundException>()).WithMessage("*/somewhere/unregistered*");
        ghRunner.Calls.Should().BeEmpty("no project account to pin means gh is never actually run");
    }

    [Fact]
    public async Task A_gh_call_against_an_unconfirmed_connection_refreshes_the_identity_live_before_giving_up()
    {
        // The exact gap independent pre-PR review (Copilot, PR #399) named: an install whose
        // GitHub connection was registered before its numeric id/login was ever observed — the
        // shape every pre-migration connection is in — must not start refusing an ordinary
        // command that worked a moment ago against ambient gh.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectWithUnconfirmedConnectionAsync("/repos/hall9k", cts.Token);
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding("token-for-observed-user\n");
        ProjectGitHubClient client = new(ghRunner.Runner, tokenRunner.Runner, name => null);
        ProjectScopedGitHubRunner runner = new(
            _postgres.Store, client,
            identityReaderFactory: _ => () => """{"id": 99, "login": "observed-user"}""");

        await runner.Runner("gh", ["repo", "view"], project.RepositoryPath, cts.Token);

        tokenRunner.Calls.Single().Arguments.Should().ContainInOrder("auth", "token", "--user", "observed-user");
        ghRunner.Calls.Single().Environment["GH_TOKEN"].Should().Be("token-for-observed-user");
    }

    [Fact]
    public async Task A_gh_call_against_an_unconfirmed_connection_gh_still_cannot_confirm_refuses_as_before()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        ProjectDetails project = await SeedProjectWithUnconfirmedConnectionAsync("/repos/hall9k", cts.Token);
        RecordingEnvironmentProcessRunner ghRunner = RecordingEnvironmentProcessRunner.Succeeding("{}");
        ProjectGitHubClient client = new(ghRunner.Runner);
        ProjectScopedGitHubRunner runner = new(
            _postgres.Store, client, identityReaderFactory: _ => () => null);

        Func<Task> run = () => runner.Runner("gh", ["repo", "view"], project.RepositoryPath, cts.Token);

        await run.Should().ThrowAsync<DomainValidationException>().WithMessage("*no confirmed GitHub account*");
    }

    private async Task<ProjectDetails> SeedProjectWithUnconfirmedConnectionAsync(
        string repositoryPath, CancellationToken cancellationToken)
    {
        Guid connectionId = DomainId.New();
        await using IDocumentSession connectionSession = _postgres.Store.LightweightSession();
        connectionSession.Events.StartStream<ConnectionAggregate>(
            connectionId,
            ConnectionDecider.Register(
                connectionId, Guid.Empty, WorkItemProvider.GitHub, "legacy-external-id", CredentialReference.GhCli, Now));
        await connectionSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, Guid.Empty, connectionId, "smoke", repositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }

    private async Task<ProjectDetails> SeedProjectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, context.ConnectionId, "smoke", repositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }
}
