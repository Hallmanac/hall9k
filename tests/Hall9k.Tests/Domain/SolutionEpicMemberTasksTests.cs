using FluentAssertions;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Which tasks of a solution epic's other member projects a plan's refinement session is told of. Everything here is pure:
/// projects, epic copies, and task rows are built in memory, and nothing opens a store or an agent.
/// </summary>
public sealed class SolutionEpicMemberTasksTests
{
    private const string Root = "root-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly SolutionMember Api = new("01JAAAAAAAAAAAAAAAAAAAAAAA", "shop-api", new Uri("https://github.com/acme/shop-api.git"));
    private static readonly SolutionMember Web = new("01JBBBBBBBBBBBBBBBBBBBBBBB", "shop-web", new Uri("https://github.com/acme/shop-web.git"));
    private static readonly SolutionMember Ops = new("01JCCCCCCCCCCCCCCCCCCCCCCC", "shop-ops", new Uri("https://github.com/acme/shop-ops.git"));

    private static readonly SolutionMembership Storefront = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web, Ops]);

    private readonly string _epicKey = SolutionEpicDecider.NewKey();
    private readonly Guid _owner = DomainId.New();

    private ProjectDetails Project(SolutionMember member, SolutionMembership? solution = null, bool joined = true) => new()
    {
        Id = DomainId.New(),
        OwnerId = _owner,
        Name = member.Name,
        ProjectKey = member.ProjectKey,
        Solution = solution ?? Storefront,
        Members = joined ? new() { [Root] = ProjectMemberRole.Owner } : [],
    };

    private EpicDetails Copy(ProjectDetails project, string? epicKey = null, string? solutionKey = null) =>
        new EpicDetailsProjection().Create(new FakeEvent<EpicAdded>(EpicDecider.Add(
            DomainId.New(), project.Id, "Cart", Now, _owner, epicKey ?? _epicKey, solutionKey ?? Storefront.Key)));

    private static EpicDetails ProjectEpic(ProjectDetails project) =>
        new EpicDetailsProjection().Create(new FakeEvent<EpicAdded>(EpicDecider.Add(DomainId.New(), project.Id, "Cart", Now, DomainId.New())));

    private static TaskListItem Task(ProjectDetails project, EpicDetails? epic, string objective = "Add the cart", TaskState? state = null) => new()
    {
        Id = DomainId.New(),
        ProjectId = project.Id,
        EpicId = epic?.Id,
        State = state ?? TaskState.Published,
        Objective = objective,
        AddedAt = Now,
    };

    private SolutionContext Context(params ProjectDetails[] projects) =>
        new(projects, SolutionRegistry.Read(projects), new Dictionary<Guid, string?> { [_owner] = Root });

    [Fact]
    public void It_lists_the_tasks_under_the_epics_copies_in_the_other_member_projects_by_id_project_objective_and_state()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        ProjectDetails ops = Project(Ops);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);
        EpicDetails opsCopy = Copy(ops);
        TaskListItem webTask = Task(web, webCopy, "Render the cart", TaskState.Queued);
        TaskListItem opsTask = Task(ops, opsCopy, "Provision the cart cache", TaskState.Draft);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy, opsCopy], [Task(api, own, "Own work"), opsTask, webTask], Context(api, web, ops));

        chosen.Should().Equal(
            new SolutionMemberTask(opsTask.Id, "shop-ops", "Provision the cart cache", TaskState.Draft),
            new SolutionMemberTask(webTask.Id, "shop-web", "Render the cart", TaskState.Queued));
    }

    [Fact]
    public void It_leaves_the_drafts_own_project_out_even_when_it_holds_tasks_under_the_epic()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy], [Task(api, own), Task(web, webCopy)], Context(api, web));

        chosen.Should().ContainSingle().Which.ProjectName.Should().Be("shop-web");
    }

    [Fact]
    public void It_reads_only_the_drafts_own_solution_epic()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);
        EpicDetails otherSolutionEpic = Copy(web, epicKey: SolutionEpicDecider.NewKey());
        EpicDetails webProjectEpic = ProjectEpic(web);
        TaskListItem wanted = Task(web, webCopy, "Render the cart");

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own,
            [own, webCopy, otherSolutionEpic, webProjectEpic],
            [wanted, Task(web, otherSolutionEpic, "Another epic"), Task(web, webProjectEpic, "A project epic"), Task(web, null, "No epic")],
            Context(api, web));

        chosen.Should().ContainSingle().Which.Id.Should().Be(wanted.Id);
    }

    [Fact]
    public void It_leaves_out_a_member_that_is_registered_but_not_joined_on_this_node()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web, joined: false);
        ProjectDetails ops = Project(Ops);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);
        EpicDetails opsCopy = Copy(ops);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy, opsCopy], [Task(web, webCopy), Task(ops, opsCopy)], Context(api, web, ops));

        chosen.Should().ContainSingle().Which.ProjectName.Should().Be("shop-ops");
    }

    [Fact]
    public void It_leaves_out_a_member_that_is_not_registered_on_this_node_at_all()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);
        ProjectDetails unregistered = Project(Ops);
        EpicDetails opsCopy = Copy(unregistered);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy, opsCopy], [Task(web, webCopy), Task(unregistered, opsCopy)], Context(api, web));

        chosen.Should().ContainSingle().Which.ProjectName.Should().Be("shop-web");
    }

    [Fact]
    public void It_leaves_out_a_copy_held_by_a_project_that_has_left_the_solution()
    {
        SolutionMembership withoutOps = SolutionDecider.Create(Storefront.Key, "storefront", [Api, Web]);
        ProjectDetails api = Project(Api, withoutOps);
        ProjectDetails web = Project(Web, withoutOps);
        ProjectDetails ops = Project(Ops);
        ops.Solution = null;
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);
        EpicDetails opsCopy = Copy(ops);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy, opsCopy], [Task(web, webCopy), Task(ops, opsCopy)], Context(api, web, ops));

        chosen.Should().ContainSingle().Which.ProjectName.Should().Be("shop-web");
    }

    [Fact]
    public void It_leaves_out_a_copy_held_by_a_project_that_carries_the_solution_but_is_no_longer_listed_by_it()
    {
        SolutionMembership withoutOps = SolutionDecider.Create(Storefront.Key, "storefront", [Api, Web]);
        ProjectDetails api = Project(Api, withoutOps);
        ProjectDetails web = Project(Web, withoutOps);
        ProjectDetails ops = Project(Ops, withoutOps);
        EpicDetails own = Copy(api);
        EpicDetails opsCopy = Copy(ops);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, opsCopy], [Task(ops, opsCopy)], Context(api, web, ops));

        chosen.Should().BeEmpty();
    }

    [Fact]
    public void It_carries_the_state_of_a_done_task_and_an_abandoned_one_so_the_prompt_can_mark_them()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy], [Task(web, webCopy, state: TaskState.Done), Task(web, webCopy, state: TaskState.Abandoned)], Context(api, web));

        chosen.Should().NotBeNull();
        chosen!.Select(task => task.State).Should().BeEquivalentTo([TaskState.Done, TaskState.Abandoned]);
    }

    [Fact]
    public void A_task_in_the_list_carries_nothing_but_its_id_project_objective_and_state()
    {
        typeof(SolutionMemberTask).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(nameof(SolutionMemberTask.Id), nameof(SolutionMemberTask.ProjectName), nameof(SolutionMemberTask.Objective), nameof(SolutionMemberTask.State));
    }

    [Fact]
    public void A_task_claiming_a_copy_that_belongs_to_another_project_is_not_listed()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        ProjectDetails ops = Project(Ops);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, webCopy], [Task(ops, webCopy)], Context(api, web, ops));

        chosen.Should().BeEmpty();
    }

    [Fact]
    public void A_draft_in_a_project_epic_gets_no_list()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = ProjectEpic(api);
        EpicDetails webCopy = Copy(web);

        SolutionEpicMemberTasks.Choose(own, [own, webCopy], [Task(web, webCopy)], Context(api, web)).Should().BeNull();
    }

    [Fact]
    public void A_draft_whose_own_project_has_left_the_solution_gets_no_list()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);
        api.Solution = null;

        SolutionEpicMemberTasks.Choose(own, [own, webCopy], [Task(web, webCopy)], Context(api, web)).Should().BeNull();
    }

    [Fact]
    public void A_draft_whose_project_is_dropped_by_the_newer_solution_record_gets_no_list()
    {
        SolutionMembership withoutApi = SolutionDecider.Create(Storefront.Key, "storefront", [Web, Ops]);
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web, withoutApi);
        ProjectDetails ops = Project(Ops, withoutApi);
        api.TeamSettingStamps[nameof(ProjectDetails.Solution)] = Now.AddHours(-1);
        web.TeamSettingStamps[nameof(ProjectDetails.Solution)] = Now;
        ops.TeamSettingStamps[nameof(ProjectDetails.Solution)] = Now;
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);

        SolutionEpicMemberTasks.Choose(own, [own, webCopy], [Task(web, webCopy)], Context(api, web, ops)).Should().BeNull();
    }

    [Fact]
    public void It_lists_the_tasks_under_every_copy_a_member_holds_of_the_epic()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails firstWebCopy = Copy(web);
        EpicDetails secondWebCopy = Copy(web);
        TaskListItem underFirst = Task(web, firstWebCopy, "Render the cart");
        TaskListItem underSecond = Task(web, secondWebCopy, "Render the checkout");

        IReadOnlyList<SolutionMemberTask>? chosen = SolutionEpicMemberTasks.Choose(
            own, [own, firstWebCopy, secondWebCopy], [underFirst, underSecond], Context(api, web));

        (chosen ?? []).Select(task => task.Id).Should().BeEquivalentTo([underFirst.Id, underSecond.Id]);
    }

    [Fact]
    public void A_draft_in_a_solution_epic_the_other_members_hold_nothing_under_gets_an_empty_list_not_none()
    {
        ProjectDetails api = Project(Api);
        ProjectDetails web = Project(Web);
        EpicDetails own = Copy(api);
        EpicDetails webCopy = Copy(web);

        SolutionEpicMemberTasks.Choose(own, [own, webCopy], [Task(api, own)], Context(api, web)).Should().NotBeNull().And.BeEmpty();
    }
}
