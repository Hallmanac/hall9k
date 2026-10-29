using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The write every solution command ends in, against a real store: one commit puts the membership
/// (and its team companion) on every member project, a refusal on any one of them writes nothing,
/// and an unlink clears the removed project while rewriting the rest.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class SolutionWriterTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string AdminKey = "01JCCCCCCCCCCCCCCCCCCCCCCC";

    private readonly PostgresFixture _postgres;

    public SolutionWriterTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedProjectAsync(string name, string projectKey, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            id,
            new ProjectRegistered(
                id, DomainId.New(), DomainId.New(), name, "/repos/" + name, new Uri($"https://github.com/acme/{name}.git"), "main", Now),
            new ProjectKeyAssigned(id, projectKey, Now));
        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    private async Task<SolutionContext> LoadAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession session = _postgres.Store.QuerySession();
        return await SolutionContext.LoadAsync(session, cancellationToken);
    }

    private async Task WriteAsync(IReadOnlyList<SolutionWrite> writes, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await SolutionWriter.AppendAsync(session, writes, cancellationToken);
    }

    [Fact]
    public async Task One_write_puts_the_membership_and_its_team_companion_on_every_member_and_the_registry_reads_one_solution()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedProjectAsync("shop-web", WebKey, cts.Token);
        SolutionContext before = await LoadAsync(cts.Token);
        ProjectDetails[] projects = [.. before.Projects.OrderBy(project => project.Name)];
        SolutionMembership membership = SolutionDecider.Create(
            SolutionDecider.NewKey(), "storefront", [.. projects.Select(SolutionDecider.MemberOf)]);

        await WriteAsync([.. projects.Select(project => new SolutionWrite(project, membership))], cts.Token);

        SolutionContext after = await LoadAsync(cts.Token);
        after.Projects.Should().AllSatisfy(project => project.Solution.Should().BeEquivalentTo(membership));
        SolutionView view = after.Solutions.Should().ContainSingle().Subject;
        view.HeldProjects.Select(project => project.Id).Should().BeEquivalentTo([api, web]);

        await using IQuerySession session = _postgres.Store.QuerySession();
        foreach (Guid id in new[] { api, web })
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(id, token: cts.Token);
            events.Select(@event => @event.EventType).Should().Contain(typeof(ProjectSettingsChanged))
                .And.Contain(typeof(ProjectTeamSettingsChanged), "the companion is what replicates, with no path of its own");
        }
    }

    [Fact]
    public async Task A_refusal_on_any_member_writes_nothing_to_the_others()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedProjectAsync("shop-api", ApiKey, cts.Token);
        await SeedProjectAsync("shop-web", WebKey, cts.Token);
        await SeedProjectAsync("shop-admin", AdminKey, cts.Token);
        SolutionContext context = await LoadAsync(cts.Token);
        ProjectDetails api = context.Projects.Single(project => project.Name == "shop-api");
        ProjectDetails web = context.Projects.Single(project => project.Name == "shop-web");
        ProjectDetails admin = context.Projects.Single(project => project.Name == "shop-admin");

        SolutionMembership storefront = SolutionDecider.Create(
            SolutionDecider.NewKey(), "storefront", [SolutionDecider.MemberOf(api), SolutionDecider.MemberOf(web)]);
        await WriteAsync([new SolutionWrite(api, storefront), new SolutionWrite(web, storefront)], cts.Token);

        // A second open solution over the free project and the one already held: the held one refuses.
        SolutionMembership marketplace = SolutionDecider.Create(
            SolutionDecider.NewKey(), "marketplace", [SolutionDecider.MemberOf(admin), SolutionDecider.MemberOf(web)]);
        Func<Task> act = () => WriteAsync([new SolutionWrite(admin, marketplace), new SolutionWrite(web, marketplace)], cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*already in the open solution 'storefront'*h9k solution unlink*");
        SolutionContext after = await LoadAsync(cts.Token);
        after.Projects.Single(project => project.Name == "shop-admin").Solution.Should().BeNull("one commit, so the free project was not half written");
        after.Projects.Single(project => project.Name == "shop-web").Solution!.Name.Should().Be("storefront");
    }

    [Fact]
    public async Task An_unlink_clears_the_removed_project_and_rewrites_the_rest_so_it_can_join_another_solution()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedProjectAsync("shop-api", ApiKey, cts.Token);
        await SeedProjectAsync("shop-web", WebKey, cts.Token);
        SolutionContext context = await LoadAsync(cts.Token);
        ProjectDetails api = context.Projects.Single(project => project.Name == "shop-api");
        ProjectDetails web = context.Projects.Single(project => project.Name == "shop-web");
        SolutionMembership storefront = SolutionDecider.Create(
            SolutionDecider.NewKey(), "storefront", [SolutionDecider.MemberOf(api), SolutionDecider.MemberOf(web)]);
        await WriteAsync([new SolutionWrite(api, storefront), new SolutionWrite(web, storefront)], cts.Token);

        SolutionMembership remaining = SolutionDecider.Unlink(storefront, WebKey);
        await WriteAsync(
            [new SolutionWrite(api, remaining), new SolutionWrite(web, null)], cts.Token);

        SolutionContext after = await LoadAsync(cts.Token);
        after.Projects.Single(project => project.Name == "shop-web").Solution.Should().BeNull();
        after.Projects.Single(project => project.Name == "shop-api").Solution!.Members.Select(member => member.Name).Should().Equal("shop-api");

        SolutionMembership marketplace = SolutionDecider.Create(
            SolutionDecider.NewKey(), "marketplace", [SolutionDecider.MemberOf(web)]);
        Func<Task> join = () => WriteAsync([new SolutionWrite(web, marketplace)], cts.Token);
        await join.Should().NotThrowAsync("unlink is what frees a project for another open solution");
    }
}
