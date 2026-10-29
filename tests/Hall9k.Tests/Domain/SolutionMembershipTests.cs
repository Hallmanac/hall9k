using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The solution is a membership team setting on each member project, so everything it does rides
/// <see cref="ProjectDecider.ChangeSettings"/> and <see cref="ProjectTeamSettingsChanged"/>: what a
/// membership must be, the one-open-solution rule, the companion event that replicates it, and how
/// a receiving node's own project takes it.
/// </summary>
public sealed class SolutionMembershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";

    private static SolutionMember Api => new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));

    private static SolutionMember Web => new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));

    private static SolutionMembership Open(string key, string name = "storefront") =>
        SolutionDecider.Create(key, name, [Api, Web]);

    private static ProjectAggregate KeyedProject(string name, string projectKey)
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(), name, "/repos/" + name, null, null, Now));
        project.Apply(new ProjectKeyAssigned(project.Id, projectKey, Now));
        return project;
    }

    private static ProjectSettingsChanged Write(ProjectAggregate project, SolutionMembership? membership, DateTimeOffset? at = null) =>
        ProjectDecider.ChangeSettings(
            project,
            verifyCommands: default,
            skipPermissions: default,
            contextLinks: default,
            changedAt: at ?? Now,
            changedByOwnerId: DomainId.New(),
            solution: Optional<SolutionMembership?>.Of(membership));

    [Fact]
    public void A_membership_carries_the_key_name_state_close_reason_and_every_member()
    {
        SolutionMembership membership = Open(SolutionDecider.NewKey());

        membership.Key.Should().HaveLength(26);
        membership.State.Should().Be(SolutionState.Open);
        membership.CloseReason.Should().BeNull();
        membership.Members.Select(member => member.ProjectKey).Should().Equal(ApiKey, WebKey);
        membership.Members.Should().AllSatisfy(member =>
        {
            member.Name.Should().NotBeNullOrWhiteSpace();
            member.RepositoryUrl.IsAbsoluteUri.Should().BeTrue();
        });
    }

    [Fact]
    public void Validate_refuses_a_key_that_is_not_a_ulid()
    {
        Action act = () => SolutionDecider.Create("not-a-ulid", "storefront", [Api]);

        act.Should().Throw<DomainValidationException>().WithMessage("*not a solution key*26-character*");
    }

    [Fact]
    public void Validate_refuses_a_blank_name()
    {
        Action act = () => SolutionDecider.Create(SolutionDecider.NewKey(), "  ", [Api]);

        act.Should().Throw<DomainValidationException>().WithMessage("*requires a name*");
    }

    [Fact]
    public void Validate_refuses_a_closed_membership_without_a_reason_and_an_open_one_with_a_reason()
    {
        string key = SolutionDecider.NewKey();

        Action closedWithoutReason = () => SolutionDecider.Validate(new SolutionMembership(key, "s", SolutionState.Closed, " ", [Api]));
        Action openWithReason = () => SolutionDecider.Validate(new SolutionMembership(key, "s", SolutionState.Open, "done", [Api]));

        closedWithoutReason.Should().Throw<DomainValidationException>().WithMessage("*reason*");
        openWithReason.Should().Throw<DomainValidationException>().WithMessage("*no close reason*");
    }

    [Fact]
    public void Validate_refuses_an_unknown_state_no_members_a_bad_member_key_and_a_repeated_member()
    {
        string key = SolutionDecider.NewKey();

        Action unknownState = () => SolutionDecider.Validate(new SolutionMembership(key, "s", SolutionState.Unknown, null, [Api]));
        Action noMembers = () => SolutionDecider.Validate(new SolutionMembership(key, "s", SolutionState.Open, null, []));
        Action badMemberKey = () => SolutionDecider.Validate(
            new SolutionMembership(key, "s", SolutionState.Open, null, [Api with { ProjectKey = "a-project-id" }]));
        Action repeated = () => SolutionDecider.Validate(new SolutionMembership(key, "s", SolutionState.Open, null, [Api, Api]));

        unknownState.Should().Throw<DomainValidationException>().WithMessage("*Open*Closed*");
        noMembers.Should().Throw<DomainValidationException>().WithMessage("*at least one project*");
        badMemberKey.Should().Throw<DomainValidationException>().WithMessage("*not a project key*");
        repeated.Should().Throw<DomainValidationException>().WithMessage("*listed twice*");
    }

    [Fact]
    public void ChangeSettings_writes_a_membership_and_the_aggregate_holds_it_as_one_value()
    {
        ProjectAggregate project = KeyedProject("shop-api", ApiKey);
        SolutionMembership membership = Open(SolutionDecider.NewKey());

        project.Apply(Write(project, membership));
        project.Solution.Should().BeSameAs(membership);

        SolutionMembership renamed = SolutionDecider.Rename(membership, "storefront-v2");
        project.Apply(Write(project, renamed));
        project.Solution.Should().BeSameAs(renamed, "the setting is a single value in storage: a rewrite replaces it, it never accumulates");
    }

    [Fact]
    public void A_project_in_an_open_solution_refuses_a_membership_with_a_different_key_naming_the_solution_and_unlink()
    {
        ProjectAggregate project = KeyedProject("shop-api", ApiKey);
        project.Apply(Write(project, Open(SolutionDecider.NewKey(), "storefront")));

        Action act = () => Write(project, Open(SolutionDecider.NewKey(), "marketplace"));

        act.Should().Throw<DomainConflictException>()
            .WithMessage("*already in the open solution 'storefront'*at most one open solution*h9k solution unlink*");
    }

    [Fact]
    public void A_project_may_be_rewritten_into_its_own_solution_cleared_or_moved_once_the_solution_is_closed()
    {
        ProjectAggregate project = KeyedProject("shop-api", ApiKey);
        SolutionMembership storefront = Open(SolutionDecider.NewKey(), "storefront");
        project.Apply(Write(project, storefront));

        Action rewriteSame = () => Write(project, SolutionDecider.Rename(storefront, "storefront-v2"));
        Action clear = () => Write(project, null);
        rewriteSame.Should().NotThrow();
        clear.Should().NotThrow();

        project.Apply(Write(project, SolutionDecider.Close(storefront, "retired")));
        Action moveAfterClose = () => Write(project, Open(SolutionDecider.NewKey(), "marketplace"));
        moveAfterClose.Should().NotThrow("a closed membership does not hold the project");
    }

    [Fact]
    public void A_membership_that_omits_the_project_it_is_written_onto_is_refused()
    {
        ProjectAggregate project = KeyedProject("shop-admin", "01JCCCCCCCCCCCCCCCCCCCCCCC");

        Action act = () => Write(project, Open(SolutionDecider.NewKey()));

        act.Should().Throw<DomainValidationException>().WithMessage("*does not list project 'shop-admin'*");
    }

    [Fact]
    public void The_team_companion_carries_the_membership_and_a_plain_node_change_adds_none()
    {
        ProjectAggregate project = KeyedProject("shop-api", ApiKey);
        SolutionMembership membership = Open(SolutionDecider.NewKey());

        ProjectTeamSettingsChanged? companion = ProjectTeamSettingsChanged.From(Write(project, membership));

        companion.Should().NotBeNull();
        companion!.Solution.HasValue.Should().BeTrue();
        companion.Solution.Value.Should().BeSameAs(membership);

        ProjectTeamSettingsChanged.From(ProjectDecider.ChangeSettings(
            project, verifyCommands: default, skipPermissions: default, contextLinks: default,
            changedAt: Now, changedByOwnerId: DomainId.New(), model: Optional<AgentModel>.Of(AgentModel.Sonnet)))
            .Should().BeNull("the solution field alone makes a change a team change");
    }

    [Fact]
    public void The_team_companion_round_trips_through_the_stored_json_and_an_old_event_reads_as_absent()
    {
        JsonSerializerOptions stored = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ProjectTeamSettingsChanged companion = new(
            DomainId.New(), Now, DomainId.New(), Solution: Optional<SolutionMembership?>.Of(Open(SolutionDecider.NewKey())));

        ProjectTeamSettingsChanged read = JsonSerializer.Deserialize<ProjectTeamSettingsChanged>(
            JsonSerializer.Serialize(companion, stored), stored)!;

        read.Solution.Value.Should().BeEquivalentTo(companion.Solution.Value);
        read.Solution.Value!.State.Should().Be(SolutionState.Open);

        ProjectTeamSettingsChanged before = JsonSerializer.Deserialize<ProjectTeamSettingsChanged>(
            """{"id":"0191d3b0-0000-7000-8000-000000000001","changedAt":"2026-09-16T12:00:00+00:00","changedByOwnerId":"0191d3b0-0000-7000-8000-000000000002"}""",
            stored)!;
        before.Solution.HasValue.Should().BeFalse("a stream written before this setting existed replays unchanged");
    }

    [Fact]
    public void A_replicated_membership_lands_on_the_receivers_own_project_through_the_team_event()
    {
        // The receiver is a different install of the same repository: its own project id, its own
        // aggregate, and only the team companion ever reaches it.
        ProjectAggregate sender = KeyedProject("shop-api", ApiKey);
        SolutionMembership membership = Open(SolutionDecider.NewKey());
        ProjectTeamSettingsChanged replicated = ProjectTeamSettingsChanged.From(Write(sender, membership))!;

        ProjectAggregate receiverAggregate = KeyedProject("shop-api", ApiKey);
        receiverAggregate.Apply(replicated);
        receiverAggregate.Solution.Should().BeEquivalentTo(membership);

        ProjectDetailsProjection projection = new();
        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            DomainId.New(), DomainId.New(), DomainId.New(), "shop-api", "/repos/shop-api", null, "main", Now)));
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(replicated), view);

        view.Solution.Should().BeEquivalentTo(membership);
        // A node that receives the membership for any one member knows the whole solution from it.
        SolutionRegistry.Read([view]).Should().ContainSingle()
            .Which.Membership.Members.Select(member => member.Name).Should().Equal("shop-api", "shop-web");
    }

    [Fact]
    public void An_older_replicated_membership_never_overwrites_a_newer_one_and_a_null_clears_it()
    {
        ProjectDetailsProjection projection = new();
        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            DomainId.New(), DomainId.New(), DomainId.New(), "shop-api", "/repos/shop-api", null, "main", Now)));
        string key = SolutionDecider.NewKey();
        SolutionMembership newer = SolutionDecider.Rename(Open(key), "storefront-v2");

        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(new ProjectTeamSettingsChanged(
            view.Id, Now.AddMinutes(2), DomainId.New(), Solution: Optional<SolutionMembership?>.Of(newer))), view);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(new ProjectTeamSettingsChanged(
            view.Id, Now.AddMinutes(1), DomainId.New(), Solution: Optional<SolutionMembership?>.Of(Open(key)))), view);
        view.Solution!.Name.Should().Be("storefront-v2");

        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(new ProjectTeamSettingsChanged(
            view.Id, Now.AddMinutes(3), DomainId.New(), Solution: Optional<SolutionMembership?>.Of(null))), view);
        view.Solution.Should().BeNull();
    }
}
