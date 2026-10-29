using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The solution board and the CLI-side decisions around it. Everything here is pure: the rows come
/// from <see cref="StatusFixtures"/>, the projects are plain documents, and nothing opens a store,
/// creates a branch, or touches GitHub.
/// </summary>
public sealed class SolutionBoardTests
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string AdminKey = "01JCCCCCCCCCCCCCCCCCCCCCCC";
    private const string RootFingerprint = "SHA256:root";

    private static readonly Guid OwnerId = DomainId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly SolutionMember Api = new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));
    private static readonly SolutionMember Web = new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));
    private static readonly SolutionMember Admin = new(AdminKey, "shop-admin", new Uri("https://github.com/acme/shop-admin.git"));

    private static string? RootOf(Guid ownerId) => ownerId == OwnerId ? RootFingerprint : null;

    private static ProjectDetails Project(SolutionMember member, SolutionMembership? solution = null) => new()
    {
        Id = DomainId.New(),
        OwnerId = OwnerId,
        Name = member.Name,
        ProjectKey = member.ProjectKey,
        RepositoryUrl = member.RepositoryUrl,
        Solution = solution,
        Members = new Dictionary<string, ProjectMemberRole> { [RootFingerprint] = ProjectMemberRole.Owner },
    };

    private static TaskStatusRow Row(ProjectDetails project, TaskState state, string objective, int minutesAgo) =>
        StatusFixtures.Compose(
            StatusFixtures.Task(state, objective: objective, projectId: project.Id, addedAt: Now.AddMinutes(-minutesAgo)),
            projects: new Dictionary<Guid, string> { [project.Id] = project.Name });

    [Fact]
    public void The_board_groups_each_held_members_tasks_by_project_newest_first_and_rolls_them_up()
    {
        SolutionMembership solution = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);
        ProjectDetails api = Project(Api, solution);
        ProjectDetails web = Project(Web, solution);
        ProjectDetails unrelated = Project(Admin);
        TaskStatusRow[] rows =
        [
            Row(api, TaskState.Published, "Add a cart endpoint", minutesAgo: 30),
            Row(api, TaskState.Done, "Fix the price rounding", minutesAgo: 5),
            Row(web, TaskState.Draft, "Show the cart badge", minutesAgo: 10),
            Row(unrelated, TaskState.Published, "Not part of this solution", minutesAgo: 1),
        ];

        SolutionBoard board = SolutionBoard.Compose(
            SolutionRegistry.Read([api, web]).Single(), [api, web, unrelated], rows, RootOf);

        board.Projects.Select(project => project.Member.Name).Should().Equal("shop-api", "shop-web");
        board.Projects[0].Rows.Select(row => row.Objective).Should().Equal("Fix the price rounding", "Add a cart endpoint");
        board.Projects[1].Rows.Should().ContainSingle().Which.Objective.Should().Be("Show the cart badge");
        board.Rollup.Total.Should().Be(3, "another project's task is on nobody's board here");
        board.Projects[0].Rollup.Done.Should().Be(1);
        board.NotJoined.Should().BeEmpty();
    }

    [Fact]
    public void A_member_this_node_has_not_joined_is_listed_by_name_and_url_with_no_rows()
    {
        SolutionMembership solution = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web, Admin]);
        ProjectDetails api = Project(Api, solution);
        ProjectDetails web = Project(Web, solution);
        web.Members = [];

        SolutionBoard board = SolutionBoard.Compose(
            SolutionRegistry.Read([api, web]).Single(), [api, web], [Row(web, TaskState.Published, "Hidden until joined", 1)], RootOf);

        board.Projects.Select(project => project.Member.Name).Should().Equal("shop-api");
        board.NotJoined.Select(member => member.Name).Should().Equal("shop-web", "shop-admin");
        board.NotJoined.Select(member => member.RepositoryUrl.ToString()).Should().Equal(
            "https://github.com/acme/shop-web.git", "https://github.com/acme/shop-admin.git");
        board.Rollup.Total.Should().Be(0);
    }

    [Fact]
    public void The_header_shows_a_closed_solutions_reason_and_an_open_ones_state()
    {
        SolutionMembership open = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);
        SolutionMembership closed = SolutionDecider.Close(open, "Retired for the [marketplace]");

        RenderedText(SolutionShowCommand.Header(open)).Should().Contain("open").And.Contain("storefront").And.Contain(open.Key);
        RenderedText(SolutionShowCommand.Header(closed)).Should().Contain("closed").And.Contain("Retired for the [marketplace]");
    }

    [Fact]
    public void The_board_table_is_the_project_show_table_every_row_an_id_and_a_plain_description()
    {
        ProjectDetails api = Project(Api);
        TaskStatusRow row = Row(api, TaskState.Published, "Add a cart endpoint", 1);

        string rendered = RenderedText(ProjectShowCommand.TaskTable([row], 160, Now));

        rendered.Should().Contain(row.IdMarkup.Replace("[dim]", string.Empty).Replace("[/]", string.Empty).Trim()[..4]);
        rendered.Should().Contain("Add a cart endpoint");
    }

    [Fact]
    public void The_scope_of_a_solution_is_its_member_projects_known_here_by_carrying_the_membership_or_holding_a_members_key()
    {
        SolutionMembership solution = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web, Admin]);
        ProjectDetails api = Project(Api, solution);
        ProjectDetails webWithoutTheRecord = Project(Web);
        ProjectDetails stranger = Project(new SolutionMember("01JDDDDDDDDDDDDDDDDDDDDDDD", "other", Api.RepositoryUrl));

        IReadOnlySet<Guid> ids = SolutionScope.ProjectIds(
            SolutionRegistry.Read([api]).Single(), [api, webWithoutTheRecord, stranger]);

        ids.Should().BeEquivalentTo([api.Id, webWithoutTheRecord.Id]);
    }

    [Fact]
    public void A_member_that_has_joined_another_solution_is_off_this_solutions_board_and_filter()
    {
        SolutionMembership closed = SolutionDecider.Close(SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]), "retired");
        SolutionMembership marketplace = SolutionDecider.Create(SolutionDecider.NewKey(), "marketplace", [Web, Admin]);
        ProjectDetails api = Project(Api, closed);
        ProjectDetails web = Project(Web, marketplace);
        SolutionView view = SolutionRegistry.Read([api, web]).Single(solution => solution.Membership.Key == closed.Key);

        SolutionScope.ProjectIds(view, [api, web]).Should().BeEquivalentTo([api.Id]);
        SolutionBoard board = SolutionBoard.Compose(
            view, [api, web], [Row(web, TaskState.Published, "Belongs to marketplace now", 1)], RootOf);

        board.Projects.Select(project => project.Project.Name).Should().Equal("shop-api");
        board.NotJoined.Should().BeEmpty("the member is registered and joined, it has only moved on");
        board.MovedOn.Should().ContainSingle().Which.Should().Be(new SolutionBoardMovedMember(Web, marketplace));
        board.Rollup.Total.Should().Be(0);
    }

    [Fact]
    public void A_carrier_the_record_does_not_list_is_on_neither_the_board_nor_the_filter()
    {
        string key = SolutionDecider.NewKey();
        SolutionMembership rename = SolutionDecider.Rename(SolutionDecider.Create(key, "storefront", [Api, Web]), "storefront-v2");
        SolutionMembership older = SolutionDecider.Create(key, "storefront", [Api, Web, Admin]);
        ProjectDetails api = Project(Api, rename);
        api.TeamSettingStamps[nameof(ProjectDetails.Solution)] = Now;
        ProjectDetails admin = Project(Admin, older);
        SolutionView view = SolutionRegistry.Read([api, admin]).Single();

        view.Unlisted.Should().ContainSingle().Which.Should().BeSameAs(admin);
        SolutionScope.ProjectIds(view, [api, admin]).Should().BeEquivalentTo([api.Id]);
        SolutionBoard.Compose(view, [api, admin], [], RootOf).Projects.Select(project => project.Project.Name)
            .Should().Equal("shop-api");
    }

    [Fact]
    public void The_project_show_row_names_the_solution_the_closed_state_or_how_to_make_one()
    {
        SolutionMembership open = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);
        SolutionMembership closed = SolutionDecider.Close(open, "retired");

        ProjectShowCommand.SolutionRow(Project(Api)).Should().Contain("h9k solution add");
        ProjectShowCommand.SolutionRow(Project(Api, open)).Should().Contain("storefront").And.Contain("2 projects");
        ProjectShowCommand.SolutionRow(Project(Api, closed)).Should().Contain("closed").And.Contain("retired");
    }

    [Fact]
    public void The_owner_warning_is_silent_when_nothing_differs()
    {
        FakeConfirmation confirmation = new(interactive: false);

        SolutionOwnerConfirmation.Confirm([], "storefront", yes: false, confirmation).Should().BeTrue();
        confirmation.Asked.Should().BeFalse();
    }

    [Fact]
    public void A_different_owner_asks_and_yes_answers_it_without_asking()
    {
        IReadOnlyList<SolutionOwnerMismatch> mismatch = Mismatch();

        FakeConfirmation interactive = new(interactive: true, answer: true);
        SolutionOwnerConfirmation.Confirm(mismatch, "storefront", yes: false, interactive).Should().BeTrue();
        interactive.Asked.Should().BeTrue();

        FakeConfirmation declined = new(interactive: true, answer: false);
        SolutionOwnerConfirmation.Confirm(mismatch, "storefront", yes: false, declined).Should().BeFalse();

        FakeConfirmation withYes = new(interactive: true, answer: false);
        SolutionOwnerConfirmation.Confirm(mismatch, "storefront", yes: true, withYes).Should().BeTrue();
        withYes.Asked.Should().BeFalse("--yes answers it");
    }

    [Fact]
    public void A_session_that_cannot_prompt_refuses_a_different_owner_without_yes_quoting_the_flag()
    {
        FakeConfirmation noTerminal = new(interactive: false);

        Action act = () => SolutionOwnerConfirmation.Confirm(Mismatch(), "storefront", yes: false, noTerminal);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*different owner or organization*github.com/other-org*github.com/acme*cannot prompt*--yes*");
        noTerminal.Asked.Should().BeFalse();
    }

    [Fact]
    public void A_task_list_scoped_by_solution_never_reads_as_the_whole_board_and_keeps_the_filter_in_its_hints()
    {
        TaskListCommand.Settings settings = new() { Solution = "storefront" };

        string allArchived = TaskListCommand.EmptyResultMessage(3, 0, settings, project: null);
        string nothing = TaskListCommand.EmptyResultMessage(0, 0, settings, project: null);
        string footer = TaskListCommand.Footer(30, 20, 0, 0, settings, project: null);

        allArchived.Should().Contain("matching --solution storefront").And.Contain("--include-archived --solution storefront");
        nothing.Should().Contain("--solution storefront");
        footer.Should().Contain("in solution storefront").And.Contain("--all --solution storefront");
    }

    [Fact]
    public void An_epic_list_scoped_by_solution_names_it_in_its_footer_and_keeps_it_in_the_state_hint()
    {
        EpicDetails open = new() { Id = DomainId.New(), State = EpicState.Open, Title = "Cart" };
        EpicDetails closed = new() { Id = DomainId.New(), State = EpicState.Closed, Title = "Old" };

        string footer = EpicListCommand.Footer(EpicGroup.Of([open]), EpicGroup.Of([open, closed]), project: null, state: EpicState.Open, solution: "storefront");

        footer.Should().Contain("in solution storefront").And.Contain("h9k epic list --state all --solution storefront");
    }

    private static IReadOnlyList<SolutionOwnerMismatch> Mismatch() =>
        SolutionDecider.OwnerMismatches(
            [Api], [new SolutionMember(AdminKey, "other-admin", new Uri("https://github.com/other-org/other-admin.git"))]);

    private static string RenderedText(Spectre.Console.Rendering.IRenderable renderable)
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

    private sealed class FakeConfirmation(bool interactive, bool answer = false) : IInteractiveConfirmation
    {
        public bool IsInteractive => interactive;

        public bool Asked { get; private set; }

        public bool Confirm(string prompt, bool defaultValue)
        {
            Asked = true;
            return answer;
        }
    }
}
