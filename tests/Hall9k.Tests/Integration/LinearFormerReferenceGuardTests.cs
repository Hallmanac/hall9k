using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// One Linear issue is one live task even after the issue moves to another team and so answers with a
/// new identifier: a task that recorded the old identifier still holds it, and a second task adopting
/// the issue under its new identifier is refused rather than let through because no task carries that
/// exact string.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LinearFormerReferenceGuardTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(Guid ProjectId, ExternalReference Former, ImportedWorkItem Moved);

    private async Task<Seeded> SeedTaskCarryingTheOldIdentifierAsync(CancellationToken cancellationToken)
    {
        // A number of this test's own: the guard is keyed on the canonical reference and is
        // project-blind, so tests sharing this class's fixture must not name the same issue.
        int number = Random.Shared.Next(1, 1_000_000);
        ExternalReference former = new(WorkItemProvider.Linear, $"ENG-{number}");
        ImportedWorkItem moved = new(
            new ExternalReference(WorkItemProvider.Linear, $"PLAT-{number}"),
            "Add rate limiting",
            Body: null,
            WorkItemStatus.Open,
            Url: null,
            Now)
        {
            FormerReferences = [former],
        };

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"linear-former-{taskId:N}", "/tmp/linear-former-repo",
            new Uri("https://github.com/acme/web"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
        session.Events.StartStream<TaskAggregate>(
            taskId,
            TaskDecider.Add(
                taskId, projectId, "Add rate limiting to auth endpoints", ["429 returned past the limit"],
                TaskType.Feature, null, null, former, Now.AddDays(-1), node.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        return new Seeded(projectId, former, moved);
    }

    [Fact]
    public async Task A_second_task_cannot_adopt_an_issue_under_its_new_identifier()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedTaskCarryingTheOldIdentifierAsync(cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        Func<Task> adopt = () => TaskAddCommand.RefuseSecondAdoptionUnderFormerReferencesAsync(
            session, seeded.Moved, new TaskAddCommand.AdoptionSite(seeded.ProjectId, null), null, null, cts.Token);

        (await adopt.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(seeded.Moved.Reference.ToString()).And.Contain($"used to call {seeded.Former}")
            .And.Contain("one issue is one live task");
    }

    [Fact]
    public async Task The_task_being_relinked_does_not_count_as_holding_the_issue_against_itself()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedTaskCarryingTheOldIdentifierAsync(cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        Func<Task> relink = () => TaskAddCommand.RefuseSecondAdoptionUnderFormerReferencesAsync(
            session, seeded.Moved, new TaskAddCommand.AdoptionSite(seeded.ProjectId, null), null, seeded.Former, cts.Token);

        await relink.Should().NotThrowAsync();
    }
}
