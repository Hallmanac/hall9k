using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
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
}
