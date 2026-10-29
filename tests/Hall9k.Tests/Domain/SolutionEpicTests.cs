using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A solution epic is one ordinary epic per member project sharing a key, so everything about it that
/// needs no database is here: how the copies are minted, how they group into the one epic a person
/// sees, what the shared-issue rule admits, and when closeout holds an issue open. Nothing opens a
/// store, creates a branch, or touches GitHub.
/// </summary>
public sealed class SolutionEpicTests
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    private static readonly SolutionMember Api = new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));
    private static readonly SolutionMember Web = new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));

    private static readonly SolutionMembership Storefront = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);

    private static EpicDetails Copy(EpicAdded added, EpicState? state = null)
    {
        EpicDetails view = new EpicDetailsProjection().Create(new FakeEvent<EpicAdded>(added));
        view.State = state ?? EpicState.Open;
        return view;
    }

    private static ProjectDetails Project(SolutionMember member, SolutionMembership? solution) => new()
    {
        Id = DomainId.New(),
        Name = member.Name,
        ProjectKey = member.ProjectKey,
        Solution = solution,
    };

    private static TaskListItem Task(ProjectDetails project, EpicDetails? epic, TaskState? state = null) => new()
    {
        Id = DomainId.New(),
        ProjectId = project.Id,
        EpicId = epic?.Id,
        State = state ?? TaskState.Published,
        Objective = "Add the cart",
        AddedAt = Now,
    };

    [Fact]
    public void An_epic_added_before_solutions_existed_replays_as_a_project_epic()
    {
        string legacy = $$"""{"Id":"{{DomainId.New()}}","ProjectId":"{{DomainId.New()}}","Title":"Old","AddedAt":"2026-08-28T12:00:00+00:00","AddedByOwnerId":"{{Owner}}"}""";

        EpicAdded replayed = JsonSerializer.Deserialize<EpicAdded>(legacy)!;
        EpicDetails view = new EpicDetailsProjection().Create(new FakeEvent<EpicAdded>(replayed));

        replayed.SolutionEpicKey.Should().BeNull();
        replayed.SolutionKey.Should().BeNull();
        view.IsSolutionEpic.Should().BeFalse();
        view.State.Should().Be(EpicState.Open);
    }

    [Fact]
    public void The_copy_projection_carries_both_keys()
    {
        EpicAdded added = EpicDecider.Add(
            DomainId.New(), DomainId.New(), "Cart", Now, Owner, SolutionEpicDecider.NewKey(), Storefront.Key);

        EpicDetails view = Copy(added);

        view.IsSolutionEpic.Should().BeTrue();
        view.SolutionEpicKey.Should().Be(added.SolutionEpicKey);
        view.SolutionKey.Should().Be(Storefront.Key);
    }

    [Fact]
    public void The_two_keys_are_set_together_and_are_ulids()
    {
        Action onlyEpicKey = () => EpicDecider.Add(DomainId.New(), DomainId.New(), "Cart", Now, Owner, SolutionEpicDecider.NewKey(), null);
        Action onlySolutionKey = () => EpicDecider.Add(DomainId.New(), DomainId.New(), "Cart", Now, Owner, null, Storefront.Key);
        Action notAUlid = () => EpicDecider.Add(DomainId.New(), DomainId.New(), "Cart", Now, Owner, "not-a-key", Storefront.Key);

        onlyEpicKey.Should().Throw<DomainValidationException>().WithMessage("*both*or neither*");
        onlySolutionKey.Should().Throw<DomainValidationException>().WithMessage("*both*or neither*");
        notAUlid.Should().Throw<DomainValidationException>().WithMessage("*26-character ULID*");
    }

    [Fact]
    public void A_new_solution_epic_is_one_copy_per_held_project_sharing_one_key_and_the_title()
    {
        Guid api = DomainId.New();
        Guid web = DomainId.New();

        IReadOnlyList<EpicAdded> copies = SolutionEpicDecider.Add(Storefront.Key, "  Cart  ", [api, web], DomainId.New, Now, Owner);

        copies.Select(copy => copy.ProjectId).Should().Equal(api, web);
        copies.Select(copy => copy.Title).Should().OnlyContain(title => title == "Cart");
        copies.Select(copy => copy.SolutionEpicKey).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        copies.Select(copy => copy.SolutionKey).Should().OnlyContain(key => key == Storefront.Key);
        copies.Select(copy => copy.Id).Distinct().Should().HaveCount(2, "each copy is its own epic stream");
    }

    [Fact]
    public void Copies_group_into_one_epic_whose_state_is_closed_as_soon_as_a_copy_is()
    {
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [DomainId.New(), DomainId.New()], DomainId.New, Now, Owner);
        EpicDetails projectEpic = Copy(EpicDecider.Add(DomainId.New(), DomainId.New(), "Alone", Now, Owner));

        IReadOnlyList<EpicGroup> open = EpicGroup.Of([Copy(added[0]), Copy(added[1]), projectEpic]);
        IReadOnlyList<EpicGroup> oneClosed = EpicGroup.Of([Copy(added[0]), Copy(added[1], EpicState.Closed)]);

        open.Should().HaveCount(2);
        EpicGroup solution = open.Single(group => group.IsSolutionEpic);
        solution.Copies.Should().HaveCount(2);
        solution.Title.Should().Be("Cart");
        solution.Reference.Should().Be(added[0].SolutionEpicKey);
        open.Single(group => !group.IsSolutionEpic).Reference.Should().Be(projectEpic.Id.ToString());
        solution.State.Should().Be(EpicState.Open);
        oneClosed.Single().State.Should().Be(EpicState.Closed, "closing is one act, so a closed copy beside an open one is that act still replicating");
    }

    [Fact]
    public void A_project_linked_later_gets_a_copy_of_every_open_solution_epic_it_lacks_and_nothing_else()
    {
        Guid api = DomainId.New();
        Guid admin = DomainId.New();
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api], DomainId.New, Now, Owner);
        IReadOnlyList<EpicAdded> checkout = SolutionEpicDecider.Add(Storefront.Key, "Checkout", [api], DomainId.New, Now.AddHours(1), Owner);
        IReadOnlyList<EpicAdded> retired = SolutionEpicDecider.Add(Storefront.Key, "Retired", [api], DomainId.New, Now.AddHours(2), Owner);
        IReadOnlyList<EpicAdded> elsewhere = SolutionEpicDecider.Add(SolutionDecider.NewKey(), "Another solution's", [api], DomainId.New, Now, Owner);
        EpicDetails projectEpic = Copy(EpicDecider.Add(DomainId.New(), api, "A project epic", Now, Owner));
        EpicDetails[] held =
        [
            Copy(cart[0]), Copy(checkout[0]), Copy(retired[0], EpicState.Closed), Copy(elsewhere[0]), projectEpic,
        ];

        IReadOnlyList<EpicAdded> copies = SolutionEpicDecider.CopiesForNewMembers(held, Storefront.Key, [admin], DomainId.New, Now.AddDays(1), Owner);

        copies.Select(copy => copy.Title).Should().BeEquivalentTo("Cart", "Checkout");
        copies.Should().OnlyContain(copy => copy.ProjectId == admin && copy.SolutionKey == Storefront.Key);
        copies.Single(copy => copy.Title == "Cart").SolutionEpicKey.Should().Be(cart[0].SolutionEpicKey, "the copy joins the same epic");
    }

    [Fact]
    public void A_project_that_kept_a_copy_from_an_earlier_membership_is_not_given_a_second()
    {
        Guid api = DomainId.New();
        Guid web = DomainId.New();
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api, web], DomainId.New, Now, Owner);

        IReadOnlyList<EpicAdded> copies = SolutionEpicDecider.CopiesForNewMembers(
            [Copy(cart[0]), Copy(cart[1])], Storefront.Key, [web], DomainId.New, Now.AddDays(1), Owner);

        copies.Should().BeEmpty();
    }

    [Fact]
    public void A_second_task_may_hold_an_issue_in_the_same_solution_epic_in_a_different_member_project()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, Owner);
        EpicDetails apiCopy = Copy(cart[0]);
        EpicDetails webCopy = Copy(cart[1]);
        SharedIssueHolder holder = new(Task(api, apiCopy), apiCopy, api);

        SharedIssueRule.FirstBar(web.Id, web, webCopy, [holder]).Should().BeNull();
    }

    [Fact]
    public void A_holder_outside_the_epic_in_another_member_project_bars_the_task_and_is_told_to_join_the_epic_first()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, Owner);
        IReadOnlyList<EpicAdded> checkout = SolutionEpicDecider.Add(Storefront.Key, "Checkout", [api.Id, web.Id], DomainId.New, Now, Owner);
        EpicDetails webCart = Copy(cart[1]);
        SharedIssueHolder noEpic = new(Task(api, null), null, api);
        SharedIssueHolder otherEpic = new(Task(api, Copy(checkout[0])), Copy(checkout[0]), api);

        SharedIssueRule.FirstBar(web.Id, web, webCart, [noEpic])!.OutsideSolutionEpic.Should().BeTrue();
        SharedIssueRule.FirstBar(web.Id, web, webCart, [otherEpic])!.OutsideSolutionEpic.Should().BeTrue();
    }

    [Fact]
    public void Every_other_case_keeps_the_one_live_task_per_issue_refusal()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        ProjectDetails stranger = Project(new SolutionMember("01JDDDDDDDDDDDDDDDDDDDDDDD", "other", new Uri("https://github.com/acme/other.git")), null);
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, Owner);
        EpicDetails apiCopy = Copy(cart[0]);
        EpicDetails webCopy = Copy(cart[1]);
        EpicDetails projectEpic = Copy(EpicDecider.Add(DomainId.New(), web.Id, "Project only", Now, Owner));
        SharedIssueHolder inApi = new(Task(api, apiCopy), apiCopy, api);
        SharedIssueHolder inWeb = new(Task(web, webCopy), webCopy, web);
        SharedIssueHolder unlinkedApi = new(Task(api, apiCopy), apiCopy, Project(Api, null));

        // No epic, or a project epic, for the new task: today's rule, whoever holds the issue.
        SharedIssueRule.FirstBar(web.Id, web, null, [inApi])!.OutsideSolutionEpic.Should().BeFalse();
        SharedIssueRule.FirstBar(web.Id, web, projectEpic, [inApi])!.OutsideSolutionEpic.Should().BeFalse();
        // The same project: two tasks of one project still never share an issue.
        SharedIssueRule.FirstBar(web.Id, web, webCopy, [inWeb])!.OutsideSolutionEpic.Should().BeFalse();
        // A holder whose project has left the solution is no member project.
        SharedIssueRule.FirstBar(web.Id, web, webCopy, [unlinkedApi]).Should().NotBeNull();
        // A new task whose own project has left the solution cannot share either.
        SharedIssueRule.FirstBar(stranger.Id, stranger, webCopy, [inApi]).Should().NotBeNull();
        // One bad holder among good ones bars the task, and is the one named.
        SharedIssueRule.FirstBar(web.Id, web, webCopy, [inApi, inWeb])!.Holder.Should().Be(inWeb);
    }

    [Fact]
    public void Closeout_has_nothing_to_say_unless_another_projects_task_in_the_same_solution_epic_holds_the_issue()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, Owner);
        EpicDetails apiCopy = Copy(cart[0]);
        TaskListItem closing = Task(api, apiCopy, TaskState.Done);
        SharedIssueHolder sameProject = new(Task(api, apiCopy), apiCopy, api);
        SharedIssueHolder noEpic = new(Task(web, null), null, web);

        SharedIssueRule.CloseoutHold(closing, null, [], Storefront, [], everyOtherLinkedTaskClosedOut: true).Should().BeNull();
        SharedIssueRule.CloseoutHold(closing, apiCopy, [sameProject, noEpic], Storefront, [Web], everyOtherLinkedTaskClosedOut: false)
            .Should().BeNull("a holder in the same project or outside the epic is the ordinary cross-task gate's business");
    }

    [Fact]
    public void Closeout_holds_the_issue_open_unless_this_node_holds_every_member_and_every_other_task_is_closed_out()
    {
        ProjectDetails api = Project(Api, Storefront);
        ProjectDetails web = Project(Web, Storefront);
        IReadOnlyList<EpicAdded> cart = SolutionEpicDecider.Add(Storefront.Key, "Cart", [api.Id, web.Id], DomainId.New, Now, Owner);
        EpicDetails apiCopy = Copy(cart[0]);
        EpicDetails webCopy = Copy(cart[1]);
        TaskListItem closing = Task(api, apiCopy, TaskState.Done);
        SharedIssueHolder other = new(Task(web, webCopy), webCopy, web);

        SharedIssueRule.CloseoutHold(closing, apiCopy, [other], Storefront, [Web], everyOtherLinkedTaskClosedOut: true)
            .Should().Contain("does not hold 'shop-web'").And.Contain("storefront");
        SharedIssueRule.CloseoutHold(closing, apiCopy, [other], null, [], everyOtherLinkedTaskClosedOut: true)
            .Should().Contain("no record of the solution");
        SharedIssueRule.CloseoutHold(closing, apiCopy, [other], Storefront, [], everyOtherLinkedTaskClosedOut: false)
            .Should().Contain("not yet done or abandoned");
        SharedIssueRule.CloseoutHold(closing, apiCopy, [other], Storefront, [], everyOtherLinkedTaskClosedOut: true)
            .Should().BeNull("every member is held and every other task is closed out, so the ordinary rule decides");
    }

    [Fact]
    public void Closing_a_solution_epic_closes_every_copy_this_node_holds_with_one_reason_and_skips_a_copy_already_closed()
    {
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [DomainId.New(), DomainId.New(), DomainId.New()], DomainId.New, Now, Owner);
        EpicAggregate[] copies = [.. added.Select(copy => { EpicAggregate aggregate = new(); aggregate.Apply(copy); return aggregate; })];
        copies[2].Apply(new EpicClosed(copies[2].Id, "closed elsewhere", Now, Owner));

        IReadOnlyList<EpicClosed> closes = Hall9k.Cli.Commands.EpicCloseCommand.Close(copies, "shipped", Now.AddDays(1), Owner);

        closes.Select(close => close.Id).Should().BeEquivalentTo([copies[0].Id, copies[1].Id]);
        closes.Should().OnlyContain(close => close.Reason == "shipped");
    }

    [Fact]
    public void Closing_without_a_reason_is_refused_and_so_is_closing_a_solution_epic_with_nothing_open()
    {
        IReadOnlyList<EpicAdded> added = SolutionEpicDecider.Add(Storefront.Key, "Cart", [DomainId.New()], DomainId.New, Now, Owner);
        EpicAggregate copy = new();
        copy.Apply(added[0]);

        Action blank = () => Hall9k.Cli.Commands.EpicCloseCommand.Close([copy], " ", Now, Owner);
        blank.Should().Throw<DomainValidationException>().WithMessage("*records why*");

        copy.Apply(new EpicClosed(copy.Id, "done", Now, Owner));
        Action closed = () => Hall9k.Cli.Commands.EpicCloseCommand.Close([copy], "again", Now, Owner);
        closed.Should().Throw<DomainConflictException>().WithMessage("*only an open epic closes*");
    }
}
