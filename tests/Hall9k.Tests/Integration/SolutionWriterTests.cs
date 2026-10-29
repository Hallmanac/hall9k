using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The write every solution command ends in, against a real store: one commit puts the membership
/// (and its team companion) on every member project, a refusal on any one of them writes nothing,
/// and an unlink clears the removed project while rewriting the rest. The solution epics that ride on
/// a membership are here too, since each is only a few ordinary epic streams read back through the
/// members a store holds: their copies, a task joining one, the shared-issue admission, the closeout
/// guard, and what an unlink leaves behind.
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
        context = await LoadAsync(cts.Token);
        admin = context.Projects.Single(project => project.Name == "shop-admin");
        web = context.Projects.Single(project => project.Name == "shop-web");
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

        context = await LoadAsync(cts.Token);
        api = context.Projects.Single(project => project.Name == "shop-api");
        web = context.Projects.Single(project => project.Name == "shop-web");
        SolutionMembership remaining = SolutionDecider.Unlink(storefront, WebKey);
        await WriteAsync(
            [new SolutionWrite(api, remaining), new SolutionWrite(web, null)], cts.Token);

        SolutionContext after = await LoadAsync(cts.Token);
        after.Projects.Single(project => project.Name == "shop-web").Solution.Should().BeNull();
        after.Projects.Single(project => project.Name == "shop-api").Solution!.Members.Select(member => member.Name).Should().Equal("shop-api");

        web = (await LoadAsync(cts.Token)).Projects.Single(project => project.Name == "shop-web");
        SolutionMembership marketplace = SolutionDecider.Create(
            SolutionDecider.NewKey(), "marketplace", [SolutionDecider.MemberOf(web)]);
        Func<Task> join = () => WriteAsync([new SolutionWrite(web, marketplace)], cts.Token);
        await join.Should().NotThrowAsync("unlink is what frees a project for another open solution");
    }

    [Fact]
    public async Task A_write_decided_from_a_stale_load_is_refused_and_writes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedProjectAsync("shop-api", ApiKey, cts.Token);
        await SeedProjectAsync("shop-web", WebKey, cts.Token);
        SolutionContext stale = await LoadAsync(cts.Token);
        ProjectDetails api = stale.Projects.Single(project => project.Name == "shop-api");
        ProjectDetails web = stale.Projects.Single(project => project.Name == "shop-web");
        SolutionMembership storefront = SolutionDecider.Create(
            SolutionDecider.NewKey(), "storefront", [SolutionDecider.MemberOf(api), SolutionDecider.MemberOf(web)]);

        // Another writer (a replicated change, a second command) puts the solution on shop-web after this load.
        await WriteAsync([new SolutionWrite(web, storefront)], cts.Token);

        Func<Task> act = () => WriteAsync([new SolutionWrite(api, storefront), new SolutionWrite(web, storefront)], cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*changed while the solution was being rewritten*");
        SolutionContext after = await LoadAsync(cts.Token);
        after.Projects.Single(project => project.Name == "shop-api").Solution.Should().BeNull();
    }

    private const string Root = "SHA256:root";

    private Guid _ownerId;

    /// <summary>A project this node has joined: its owner claims <see cref="Root"/> and the project vouches that root as a member.</summary>
    private async Task<Guid> SeedJoinedProjectAsync(string name, string projectKey, CancellationToken cancellationToken, bool joined = true)
    {
        await using (IDocumentSession owner = _postgres.Store.LightweightSession())
        {
            if (_ownerId == Guid.Empty || await owner.LoadAsync<OwnerDetails>(_ownerId, cancellationToken) is null)
            {
                _ownerId = DomainId.New();
                owner.Events.StartStream<OwnerAggregate>(
                    _ownerId, new OwnerRegistered(_ownerId, "owner", null, Now), new OwnerRootClaimed(_ownerId, Root, true, Now));
                await owner.SaveChangesAsync(cancellationToken);
            }
        }

        Guid id = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        List<object> events =
        [
            new ProjectRegistered(
                id, _ownerId, DomainId.New(), name, "/repos/" + name, new Uri($"https://github.com/acme/{name}.git"), "main", Now),
            new ProjectKeyAssigned(id, projectKey, Now),
        ];
        if (joined)
        {
            events.Add(new MemberVouched(id, Root, ProjectMemberRole.Owner, Now));
        }

        session.Events.StartStream<ProjectAggregate>(id, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    /// <summary>The solution over every named project, written the way <c>h9k solution add</c> writes it.</summary>
    private async Task<SolutionMembership> GroupAsync(string name, CancellationToken cancellationToken, params string[] projectNames)
    {
        SolutionContext context = await LoadAsync(cancellationToken);
        ProjectDetails[] projects = [.. projectNames.Select(projectName => context.Projects.Single(project => project.Name == projectName))];
        SolutionMembership membership = SolutionDecider.Create(
            SolutionDecider.NewKey(), name, [.. projects.Select(SolutionDecider.MemberOf)]);
        await WriteAsync([.. projects.Select(project => new SolutionWrite(project, membership))], cancellationToken);
        return membership;
    }

    private async Task<ProjectDetails> ProjectAsync(string name, CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken)).Projects.Single(project => project.Name == name);

    /// <summary>An epic added in <paramref name="project"/> the way <c>h9k epic add</c> adds it, saved.</summary>
    private async Task<IReadOnlyList<EpicAdded>> AddEpicAsync(string project, string title, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectDetails details = (await SolutionContext.LoadAsync(session, cancellationToken)).Projects.Single(p => p.Name == project);
        SolutionEpicAddition? addition = await SolutionEpicAddition.PlanAsync(session, details, title, _ownerId, cancellationToken);
        IReadOnlyList<EpicAdded> copies = addition?.Copies
            ?? [EpicDecider.Add(DomainId.New(), details.Id, title, Now, _ownerId)];
        foreach (EpicAdded copy in copies)
        {
            session.Events.StartStream<EpicAggregate>(copy.Id, copy);
        }

        await session.SaveChangesAsync(cancellationToken);
        return copies;
    }

    private async Task<Guid> AddTaskAsync(
        string project, Guid? epicId, ExternalReference? reference, CancellationToken cancellationToken)
    {
        Guid projectId = (await ProjectAsync(project, cancellationToken)).Id;
        Guid taskId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(
            taskId,
            TaskDecider.Add(
                taskId, projectId, $"Work in {project}", ["it works"], TaskType.Feature, null, null, reference, Now, _ownerId,
                epicId: epicId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task AbandonAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.Abandon(task, "done elsewhere", Now.AddHours(1), _ownerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static Guid CopyIn(IReadOnlyList<EpicAdded> copies, Guid projectId) => copies.Single(copy => copy.ProjectId == projectId).Id;

    private static ExternalReference Issue() => new(WorkItemProvider.GitHub, $"acme/shop-{Guid.NewGuid():N}#7");

    [Fact]
    public async Task An_epic_added_in_a_member_project_is_one_copy_in_every_member_this_node_holds_and_names_the_member_it_does_not()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await SeedJoinedProjectAsync("shop-admin", AdminKey, cts.Token, joined: false);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web", "shop-admin");

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            ProjectDetails details = (await SolutionContext.LoadAsync(session, cts.Token)).Projects.Single(project => project.Name == "shop-api");
            SolutionEpicAddition addition = (await SolutionEpicAddition.PlanAsync(session, details, "Cart", _ownerId, cts.Token))!;

            addition.NotHeld.Select(member => member.Name).Should().Equal("shop-admin");
            addition.Copies.Select(copy => copy.ProjectId).Should().BeEquivalentTo([api, web]);
        }

        IReadOnlyList<EpicAdded> copies = await AddEpicAsync("shop-api", "Cart", cts.Token);

        await using IQuerySession read = _postgres.Store.QuerySession();
        IReadOnlyList<EpicDetails> epics = await read.Query<EpicDetails>().ToListAsync(cts.Token);
        epics.Should().HaveCount(2, "the project this node has not joined gets no copy from it");
        epics.Select(epic => epic.SolutionEpicKey).Distinct().Should().ContainSingle().Which.Should().Be(copies[0].SolutionEpicKey);
        epics.Select(epic => epic.Title).Should().OnlyContain(title => title == "Cart");
        foreach (EpicAdded copy in copies)
        {
            IReadOnlyList<IEvent> stream = await read.Events.FetchStreamAsync(copy.Id, token: cts.Token);
            stream.Select(@event => @event.Data).Should().ContainSingle().Which.Should().BeOfType<EpicAdded>(
                "each copy is an ordinary epic stream, which replicates through its own project's ledger as an epic does today");
        }
    }

    [Fact]
    public async Task A_project_outside_every_solution_adds_an_ordinary_project_epic_and_a_closed_solution_holds_nobody()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        SolutionMembership storefront = await GroupAsync("storefront", cts.Token, "shop-api");
        ProjectDetails web = await ProjectAsync("shop-web", cts.Token);

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            (await SolutionEpicAddition.PlanAsync(session, web, "Cart", _ownerId, cts.Token)).Should().BeNull();
        }

        IReadOnlyList<EpicAdded> copies = await AddEpicAsync("shop-web", "Alone", cts.Token);

        copies.Should().ContainSingle().Which.SolutionEpicKey.Should().BeNull();
        ProjectDetails api = await ProjectAsync("shop-api", cts.Token);
        await WriteAsync([new SolutionWrite(api, SolutionDecider.Close(storefront, "retired"))], cts.Token);
        await using IQuerySession closed = _postgres.Store.QuerySession();
        (await SolutionEpicAddition.PlanAsync(closed, await ProjectAsync("shop-api", cts.Token), "After", _ownerId, cts.Token))
            .Should().BeNull("a closed solution no longer holds its members, so a plain project epic is allowed again");
    }

    [Fact]
    public async Task A_project_linked_later_gets_a_copy_of_every_open_solution_epic_and_the_epics_it_already_has_are_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid admin = await SeedJoinedProjectAsync("shop-admin", AdminKey, cts.Token);
        SolutionMembership storefront = await GroupAsync("storefront", cts.Token, "shop-api");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        IReadOnlyList<EpicAdded> retired = await AddEpicAsync("shop-api", "Retired", cts.Token);
        await AddEpicAsync("shop-admin", "Admin only", cts.Token);
        await using (IDocumentSession close = _postgres.Store.LightweightSession())
        {
            EpicAggregate epic = (await close.Events.AggregateStreamAsync<EpicAggregate>(retired[0].Id, token: cts.Token))!;
            close.Events.Append(epic.Id, EpicDecider.Close(epic, "no longer needed", Now.AddHours(1), _ownerId));
            await close.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            ProjectDetails details = (await SolutionContext.LoadAsync(session, cts.Token)).Projects.Single(project => project.Name == "shop-admin");
            (await SolutionEpicCopies.StageAsync(session, storefront.Key, [details], cts.Token)).Should().Be(1);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession read = _postgres.Store.QuerySession();
        EpicDetails[] inAdmin = [.. (await read.Query<EpicDetails>().ToListAsync(cts.Token)).Where(epic => epic.ProjectId == admin)];
        inAdmin.Select(epic => epic.Title).Should().BeEquivalentTo("Admin only", "Cart");
        inAdmin.Single(epic => epic.Title == "Cart").SolutionEpicKey.Should().Be(cart[0].SolutionEpicKey);

        await using IDocumentSession again = _postgres.Store.LightweightSession();
        ProjectDetails admin2 = (await SolutionContext.LoadAsync(again, cts.Token)).Projects.Single(project => project.Name == "shop-admin");
        (await SolutionEpicCopies.StageAsync(again, storefront.Key, [admin2], cts.Token)).Should().Be(0, "the copy is already there");
    }

    [Fact]
    public async Task A_task_joins_a_solution_epic_by_its_key_or_any_copys_id_in_its_own_projects_copy_and_only_from_a_member_project_while_it_is_open()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        Guid outsider = await SeedJoinedProjectAsync("shop-other", AdminKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        string key = cart[0].SolutionEpicKey!;

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            (await EpicIdResolver.ResolveForMembershipAsync(session, key, web, cts.Token)).Should().Be(CopyIn(cart, web));
            (await EpicIdResolver.ResolveForMembershipAsync(session, key[^8..], api, cts.Token)).Should().Be(CopyIn(cart, api));
            (await EpicIdResolver.ResolveForMembershipAsync(session, CopyIn(cart, api).ToString(), web, cts.Token))
                .Should().Be(CopyIn(cart, web), "any copy's id names the solution epic, and the task joins its own project's copy");
            (await EpicIdResolver.ResolveIdsAsync(session, key, cts.Token)).Should().BeEquivalentTo(cart.Select(copy => copy.Id));

            Func<Task> outside = () => EpicIdResolver.ResolveForMembershipAsync(session, key, outsider, cts.Token);
            await outside.Should().ThrowAsync<DomainConflictException>().WithMessage("*solution epic of 'storefront'*not*");
        }

        await using (IDocumentSession close = _postgres.Store.LightweightSession())
        {
            foreach (EpicClosed closed in EpicCloseCommand.Close(
                         await EpicIdResolver.LoadAsync(close, key, cts.Token), "shipped", Now.AddHours(1), _ownerId))
            {
                close.Events.Append(closed.Id, closed);
            }

            await close.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession after = _postgres.Store.QuerySession();
        Func<Task> closedJoin = () => EpicIdResolver.ResolveForMembershipAsync(after, key, web, cts.Token);
        await closedJoin.Should().ThrowAsync<DomainConflictException>().WithMessage("*Open is the only state a task can join*");
        (await after.Query<EpicDetails>().ToListAsync(cts.Token)).Should().OnlyContain(
            epic => epic.State == EpicState.Closed, "closing a solution epic closes every copy this node holds");
    }

    [Fact]
    public async Task A_second_task_holds_an_issue_only_in_the_same_solution_epic_in_a_different_member_project()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        IReadOnlyList<EpicAdded> checkout = await AddEpicAsync("shop-api", "Checkout", cts.Token);
        ExternalReference issue = Issue();
        Guid holder = await AddTaskAsync("shop-api", CopyIn(cart, api), issue, cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        Task Refuse(Guid project, Guid? epic) => TaskAddCommand.RefuseSecondAdoptionAsync(
            session, issue, new TaskAddCommand.AdoptionSite(project, epic), ownerRootFingerprint: null, cts.Token);

        await Refuse(web, CopyIn(cart, web));

        Func<Task> noEpic = () => Refuse(web, null);
        await noEpic.Should().ThrowAsync<DomainConflictException>().WithMessage($"*is already adopted by task {TaskListCommand.ShortId(holder)}*one issue is one live task*");
        Func<Task> otherEpic = () => Refuse(web, CopyIn(checkout, web));
        await otherEpic.Should().ThrowAsync<DomainConflictException>()
            .Where(exception => exception.Message.Contains("one issue is one live task") && exception.Message.Contains("join it to the epic first"),
                "the holder is a member project's task outside the new task's epic, so it is told to join the epic first");
        Func<Task> sameProject = () => Refuse(api, CopyIn(cart, api));
        await sameProject.Should().ThrowAsync<DomainConflictException>().WithMessage("*one issue is one live task*");
    }

    [Fact]
    public async Task A_holder_outside_the_epic_is_named_with_the_remedy_of_joining_it_to_the_epic_first_and_a_plain_refusal_stays_today_s_message()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        ExternalReference issue = Issue();
        Guid holder = await AddTaskAsync("shop-api", null, issue, cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        Func<Task> outside = () => TaskAddCommand.RefuseSecondAdoptionAsync(
            session, issue, new TaskAddCommand.AdoptionSite(web, CopyIn(cart, web)), null, cts.Token);
        Func<Task> plain = () => TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        string shortId = TaskListCommand.ShortId(holder);
        await outside.Should().ThrowAsync<DomainConflictException>()
            .WithMessage($"*{shortId}*outside the solution epic*join it to the epic first with h9k task revise {shortId} --epic {cart[0].SolutionEpicKey}*");
        (await plain.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().NotContain("join it to the epic first", "without a solution epic the refusal is exactly today's");
    }

    [Fact]
    public async Task Linking_a_task_reads_its_own_epic_so_a_task_in_the_same_solution_epic_may_share_the_issue()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        ExternalReference issue = Issue();
        await AddTaskAsync("shop-api", CopyIn(cart, api), issue, cts.Token);
        Guid inEpic = await AddTaskAsync("shop-web", CopyIn(cart, web), null, cts.Token);
        Guid notInEpic = await AddTaskAsync("shop-web", null, null, cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        async Task Link(Guid taskId)
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            await TaskAddCommand.RefuseSecondAdoptionAsync(
                session, issue, new TaskAddCommand.AdoptionSite(task.ProjectId, task.EpicId), null, cts.Token);
        }

        await Link(inEpic);
        Func<Task> refused = () => Link(notInEpic);
        await refused.Should().ThrowAsync<DomainConflictException>().WithMessage("*one issue is one live task*");
    }

    [Fact]
    public async Task Closeout_holds_a_shared_issue_open_until_this_node_holds_every_member_and_every_other_task_is_done_or_abandoned()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await SeedJoinedProjectAsync("shop-admin", AdminKey, cts.Token, joined: false);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web", "shop-admin");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        ExternalReference issue = Issue();
        Guid closing = await AddTaskAsync("shop-api", CopyIn(cart, api), issue, cts.Token);
        Guid other = await AddTaskAsync("shop-web", CopyIn(cart, web), issue, cts.Token);
        await AbandonAsync(other, cts.Token);

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            (await CloseoutEngine.SharedIssueHoldAsync(session, closing, issue, cts.Token))
                .Should().Contain("does not hold 'shop-admin'", "a member this node has not joined could hold a task it cannot see");
        }

        SolutionContext context = await LoadAsync(cts.Token);
        ProjectDetails admin = context.Projects.Single(project => project.Name == "shop-admin");
        await using (IDocumentSession vouch = _postgres.Store.LightweightSession())
        {
            vouch.Events.Append(admin.Id, new MemberVouched(admin.Id, Root, ProjectMemberRole.Owner, Now.AddHours(1)));
            await vouch.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession joined = _postgres.Store.QuerySession();
        (await CloseoutEngine.SharedIssueHoldAsync(joined, closing, issue, cts.Token))
            .Should().BeNull("every member is held and the other task is abandoned, so the ordinary close rule decides");

        Guid pending = await AddTaskAsync("shop-web", CopyIn(cart, web), issue, cts.Token);
        await using IQuerySession withPending = _postgres.Store.QuerySession();
        (await CloseoutEngine.SharedIssueHoldAsync(withPending, closing, issue, cts.Token))
            .Should().Contain("not yet done or abandoned");
        pending.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Closeout_has_nothing_to_say_about_an_issue_only_one_projects_task_holds_when_this_node_holds_every_member()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        ExternalReference issue = Issue();
        Guid closing = await AddTaskAsync("shop-api", CopyIn(cart, api), issue, cts.Token);
        Guid plain = await AddTaskAsync("shop-api", null, Issue(), cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        (await CloseoutEngine.SharedIssueHoldAsync(session, closing, issue, cts.Token)).Should().BeNull();
        (await CloseoutEngine.SharedIssueHoldAsync(session, plain, Issue(), cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task Closeout_holds_an_issue_open_while_a_member_is_not_held_even_when_no_other_task_is_visible()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token, joined: false);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        ExternalReference issue = Issue();
        Guid closing = await AddTaskAsync("shop-api", CopyIn(cart, api), issue, cts.Token);
        Guid plain = await AddTaskAsync("shop-api", null, Issue(), cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        (await CloseoutEngine.SharedIssueHoldAsync(session, closing, issue, cts.Token))
            .Should().Contain("does not hold 'shop-web'", "a task in the project this node has not joined could hold the issue unseen");
        (await CloseoutEngine.SharedIssueHoldAsync(session, plain, Issue(), cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task Revising_a_task_out_of_its_solution_epic_is_refused_while_another_projects_task_shares_its_issue()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        IReadOnlyList<EpicAdded> checkout = await AddEpicAsync("shop-api", "Checkout", cts.Token);
        ExternalReference issue = Issue();
        await AddTaskAsync("shop-api", CopyIn(cart, api), issue, cts.Token);
        Guid sharing = await AddTaskAsync("shop-web", CopyIn(cart, web), issue, cts.Token);
        Guid alone = await AddTaskAsync("shop-web", CopyIn(cart, web), Issue(), cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        async Task Move(Guid taskId, Guid? toEpic) => await TaskReviseCommand.RefuseLeavingSharedIssueAsync(
            session, (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!, toEpic, cts.Token);

        Func<Task> clear = () => Move(sharing, null);
        await clear.Should().ThrowAsync<DomainConflictException>().WithMessage("*one issue is one live task*cannot leave*");
        Func<Task> other = () => Move(sharing, CopyIn(checkout, web));
        await other.Should().ThrowAsync<DomainConflictException>().WithMessage("*cannot leave*");
        await Move(alone, null);
    }

    [Fact]
    public async Task Unlinking_a_project_leaves_its_copies_and_tasks_on_the_solution_epic_and_no_later_epic_reaches_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid api = await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        SolutionMembership storefront = await GroupAsync("storefront", cts.Token, "shop-api", "shop-web");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        Guid webTask = await AddTaskAsync("shop-web", CopyIn(cart, web), null, cts.Token);

        SolutionContext context = await LoadAsync(cts.Token);
        ProjectDetails apiDetails = context.Projects.Single(project => project.Name == "shop-api");
        ProjectDetails webDetails = context.Projects.Single(project => project.Name == "shop-web");
        await WriteAsync(
            [new SolutionWrite(apiDetails, SolutionDecider.Unlink(storefront, WebKey)), new SolutionWrite(webDetails, null)], cts.Token);

        IReadOnlyList<EpicAdded> later = await AddEpicAsync("shop-api", "Checkout", cts.Token);
        IReadOnlyList<EpicAdded> projectEpic = await AddEpicAsync("shop-web", "Web only", cts.Token);

        later.Select(copy => copy.ProjectId).Should().Equal([api], "a later solution epic no longer reaches the unlinked project");
        projectEpic.Should().ContainSingle().Which.SolutionEpicKey.Should().BeNull("the project may create project epics again");

        await using IQuerySession read = _postgres.Store.QuerySession();
        SolutionContext after = await SolutionContext.LoadAsync(read, cts.Token);
        EpicGroup group = EpicGroup.Of(await read.Query<EpicDetails>().ToListAsync(cts.Token), after.Projects)
            .Single(candidate => candidate.SolutionEpicKey == cart[0].SolutionEpicKey);
        group.Copies.Select(copy => copy.ProjectId).Should().BeEquivalentTo([api, web], "the unlinked project's copy stays on the solution epic");
        (await read.LoadAsync<TaskListItem>(webTask, cts.Token))!.EpicId.Should().Be(CopyIn(cart, web), "its task stays where it was");
        after.Projects.Single(project => project.Name == "shop-web").Solution.Should().BeNull();

        Func<Task> join = () => EpicIdResolver.ResolveForMembershipAsync(read, cart[0].SolutionEpicKey!, web, cts.Token);
        await join.Should().ThrowAsync<DomainConflictException>().WithMessage("*not*", "an unlinked project's new tasks no longer join the solution epic");

        await using IDocumentSession close = _postgres.Store.LightweightSession();
        (await EpicIdResolver.LoadAsync(close, cart[0].SolutionEpicKey!, cts.Token)).Select(copy => copy.Id)
            .Should().Equal([CopyIn(cart, api)], "a later close or Jira link reaches only the projects still in the solution");
    }

    [Fact]
    public async Task A_member_with_no_copy_on_this_node_is_refused_with_a_message_that_says_where_copies_come_from()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        await GroupAsync("storefront", cts.Token, "shop-api");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);
        SolutionContext context = await LoadAsync(cts.Token);
        SolutionMembership both = SolutionDecider.Create(
            context.Solutions.Single().Membership.Key, "storefront",
            [.. context.Projects.OrderBy(project => project.Name).Select(SolutionDecider.MemberOf)]);
        ProjectDetails api = context.Projects.Single(project => project.Name == "shop-api");
        ProjectDetails webDetails = context.Projects.Single(project => project.Name == "shop-web");
        await WriteAsync([new SolutionWrite(api, both), new SolutionWrite(webDetails, both)], cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        Func<Task> join = () => EpicIdResolver.ResolveForMembershipAsync(session, cart[0].SolutionEpicKey!, web, cts.Token);

        await join.Should().ThrowAsync<DomainConflictException>().WithMessage("*holds no copy of the solution epic 'Cart'*");
    }

    [Fact]
    public async Task Linking_a_project_writes_the_membership_and_its_missing_epic_copies_in_one_commit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedJoinedProjectAsync("shop-api", ApiKey, cts.Token);
        Guid web = await SeedJoinedProjectAsync("shop-web", WebKey, cts.Token);
        SolutionMembership storefront = await GroupAsync("storefront", cts.Token, "shop-api");
        IReadOnlyList<EpicAdded> cart = await AddEpicAsync("shop-api", "Cart", cts.Token);

        // The body of h9k solution link: stage the copies, then the membership write saves both together.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            SolutionContext context = await SolutionContext.LoadAsync(session, cts.Token);
            ProjectDetails webDetails = context.Projects.Single(project => project.Name == "shop-web");
            SolutionView view = SolutionRegistry.Match(context.Solutions, "storefront");
            SolutionMembership linked = SolutionDecider.Link(view.Membership, SolutionDecider.MemberOf(webDetails));
            SolutionDecider.RequireConsistent(view, context.Projects, context.RootFingerprintOf, "link a project into", repairing: [webDetails]);

            (await SolutionEpicCopies.StageAsync(session, linked.Key, [webDetails], cts.Token)).Should().Be(1);
            await SolutionWriter.AppendAsync(session, SolutionWriter.Rewrite(linked, context, removed: []), cts.Token);
        }

        await using IQuerySession read = _postgres.Store.QuerySession();
        SolutionContext after = await SolutionContext.LoadAsync(read, cts.Token);
        after.Projects.Single(project => project.Name == "shop-web").Solution!.Key.Should().Be(storefront.Key);
        EpicDetails copy = (await read.Query<EpicDetails>().ToListAsync(cts.Token)).Single(epic => epic.ProjectId == web);
        copy.SolutionEpicKey.Should().Be(cart[0].SolutionEpicKey);
        (await EpicIdResolver.ResolveForMembershipAsync(read, cart[0].SolutionEpicKey!, web, cts.Token)).Should().Be(copy.Id);
    }
}
