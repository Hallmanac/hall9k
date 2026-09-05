using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="TaskIdResolver"/> and <see cref="IdeaIdResolver"/> shared the exact
/// vacuous-empty-fragment hole a human's cycle-7 ruling had already fixed in
/// <see cref="EpicIdResolver"/>: after stripping dashes, an empty fragment made both
/// StartsWith and EndsWith true for every id, so a blank or dashes-only reference resolved to
/// the install's sole task or idea instead of being refused as invalid input.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskAndIdeaIdResolverEmptyFragmentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_empty_or_dashes_only_fragment_never_vacuously_matches_the_only_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            Func<Task> resolveEmpty = () => TaskIdResolver.ResolveAsync(session, "", cts.Token);
            Func<Task> resolveDashesOnly = () => TaskIdResolver.ResolveAsync(session, "-", cts.Token);

            await resolveEmpty.Should().ThrowAsync<DomainValidationException>()
                .WithMessage("*no characters to match*");
            await resolveDashesOnly.Should().ThrowAsync<DomainValidationException>()
                .WithMessage("*no characters to match*");
        }
    }

    [Fact]
    public async Task An_empty_or_dashes_only_fragment_never_vacuously_matches_the_only_idea()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid ideaId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId,
                "Ideas deserve their own discovery phase. The workspace is where research lands.",
                projectId: null, Now, ProjectHome.None));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            Func<Task> resolveEmpty = () => IdeaIdResolver.ResolveAsync(session, "", cts.Token);
            Func<Task> resolveDashesOnly = () => IdeaIdResolver.ResolveAsync(session, "-", cts.Token);

            await resolveEmpty.Should().ThrowAsync<DomainValidationException>()
                .WithMessage("*no characters to match*");
            await resolveDashesOnly.Should().ThrowAsync<DomainValidationException>()
                .WithMessage("*no characters to match*");
        }
    }
}
