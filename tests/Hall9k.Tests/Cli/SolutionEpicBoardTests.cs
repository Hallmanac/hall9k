using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A solution epic read as the one epic it is: one group however many copies, one row, the solution
/// named, and member tasks grouped by their project. Everything here is pure; nothing opens a store,
/// creates a branch, or touches GitHub.
/// </summary>
public sealed class SolutionEpicBoardTests
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string RootFingerprint = "SHA256:root";

    private static readonly Guid OwnerId = DomainId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly SolutionMember Api = new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));
    private static readonly SolutionMember Web = new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));

    private static readonly SolutionMembership Storefront = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);

    private static ProjectDetails Project(SolutionMember member, SolutionMembership? solution, bool joined = true) => new()
    {
        Id = DomainId.New(),
        OwnerId = OwnerId,
        Name = member.Name,
        ProjectKey = member.ProjectKey,
        RepositoryUrl = member.RepositoryUrl,
        Solution = solution,
        Members = joined ? new Dictionary<string, ProjectMemberRole> { [RootFingerprint] = ProjectMemberRole.Owner } : [],
    };

    private static SolutionContext Context(params ProjectDetails[] projects) => new(
        projects,
        SolutionRegistry.Read(projects),
        new Dictionary<Guid, string?> { [OwnerId] = RootFingerprint });

    private static EpicDetails Copy(EpicAdded added)
    {
        EpicDetails view = new EpicDetailsProjection().Create(new FakeEvent<EpicAdded>(added));
        view.State = EpicState.Open;
        return view;
    }

    private static TaskStatusRow Row(ProjectDetails project, EpicDetails epic, string objective, int minutesAgo)
    {
        Hall9k.Domain.Features.Tasks.Projections.TaskListItem item = StatusFixtures.Task(
            TaskState.Published, objective: objective, projectId: project.Id, addedAt: Now.AddMinutes(-minutesAgo));
        item.EpicId = epic.Id;
        return StatusFixtures.Compose(item, projects: new Dictionary<Guid, string> { [project.Id] = project.Name });
    }

    [Fact]
    public void A_solution_epic_is_one_board_with_its_tasks_grouped_by_project_and_the_solution_named()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, OwnerId);
        EpicDetails apiCopy = Copy(added[0]);
        EpicDetails webCopy = Copy(added[1]);
        TaskStatusRow[] rows =
        [
            Row(api, apiCopy, "Add the cart endpoint", minutesAgo: 30),
            Row(api, apiCopy, "Price the cart", minutesAgo: 5),
            Row(web, webCopy, "Show the cart badge", minutesAgo: 10),
        ];

        EpicBoard board = EpicBoard.Compose(EpicGroup.Of([apiCopy, webCopy]).Single(), Context(api, web), rows);

        board.SolutionName.Should().Be("storefront");
        board.Projects.Select(project => project.ProjectName).Should().Equal("shop-api", "shop-web");
        board.Projects[0].Rows.Select(row => row.Objective).Should().Equal("Price the cart", "Add the cart endpoint");
        board.Projects[1].Rows.Should().ContainSingle().Which.Objective.Should().Be("Show the cart badge");
        board.Rollup.Total.Should().Be(3);
    }

    [Fact]
    public void A_node_shows_only_the_tasks_of_the_projects_it_has_joined()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront, joined: false);
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, OwnerId);
        EpicDetails apiCopy = Copy(added[0]);
        EpicDetails webCopy = Copy(added[1]);
        TaskStatusRow[] rows = [Row(api, apiCopy, "Add the cart endpoint", 30), Row(web, webCopy, "Not mine to show", 10)];

        EpicBoard board = EpicBoard.Compose(EpicGroup.Of([apiCopy, webCopy]).Single(), Context(api, web), rows);

        board.Projects.Select(project => project.ProjectName).Should().Equal("shop-api");
        board.Rollup.Total.Should().Be(1);
    }

    [Fact]
    public void A_project_epic_is_a_board_of_one_project_with_no_solution()
    {
        ProjectDetails web = Project(Web, null);
        EpicDetails epic = Copy(EpicDecider.Add(DomainId.New(), web.Id, "Interactive mode", Now, OwnerId));

        EpicBoard board = EpicBoard.Compose(EpicGroup.Of([epic]).Single(), Context(web), [Row(web, epic, "A task", 1)]);

        board.SolutionName.Should().BeNull();
        board.Projects.Should().ContainSingle().Which.ProjectName.Should().Be("shop-web");
        board.Rollup.Total.Should().Be(1);
    }

    [Fact]
    public void After_an_unlink_the_projects_copy_and_tasks_stay_on_the_solution_epic()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, OwnerId);
        EpicDetails apiCopy = Copy(added[0]);
        EpicDetails webCopy = Copy(added[1]);
        TaskStatusRow[] rows = [Row(api, apiCopy, "Add the cart endpoint", 30), Row(web, webCopy, "Show the cart badge", 10)];
        SolutionMembership remaining = SolutionDecider.Unlink(Storefront, WebKey);
        ProjectDetails stillApi = Project(Api, remaining);
        stillApi.Id = api.Id;
        ProjectDetails unlinkedWeb = Project(Web, null);
        unlinkedWeb.Id = web.Id;

        EpicBoard board = EpicBoard.Compose(EpicGroup.Of([apiCopy, webCopy]).Single(), Context(stillApi, unlinkedWeb), rows);

        board.Projects.Select(project => project.ProjectName).Should().Equal("shop-api", "shop-web");
        board.Rollup.Total.Should().Be(2, "unlinking leaves the copy and every task under it where they were");
    }

    [Fact]
    public void The_header_names_the_solution_the_key_and_every_copy_once()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, OwnerId);
        EpicBoard board = EpicBoard.Compose(
            EpicGroup.Of([Copy(added[0]), Copy(added[1])]).Single(), Context(api, web), []);

        string rendered = Render(EpicShowCommand.Header(board));

        rendered.Should().Contain("Cart").And.Contain("storefront").And.Contain(added[0].SolutionEpicKey!)
            .And.Contain("shop-api").And.Contain("shop-web");
        rendered.Split('\n').Count(line => line.Contains("Epic")).Should().Be(1, "one epic, one header");
    }

    [Fact]
    public void A_solution_epic_is_referenced_by_the_tail_of_its_shared_key_and_a_project_epic_by_its_id_tail()
    {
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [DomainId.New()], DomainId.New, Now, OwnerId);
        EpicDetails solution = Copy(added[0]);
        EpicDetails project = Copy(EpicDecider.Add(DomainId.New(), DomainId.New(), "Alone", Now, OwnerId));

        EpicListCommand.Reference(EpicGroup.Of([solution]).Single()).Should().Be(added[0].SolutionEpicKey![^8..]);
        EpicListCommand.Reference(EpicGroup.Of([project]).Single()).Should().Be(DomainId.Short(project.Id));
    }

    private static string Render(Spectre.Console.Rendering.IRenderable renderable)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;
        console.Write(renderable);
        return writer.ToString();
    }
}
