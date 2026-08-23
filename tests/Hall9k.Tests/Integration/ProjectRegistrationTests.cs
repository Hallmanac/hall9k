using FluentAssertions;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public sealed class ProjectRegistrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Registering_a_project_round_trips_through_events_aggregate_and_projection()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerRegistered owner = OwnerDecider.Register(ownerId, "Brian Hall", "brian@hallmanac.com", Now);
            session.Events.StartStream<OwnerAggregate>(ownerId, owner);

            ConnectionRegistered connection = ConnectionDecider.Register(
                connectionId, ownerId, WorkItemProvider.GitHub, "Hallmanac", CredentialReference.GhCli, Now);
            session.Events.StartStream<ConnectionAggregate>(connectionId, connection);

            ProjectRegistered project = ProjectDecider.Register(
                projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
                new Uri("https://github.com/Hallmanac/hall9k"), null, Now);
            session.Events.StartStream<ProjectAggregate>(projectId, project);

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectAggregate? aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(
                projectId, token: cts.Token);
            aggregate.Should().NotBeNull();

            ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
                aggregate!,
                verifyCommands: new List<VerifyCommand> { new("build", "dotnet build"), new("test", "dotnet test") },
                skipPermissions: true,
                maxParallelAgents: Optional<int>.None,
                contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                changedAt: Now.AddMinutes(1), changedByOwnerId: ownerId);
            session.Events.Append(projectId, changed);

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            ProjectDetails? details = await query.LoadAsync<ProjectDetails>(
                projectId, cts.Token);

            details.Should().NotBeNull();
            details!.Name.Should().Be("hall9k");
            details.BaseBranch.Should().Be("main", "the decider defaults a blank base branch");
            details.VerifyCommands.Should().HaveCount(2);
            details.SkipPermissions.Should().BeTrue();
            details.MaxParallelAgents.Should().Be(3, "absent optionals leave settings unchanged");
            details.OwnerId.Should().Be(ownerId);
            details.ConnectionId.Should().Be(connectionId);
        }
    }

    /// <summary>
    /// The default home is derived from the project name through a lossy slug, so two different
    /// names reach one directory. Origin incident (2026-08-23): the pre-PR review of the
    /// project-home branch traced "My App" and "my-app" through h9k project add and found both
    /// registering happily, both resolving to <c>~/.hall9k/projects/my-app</c>, and the second
    /// overwriting the first's generated AGENTS.md while every step reported success. The second
    /// cycle found h9k project set --home walking straight past the guard the first one added,
    /// which is why the check is scoped to whatever a command is changing rather than to a
    /// registration.
    /// </summary>
    [Fact]
    public async Task Two_projects_cannot_claim_one_home_or_one_repository_path()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        string home = Path.Combine(Path.GetTempPath(), $"hall9k-claims-{Guid.NewGuid():N}", "my-app");
        string bare = ProjectHomePaths.BareRepository(home, "My App");
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
                projectId, DomainId.New(), DomainId.New(), "My App", bare, null, null, Now,
                ProjectHome.Parse(home)));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            Func<Task> sameHome = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, Guid.Empty, home, Path.Combine(home, "repo", "elsewhere.git"), cts.Token);
            await sameHome.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*My App*already lives*");

            Func<Task> sameRepository = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, Guid.Empty, Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}"), bare, cts.Token);
            await sameRepository.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*My App*already cuts its worktrees*");

            // The project that holds the claim may always re-claim it, which is what makes
            // h9k project init idempotent against a home it created itself.
            Func<Task> itsOwn = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, projectId, home, bare, cts.Token);
            await itsOwn.Should().NotThrowAsync();

            // Blank is an absence rather than a place, so it matches nothing. h9k project set
            // depends on that: it checks only the one of --home / --repo that the invocation is
            // actually changing, and a value nobody passed must not refuse a change to the other.
            Func<Task> onlyTheRepository = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, DomainId.New(), string.Empty, Path.Combine(home, "repo", "elsewhere.git"), cts.Token);
            await onlyTheRepository.Should().NotThrowAsync();

            Func<Task> onlyTheHome = () => ProjectHomeClaims.EnsureUnclaimedAsync(
                session, DomainId.New(), home, string.Empty, cts.Token);
            await onlyTheHome.Should().ThrowAsync<DomainConflictException>()
                .WithMessage("*My App*already lives*");
        }
    }
}
