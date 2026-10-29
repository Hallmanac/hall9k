using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class SolutionDeciderTests
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string AdminKey = "01JCCCCCCCCCCCCCCCCCCCCCCC";
    private const string RootFingerprint = "SHA256:root";

    private static readonly Guid OwnerId = DomainId.New();

    private static ProjectDetails Project(string name, string? projectKey, string? url, bool joined = true, SolutionMembership? solution = null) => new()
    {
        Id = DomainId.New(),
        OwnerId = OwnerId,
        Name = name,
        ProjectKey = projectKey,
        RepositoryUrl = url is null ? null : new Uri(url),
        Solution = solution,
        Members = joined ? new Dictionary<string, ProjectMemberRole> { [RootFingerprint] = ProjectMemberRole.Owner } : [],
    };

    private static string? RootOf(Guid ownerId) => ownerId == OwnerId ? RootFingerprint : null;

    private static SolutionMember Member(string key, string name, string url) => new(key, name, new Uri(url));

    private static readonly SolutionMember Api = Member(ApiKey, "shop-api", "https://github.com/acme/shop-api.git");
    private static readonly SolutionMember Web = Member(WebKey, "shop-web", "https://github.com/acme/shop-web.git");

    [Fact]
    public void MemberOf_refuses_a_project_with_no_recorded_key_naming_join_and_assign_key()
    {
        Action act = () => SolutionDecider.MemberOf(Project("shop-api", null, "https://github.com/acme/shop-api.git"));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*no recorded project key*h9k project join shop-api*h9k project assign-key shop-api*");
    }

    [Fact]
    public void MemberOf_refuses_a_project_with_no_recorded_repository_url()
    {
        Action act = () => SolutionDecider.MemberOf(Project("shop-api", ApiKey, null));

        act.Should().Throw<DomainValidationException>().WithMessage("*no recorded repository URL*--repo-url*");
    }

    [Fact]
    public void MemberOf_names_the_project_by_its_key_name_and_url()
    {
        SolutionMember member = SolutionDecider.MemberOf(Project("shop-api", ApiKey, "https://github.com/acme/shop-api.git"));

        member.Should().Be(Api);
    }

    [Fact]
    public void Every_member_not_registered_and_joined_here_is_named_in_the_refusal()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);
        ProjectDetails[] projects = [Project("shop-api", ApiKey, Api.RepositoryUrl.ToString()), Project("shop-web", WebKey, Web.RepositoryUrl.ToString(), joined: false)];

        SolutionDecider.MembersNotHeldHere(membership, projects, RootOf).Should().Equal(Web);

        Action act = () => SolutionDecider.RequireEveryMemberHeld(membership, projects, RootOf, "rename");
        act.Should().Throw<DomainConflictException>()
            .WithMessage("*Cannot rename the solution 'storefront'*'shop-web' (https://github.com/acme/shop-web.git) is not registered and joined on this node*");

        // A member with no registered project here is not held either.
        SolutionDecider.MembersNotHeldHere(membership, [], RootOf).Should().Equal(Api, Web);
    }

    [Fact]
    public void A_wholly_held_solution_passes_the_held_check()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);
        ProjectDetails[] projects = [Project("shop-api", ApiKey, Api.RepositoryUrl.ToString()), Project("shop-web", WebKey, Web.RepositoryUrl.ToString())];

        Action act = () => SolutionDecider.RequireEveryMemberHeld(membership, projects, RootOf, "close");

        act.Should().NotThrow();
    }

    [Fact]
    public void A_new_member_whose_owner_differs_from_every_existing_member_is_a_mismatch()
    {
        SolutionMember outsider = Member(AdminKey, "other-admin", "https://github.com/other-org/other-admin.git");

        IReadOnlyList<SolutionOwnerMismatch> mismatches = SolutionDecider.OwnerMismatches([Api, Web], [outsider]);

        SolutionOwnerMismatch mismatch = mismatches.Should().ContainSingle().Subject;
        mismatch.Candidate.Should().Be(outsider);
        mismatch.CandidateOwner.Should().Be("github.com/other-org");
        mismatch.ExistingOwners.Should().Equal("github.com/acme");
    }

    [Fact]
    public void A_new_member_sharing_the_owner_of_any_existing_member_is_not_a_mismatch_whatever_the_url_form_or_case()
    {
        SolutionMember sameOwnerSsh = Member(AdminKey, "shop-admin", "ssh://git@github.com/ACME/shop-admin.git");
        SolutionMember otherOwner = Member("01JDDDDDDDDDDDDDDDDDDDDDDD", "elsewhere", "https://github.com/zeta/elsewhere.git");

        SolutionDecider.OwnerMismatches([Api], [sameOwnerSsh]).Should().BeEmpty();
        SolutionDecider.OwnerMismatches([Api, otherOwner], [sameOwnerSsh]).Should().BeEmpty("it matches one existing member, and the rule is differs from every one");
    }

    [Fact]
    public void Adding_checks_each_member_against_the_ones_before_it_and_the_first_has_nothing_to_differ_from()
    {
        SolutionMember outsider = Member(AdminKey, "other-admin", "https://github.com/other-org/other-admin.git");

        IReadOnlyList<SolutionOwnerMismatch> mismatches = SolutionDecider.OwnerMismatches([], [Api, Web, outsider]);

        mismatches.Select(mismatch => mismatch.Candidate.Name).Should().Equal("other-admin");
    }

    [Fact]
    public void The_same_owner_name_on_a_different_host_is_a_different_owner()
    {
        SolutionDecider.RepositoryOwner(new Uri("https://github.com/acme/x.git")).Should().Be("github.com/acme");
        SolutionDecider.RepositoryOwner(new Uri("https://gitlab.com/acme/x.git")).Should().Be("gitlab.com/acme");
        SolutionDecider.RepositoryOwner(new Uri("https://github.com/")).Should().BeNull();
    }

    [Fact]
    public void Link_adds_a_member_and_refuses_a_closed_solution_and_a_repeat()
    {
        SolutionMembership solution = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api]);
        SolutionMember admin = Member(AdminKey, "shop-admin", "https://github.com/acme/shop-admin.git");

        SolutionDecider.Link(solution, admin).Members.Should().Equal(Api, admin);
        Action repeat = () => SolutionDecider.Link(solution, Api);
        Action closed = () => SolutionDecider.Link(SolutionDecider.Close(solution, "retired"), admin);

        repeat.Should().Throw<DomainConflictException>().WithMessage("*already in the solution*");
        closed.Should().Throw<DomainConflictException>().WithMessage("*is closed (retired)*final*");
    }

    [Fact]
    public void Unlink_removes_a_member_refuses_a_stranger_and_refuses_the_last_member()
    {
        SolutionMembership solution = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);

        SolutionMembership remaining = SolutionDecider.Unlink(solution, WebKey);
        remaining.Members.Should().Equal(Api);

        Action stranger = () => SolutionDecider.Unlink(solution, AdminKey);
        Action last = () => SolutionDecider.Unlink(remaining, ApiKey);
        stranger.Should().Throw<DomainValidationException>().WithMessage("*not a member*shop-api, shop-web*");
        last.Should().Throw<DomainValidationException>().WithMessage("*last member*h9k solution close*");
    }

    [Fact]
    public void A_closed_solution_is_final_so_unlink_and_rename_refuse_it_the_way_link_and_close_do()
    {
        SolutionMembership closed = SolutionDecider.Close(
            SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]), "retired");

        Action unlink = () => SolutionDecider.Unlink(closed, WebKey);
        Action rename = () => SolutionDecider.Rename(closed, "storefront-v2");

        unlink.Should().Throw<DomainConflictException>().WithMessage("*is closed (retired)*final*unlink a project from*");
        rename.Should().Throw<DomainConflictException>().WithMessage("*is closed (retired)*final*be renamed*");
    }

    [Fact]
    public void A_rewrite_is_refused_when_a_held_member_no_longer_carries_the_solution()
    {
        SolutionMembership storefront = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web]);
        SolutionMembership marketplace = SolutionDecider.Create(SolutionDecider.NewKey(), "marketplace", [Web]);
        ProjectDetails api = Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: storefront);
        ProjectDetails web = Project("shop-web", WebKey, Web.RepositoryUrl.ToString(), solution: marketplace);
        ProjectDetails bare = Project("shop-web", WebKey, Web.RepositoryUrl.ToString());

        SolutionView view = SolutionRegistry.Read([api, web]).Single(solution => solution.Membership.Key == storefront.Key);
        Action elsewhere = () => SolutionDecider.RequireConsistent(view, [api, web], RootOf, "unlink a project from");
        Action cleared = () => SolutionDecider.RequireConsistent(view, [api, bare], RootOf, "rename");

        elsewhere.Should().Throw<DomainConflictException>()
            .WithMessage("*Cannot unlink a project from the solution 'storefront'*'shop-web' (carries 'marketplace')*no longer carries it*");
        cleared.Should().Throw<DomainConflictException>().WithMessage("*Cannot rename*'shop-web'*no longer carries it*");
    }

    [Fact]
    public void A_rewrite_is_refused_while_a_carrier_holds_a_different_copy_and_passes_when_they_all_agree()
    {
        string key = SolutionDecider.NewKey();
        SolutionMembership original = SolutionDecider.Create(key, "storefront", [Api, Web]);
        SolutionMembership forged = SolutionDecider.Close(original, "forged");
        ProjectDetails api = Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: forged);
        api.TeamSettingStamps[nameof(ProjectDetails.Solution)] = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ProjectDetails web = Project("shop-web", WebKey, Web.RepositoryUrl.ToString(), solution: original);

        SolutionView disagreeing = SolutionRegistry.Read([api, web]).Single();
        Action refuse = () => SolutionDecider.RequireConsistent(disagreeing, [api, web], RootOf, "rename");

        disagreeing.Inconsistent.Select(project => project.Name).Should().Equal("shop-web");
        refuse.Should().Throw<DomainConflictException>().WithMessage("*'shop-web' carries a different record*");

        // The same record read back from a stream is a different object with a different member list instance.
        ProjectDetails agreeingWeb = Project("shop-web", WebKey, Web.RepositoryUrl.ToString(), solution: original with { Members = [.. original.Members] });
        ProjectDetails agreeingApi = Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: original);
        SolutionView agreeing = SolutionRegistry.Read([agreeingApi, agreeingWeb]).Single();
        SolutionDecider.RequireConsistent(agreeing, [agreeingApi, agreeingWeb], RootOf, "rename");
        agreeing.Inconsistent.Should().BeEmpty();
    }

    [Fact]
    public void A_project_the_record_does_not_list_never_speaks_for_the_solution()
    {
        string key = SolutionDecider.NewKey();
        SolutionMembership genuine = SolutionDecider.Create(key, "storefront", [Api, Web]);
        SolutionMembership forged = SolutionDecider.Create(key, "hijacked", [Member(AdminKey, "shop-admin", "https://github.com/acme/shop-admin.git")]);
        ProjectDetails api = Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: genuine);
        ProjectDetails stranger = Project("stranger", "01JDDDDDDDDDDDDDDDDDDDDDDD", "https://github.com/acme/stranger.git", solution: forged);
        stranger.TeamSettingStamps[nameof(ProjectDetails.Solution)] = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        SolutionView view = SolutionRegistry.Read([api, stranger]).Should().ContainSingle().Subject;

        view.Membership.Name.Should().Be("storefront");
        view.Inconsistent.Select(project => project.Name).Should().Equal("stranger");
        SolutionRegistry.Read([stranger]).Should().BeEmpty("a record that no carrier is listed in vouches for nothing");
    }

    [Fact]
    public void Close_is_an_explicit_act_with_a_reason_and_is_terminal()
    {
        SolutionMembership solution = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api]);

        SolutionMembership closed = SolutionDecider.Close(solution, " Retired ");
        closed.State.Should().Be(SolutionState.Closed);
        closed.CloseReason.Should().Be("Retired");
        closed.Holds.Should().BeFalse();

        Action noReason = () => SolutionDecider.Close(solution, "");
        Action twice = () => SolutionDecider.Close(closed, "again");
        noReason.Should().Throw<DomainValidationException>().WithMessage("*reason*");
        twice.Should().Throw<DomainConflictException>().WithMessage("*is closed*");
    }

    [Fact]
    public void A_solution_name_is_unique_case_insensitively_among_every_solution_closed_ones_included()
    {
        SolutionMembership storefront = SolutionDecider.Close(SolutionDecider.Create(SolutionDecider.NewKey(), "Storefront", [Api]), "retired");
        IReadOnlyList<SolutionView> solutions = SolutionRegistry.Read([Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: storefront)]);

        Action collide = () => SolutionRegistry.RequireUniqueName(solutions, "storefront", ownKey: null);
        Action renameToSelf = () => SolutionRegistry.RequireUniqueName(solutions, "STOREFRONT", storefront.Key);
        Action blank = () => SolutionRegistry.RequireUniqueName(solutions, " ", null);

        collide.Should().Throw<DomainConflictException>().WithMessage("*already exists*");
        renameToSelf.Should().NotThrow();
        blank.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_solution_resolves_by_name_or_key_prefix_and_failures_say_what_exists()
    {
        SolutionMembership storefront = SolutionDecider.Create("01JZZZZZZZZZZZZZZZZZZZZZZ1", "storefront", [Api]);
        SolutionMembership marketplace = SolutionDecider.Create("01JZZZZZZZZZZZZZZZZZZZZZZ2", "marketplace", [Web]);
        IReadOnlyList<SolutionView> solutions = SolutionRegistry.Read([
            Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: storefront),
            Project("shop-web", WebKey, Web.RepositoryUrl.ToString(), solution: marketplace)]);

        SolutionRegistry.Match(solutions, "STOREFRONT").Membership.Should().Be(storefront);
        SolutionRegistry.Match(solutions, "01JZZZZZZZZZZZZZZZZZZZZZZ2").Membership.Should().Be(marketplace);
        Action ambiguous = () => SolutionRegistry.Match(solutions, "01JZZZ");
        Action missing = () => SolutionRegistry.Match(solutions, "nothing");
        Action none = () => SolutionRegistry.Match([], "storefront");

        ambiguous.Should().Throw<DomainConflictException>().WithMessage("*ambiguous*marketplace*storefront*");
        missing.Should().Throw<DomainNotFoundException>().WithMessage("*Known: marketplace, storefront*h9k solution list*");
        none.Should().Throw<DomainNotFoundException>().WithMessage("*h9k solution add*");
    }

    [Fact]
    public void The_registry_reads_the_freshest_copy_when_members_briefly_disagree()
    {
        string key = SolutionDecider.NewKey();
        SolutionMembership old = SolutionDecider.Create(key, "storefront", [Api, Web]);
        SolutionMembership renamed = SolutionDecider.Rename(old, "storefront-v2");
        ProjectDetails api = Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), solution: renamed);
        api.TeamSettingStamps[nameof(ProjectDetails.Solution)] = new DateTimeOffset(2026, 10, 29, 12, 5, 0, TimeSpan.Zero);
        ProjectDetails web = Project("shop-web", WebKey, Web.RepositoryUrl.ToString(), solution: old);
        web.TeamSettingStamps[nameof(ProjectDetails.Solution)] = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        SolutionView view = SolutionRegistry.Read([web, api]).Should().ContainSingle().Subject;

        view.Membership.Name.Should().Be("storefront-v2");
        view.HeldProjects.Select(project => project.Name).Should().Equal("shop-api", "shop-web");
    }
}
