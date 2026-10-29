using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure parts of <c>h9k solution invite</c> and <c>h9k solution join</c>: the bundle, the
/// per-member plan, the mint loop, and the report. Nothing here opens a store, clones, creates a
/// branch, mints against GitHub, or touches a remote; the real mint is covered against the fake
/// ledger in <c>InviteCommandsTests</c>.
/// </summary>
public sealed class SolutionInviteJoinTests
{
    private const string ApiKey = "01JAAAAAAAAAAAAAAAAAAAAAAA";
    private const string WebKey = "01JBBBBBBBBBBBBBBBBBBBBBBB";
    private const string AdminKey = "01JCCCCCCCCCCCCCCCCCCCCCCC";
    private const string RootFingerprint = "SHA256:root";

    private static readonly Guid OwnerId = DomainId.New();

    private static readonly SolutionMember Api = new(ApiKey, "shop-api", new Uri("https://github.com/acme/shop-api.git"));
    private static readonly SolutionMember Web = new(WebKey, "shop-web", new Uri("https://github.com/acme/shop-web.git"));
    private static readonly SolutionMember Admin = new(AdminKey, "shop-admin", new Uri("ssh://git@github.com/acme/shop-admin.git"));

    private static string? RootOf(Guid ownerId) => ownerId == OwnerId ? RootFingerprint : null;

    private static ProjectDetails Project(string name, string? key, string? url, bool joined = true, bool archived = false) => new()
    {
        Id = DomainId.New(),
        OwnerId = OwnerId,
        Name = name,
        ProjectKey = key,
        RepositoryUrl = url is null ? null : new Uri(url),
        IsArchived = archived,
        Members = joined ? new Dictionary<string, ProjectMemberRole> { [RootFingerprint] = ProjectMemberRole.Owner } : [],
    };

    private static SolutionInviteBundle Bundle(params SolutionMember[] members) =>
        new(
            SolutionDecider.NewKey(),
            "storefront",
            [.. members.Select(member => new SolutionInviteMember(member, $"inv-{member.Name}", member == Web ? "develop" : null))]);

    // ---- the bundle ----

    [Fact]
    public void A_bundle_round_trips_as_one_line_carrying_the_solution_and_every_member_with_its_secret()
    {
        SolutionInviteBundle bundle = Bundle(Api, Web, Admin);

        string line = bundle.Encode();
        SolutionInviteBundle parsed = SolutionInviteBundle.Parse($"  {line}\n");

        line.Should().NotContainAny(" ", "\n", "\r", "\t");
        parsed.SolutionKey.Should().Be(bundle.SolutionKey);
        parsed.SolutionName.Should().Be("storefront");
        parsed.Members.Should().Equal(bundle.Members);
        parsed.Members.Select(entry => entry.BaseBranch).Should().Equal(null, "develop", null);
    }

    [Fact]
    public void A_bundle_that_is_not_a_bundle_is_refused_and_a_project_secret_is_pointed_at_project_join()
    {
        Action act = () => SolutionInviteBundle.Parse("invthbrpoigf3cbfnobm2o4rak3vrj");

        act.Should().Throw<DomainValidationException>().WithMessage("*not a solution invite bundle*h9k project join <project> --invite*");
    }

    [Fact]
    public void A_cut_short_or_damaged_bundle_is_refused_without_a_stack_trace()
    {
        string line = Bundle(Api).Encode();

        Action truncated = () => SolutionInviteBundle.Parse(line[..^7]);
        Action garbage = () => SolutionInviteBundle.Parse(line[..line.IndexOf('.')] + ".!!!not-base64!!!");

        truncated.Should().Throw<DomainValidationException>().WithMessage("*damaged or cut short*");
        garbage.Should().Throw<DomainValidationException>().WithMessage("*damaged or cut short*");
    }

    [Fact]
    public void A_bundle_member_with_no_secret_a_bad_key_or_a_repeat_is_refused()
    {
        SolutionInviteBundle noSecret = new(SolutionDecider.NewKey(), "storefront", [new SolutionInviteMember(Api, " ")]);
        SolutionInviteBundle badKey = new(SolutionDecider.NewKey(), "storefront", [new SolutionInviteMember(Api with { ProjectKey = "not-a-key" }, "s")]);
        SolutionInviteBundle repeated = new(SolutionDecider.NewKey(), "storefront", [new SolutionInviteMember(Api, "a"), new SolutionInviteMember(Api, "b")]);
        SolutionInviteBundle empty = new(SolutionDecider.NewKey(), "storefront", []);

        ((Action)(() => SolutionInviteBundle.Parse(noSecret.Encode()))).Should().Throw<DomainValidationException>().WithMessage("*carries no secret*");
        ((Action)(() => SolutionInviteBundle.Parse(badKey.Encode()))).Should().Throw<DomainValidationException>().WithMessage("*not a project key*");
        ((Action)(() => SolutionInviteBundle.Parse(repeated.Encode()))).Should().Throw<DomainValidationException>().WithMessage("*listed twice*");
        ((Action)(() => SolutionInviteBundle.Parse(empty.Encode()))).Should().Throw<DomainValidationException>().WithMessage("*at least one project*");
    }

    // ---- the per-member plan ----

    [Fact]
    public void The_plan_registers_a_member_nothing_local_matches_and_joins_one_that_is_found_but_not_joined()
    {
        ProjectDetails unjoinedWeb = Project("web-checkout", key: null, "https://github.com/ACME/shop-web", joined: false);
        SolutionInviteBundle bundle = Bundle(Api, Web);

        IReadOnlyList<SolutionJoinStep> plan = SolutionJoinPlanner.Plan(bundle, [unjoinedWeb], RootOf);

        plan.Select(step => step.Action).Should().Equal(SolutionJoinAction.Register, SolutionJoinAction.Join);
        plan[0].Local.Should().BeNull();
        plan[1].Local.Should().BeSameAs(unjoinedWeb, "an unjoined project has no key yet, so the repository URL (host, path and case aside) finds it");
    }

    [Fact]
    public void The_plan_leaves_a_member_already_joined_alone_finding_it_by_key_before_url()
    {
        ProjectDetails byKey = Project("api-renamed", ApiKey, "https://example.com/elsewhere.git");
        ProjectDetails sameUrlOtherName = Project("decoy", key: null, Api.RepositoryUrl.ToString(), joined: false);

        IReadOnlyList<SolutionJoinStep> plan = SolutionJoinPlanner.Plan(Bundle(Api), [sameUrlOtherName, byKey], RootOf);

        plan.Single().Action.Should().Be(SolutionJoinAction.AlreadyJoined);
        plan.Single().Local.Should().BeSameAs(byKey);
    }

    [Fact]
    public void The_plan_matches_one_repository_across_ssh_and_https_and_a_dot_git_suffix()
    {
        ProjectDetails https = Project("admin", key: null, "https://github.com/acme/shop-admin", joined: false);

        SolutionJoinPlanner.Plan(Bundle(Admin), [https], RootOf).Single().Action.Should().Be(SolutionJoinAction.Join);
    }

    [Fact]
    public void The_plan_names_a_collision_when_another_local_project_holds_the_members_name_archived_or_not()
    {
        ProjectDetails other = Project("Shop-API", ApiKey.Replace('A', 'Z'), "https://github.com/acme/other.git");
        ProjectDetails archivedWeb = Project("shop-web", key: null, "https://github.com/acme/unrelated.git", joined: false, archived: true);

        IReadOnlyList<SolutionJoinStep> plan = SolutionJoinPlanner.Plan(Bundle(Api, Web), [other, archivedWeb], RootOf);

        plan.Select(step => step.Action).Should().Equal(SolutionJoinAction.NameCollision, SolutionJoinAction.NameCollision);
        plan[0].Local.Should().BeSameAs(other);
        plan[1].Local.Should().BeSameAs(archivedWeb);
    }

    [Fact]
    public void An_archived_project_is_never_the_member_even_when_its_key_matches()
    {
        ProjectDetails archived = Project("old-api", ApiKey, Api.RepositoryUrl.ToString(), archived: true);

        SolutionJoinPlanner.Plan(Bundle(Api), [archived], RootOf).Single().Action.Should().Be(SolutionJoinAction.Register);
    }

    // ---- the runner, the report and the exit code, over faked outcomes ----

    [Fact]
    public async Task A_member_that_fails_never_stops_the_others_and_nothing_is_done_for_one_already_joined()
    {
        ProjectDetails joined = Project("shop-admin", AdminKey, Admin.RepositoryUrl.ToString());
        ProjectDetails unjoined = Project("shop-web", key: null, Web.RepositoryUrl.ToString(), joined: false);
        IReadOnlyList<SolutionJoinStep> plan = SolutionJoinPlanner.Plan(Bundle(Api, Web, Admin), [joined, unjoined], RootOf);
        List<string> executed = [];

        IReadOnlyList<SolutionJoinOutcome> outcomes = await SolutionJoinRunner.RunAsync(
            plan,
            (step, _) =>
            {
                executed.Add(step.Entry.Member.Name);
                return step.Entry.Member == Api
                    ? throw new DomainValidationException("no push on the repository")
                    : Task.FromResult(SolutionJoinOutcome.Joined(step.Entry.Member));
            },
            CancellationToken.None);

        executed.Should().Equal("shop-api", "shop-web");
        outcomes.Select(outcome => outcome.State).Should().Equal(
            SolutionJoinState.NotFinished, SolutionJoinState.Joined, SolutionJoinState.AlreadyJoined);
        outcomes[0].Reason.Should().Be("no push on the repository");
        outcomes[0].HandCommand.Should().Be(SolutionJoinHandCommands.Retry()).And.NotContain("<secret>");
    }

    [Fact]
    public async Task A_join_that_throws_is_retried_through_the_bundle_and_a_collision_asks_for_the_holder_to_be_renamed()
    {
        ProjectDetails unjoined = Project("web checkout", key: null, Web.RepositoryUrl.ToString(), joined: false);
        ProjectDetails holder = Project("shop-api", "01JZZZZZZZZZZZZZZZZZZZZZZZ", "https://github.com/acme/other.git");
        IReadOnlyList<SolutionJoinStep> plan = SolutionJoinPlanner.Plan(Bundle(Api, Web), [unjoined, holder], RootOf);

        IReadOnlyList<SolutionJoinOutcome> outcomes = await SolutionJoinRunner.RunAsync(
            plan, (_, _) => throw new InvalidOperationException("secret already spent"), CancellationToken.None);

        outcomes[0].State.Should().Be(SolutionJoinState.NotFinished);
        outcomes[0].Reason.Should().Contain("already has a project named 'shop-api'");
        outcomes[0].HandCommand.Should().StartWith("h9k project rename shop-api <new-name>, then h9k solution join --invite -");
        outcomes[1].Reason.Should().Be("secret already spent");
        outcomes[1].HandCommand.Should().Be(SolutionJoinHandCommands.Retry());
        SolutionJoinHandCommands.InitThenRetry("web checkout").Should().StartWith("h9k project init \"web checkout\", then h9k solution join --invite -");
    }

    [Fact]
    public void The_report_gives_every_member_one_state_names_each_unfinished_one_and_says_the_daemon_vouches()
    {
        SolutionJoinOutcome[] outcomes =
        [
            SolutionJoinOutcome.RegisteredAndJoined(Api),
            SolutionJoinOutcome.Joined(Web),
            SolutionJoinOutcome.AlreadyJoined(Admin),
            SolutionJoinOutcome.NotFinished(
                new SolutionMember(WebKey, "shop-pay", new Uri("https://github.com/acme/shop-pay.git")),
                "could not clone https://github.com/acme/shop-pay.git: Permission denied", SolutionJoinHandCommands.InitThenRetry("shop-pay")),
        ];

        string report = string.Join("\n", SolutionJoinReport.Lines("storefront", outcomes));

        report.Should().Contain("registered and joined[/]: shop-api")
            .And.Contain("joined[/]: shop-web")
            .And.Contain("already joined[/]: shop-admin")
            .And.Contain("not finished[/]: shop-pay")
            .And.Contain("why: could not clone https://github.com/acme/shop-pay.git: Permission denied")
            .And.Contain("finish it with: h9k project init shop-pay, then h9k solution join --invite -")
            .And.Contain("1 of 4 did not finish")
            .And.Contain("vouched in by the minting node's daemon on its next sweep")
            .And.Contain("push access");
        report.Should().NotContain("inv-", "the report never prints a secret").And.NotContain("<secret>");
        outcomes.Select(outcome => SolutionJoinReport.Label(outcome.State)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_exit_code_is_non_zero_exactly_when_a_member_did_not_finish()
    {
        SolutionJoinReport.ExitCode([SolutionJoinOutcome.Joined(Api), SolutionJoinOutcome.AlreadyJoined(Web)]).Should().Be(ExitCodes.Ok);
        SolutionJoinReport.ExitCode([SolutionJoinOutcome.RegisteredAndJoined(Api)]).Should().Be(ExitCodes.Ok);
        SolutionJoinReport.ExitCode(
            [SolutionJoinOutcome.Joined(Api), SolutionJoinOutcome.NotFinished(Web, "why", "how")]).Should().NotBe(ExitCodes.Ok);
    }

    // ---- the mint loop over a faked mint (the real mint is covered in InviteCommandsTests) ----

    [Fact]
    public async Task The_mint_loop_mints_for_each_held_member_and_lists_each_refusal_with_its_reason()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api, Web, Admin]);
        ProjectDetails api = Project("shop-api", ApiKey, Api.RepositoryUrl.ToString());
        api.BaseBranch = "develop";
        ProjectDetails web = Project("shop-web", WebKey, Web.RepositoryUrl.ToString());
        DateTimeOffset expires = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        SolutionMintResult result = await SolutionInviteMinter.MintAsync(
            membership,
            [api, web],
            RootOf,
            (project, _) => project.Id == web.Id
                ? throw new DomainValidationException("does not currently hold the owner role in 'shop-web'")
                : Task.FromResult(new ProjectInviteCommand.MintedInvite("secret-api", expires, ProjectMemberRole.Member)),
            CancellationToken.None);

        result.Minted.Select(minted => (minted.Member.Name, minted.Invite.Secret)).Should().Equal(("shop-api", "secret-api"));
        result.Refused.Select(refusal => refusal.Member.Name).Should().Equal("shop-web", "shop-admin");
        result.Refused[0].Reason.Should().Contain("owner role");
        result.Refused[1].Reason.Should().Contain("not registered and joined on this node");

        SolutionInviteBundle bundle = SolutionInviteBundle.Parse(result.ToBundle(membership).Encode());
        bundle.Members.Select(entry => entry.Member.Name).Should().Equal("shop-api");
        bundle.Members.Single().Secret.Should().Be("secret-api");
        bundle.Members.Single().BaseBranch.Should().Be("develop", "the joining node registers the member on the branch it is based on here");
    }

    [Fact]
    public async Task A_member_whose_project_is_registered_but_not_joined_here_is_not_minted_for()
    {
        SolutionMembership membership = SolutionDecider.Create(SolutionDecider.NewKey(), "storefront", [Api]);
        bool minted = false;

        SolutionMintResult result = await SolutionInviteMinter.MintAsync(
            membership,
            [Project("shop-api", ApiKey, Api.RepositoryUrl.ToString(), joined: false)],
            RootOf,
            (_, _) =>
            {
                minted = true;
                return Task.FromResult(new ProjectInviteCommand.MintedInvite("s", DateTimeOffset.UtcNow, ProjectMemberRole.Member));
            },
            CancellationToken.None);

        minted.Should().BeFalse();
        result.Minted.Should().BeEmpty();
        result.Refused.Should().ContainSingle();
    }
}
