using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What <c>h9k pr resolve</c> can say about which review comments its lap may read when closeout never
/// dispatched one first (task: a changes-requested review or an unresolved review thread from someone
/// outside the roster never reaches a build lap): only that the gate is off, by the last visibility the
/// daemon observed and the project's own setting. Everything else reads as nothing readable.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ManualResolveReadScopeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_repository_last_observed_private_reads_as_every_thread_readable()
    {
        Guid projectId = await ObserveVisibilityAsync(isPrivate: true);
        await using IQuerySession query = postgres.Store.QuerySession();

        ReviewReadScope? scope = await PullRequestResolveCommand.UnrestrictedScopeIfGateOffAsync(
            query, projectId, CancellationToken.None);

        scope.Should().Be(ReviewReadScope.AllThreads);
    }

    [Fact]
    public async Task A_repository_last_observed_public_reads_as_no_scope_at_all()
    {
        Guid projectId = await ObserveVisibilityAsync(isPrivate: false);
        await using IQuerySession query = postgres.Store.QuerySession();

        (await PullRequestResolveCommand.UnrestrictedScopeIfGateOffAsync(query, projectId, CancellationToken.None))
            .Should().BeNull("the prompt reads no recorded scope as nothing readable, never as everything");
    }

    [Fact]
    public async Task A_repository_whose_visibility_was_never_observed_fails_closed()
    {
        await using IQuerySession query = postgres.Store.QuerySession();

        (await PullRequestResolveCommand.UnrestrictedScopeIfGateOffAsync(query, DomainId.New(), CancellationToken.None))
            .Should().BeNull();
    }

    private async Task<Guid> ObserveVisibilityAsync(bool isPrivate)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Store(new ProjectRepositoryVisibility { Id = projectId, IsPrivate = isPrivate, ObservedAt = Now });
        await session.SaveChangesAsync();
        return projectId;
    }
}
