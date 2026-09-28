using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k project members</c> and <c>h9k project member remove</c> (idea 202383dc, T1), against
/// real Marten/Postgres but a <see cref="FakeLedger"/>/<see cref="FakeLedgerChainReader"/> stand-in
/// for the ledger and the chain read (Brian's 2026-09-13 testing rule).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectMembersCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string MembersRefName = "refs/hall9k/ledger/members";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public ProjectMembersCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    private const string CollaboratorsJson =
        """[{"id":42,"login":"octocat","role_name":"write"},{"id":43,"login":"reader","role_name":"read"}]""";

    private static readonly Guid RootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Members_lists_the_chains_own_current_members()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-a"] = new("root-a", "ssh-ed25519 AAA root-a", []) },
            [new ProjectMember("root-a", MembershipRole.Owner, Now)]);

        (int exitCode, string output) = await RunMembersAsync(project, chain, GitHubAccessFakes.GrantingPush());

        exitCode.Should().Be(ExitCodes.Ok);
        output.Should().Contain("root-a");
    }

    [Fact]
    public async Task A_declared_account_with_push_in_the_roster_reads_declared_push_confirmed()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (_, string output) = await RunMembersAsync(
            project, ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat"))),
            GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        output.Should().Contain("octocat").And.Contain("declared, push confirmed");
        output.Should().NotContain("verified").And.NotContain("collaborator roster as of");
    }

    [Fact]
    public async Task A_declared_account_without_push_in_the_roster_reads_declared_read_only()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (_, string output) = await RunMembersAsync(
            project, ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(43, "reader"))),
            GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        output.Should().Contain("reader").And.Contain("declared, read only");
    }

    [Fact]
    public async Task A_declared_account_absent_from_the_roster_reads_declared_not_a_collaborator()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (_, string output) = await RunMembersAsync(
            project, ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(99, "stranger"))),
            GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        output.Should().Contain("stranger").And.Contain("declared, not a collaborator");
    }

    [Fact]
    public async Task A_node_without_push_holds_no_roster_so_a_declared_account_reads_declared_unchecked_here()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (_, string output) = await RunMembersAsync(
            project, ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat"))), GitHubAccessFakes.DenyingPush());

        output.Should().Contain("octocat").And.Contain("declared, unchecked here");
        output.Should().NotContain("collaborator roster as of", "gh answered, so the roster is not a stored fallback");
    }

    [Fact]
    public async Task A_member_whose_nodes_declare_nothing_reads_unknown_in_both_columns()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (_, string output) = await RunMembersAsync(
            project, ChainDeclaring(), GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        output.Should().Contain("unknown").And.NotContain("declared,");
    }

    [Fact]
    public async Task Every_distinct_declared_account_of_a_member_is_listed_with_its_own_standing()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (_, string output) = await RunMembersAsync(
            project,
            ChainDeclaring(
                (RootNodeId, new DeclaredGitHubAccount(42, "octocat")), (SecondNodeId, new DeclaredGitHubAccount(99, "work-account"))),
            GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        output.Should().Contain("octocat").And.Contain("work-account")
            .And.Contain("declared, push confirmed").And.Contain("declared, not a collaborator");
    }

    [Fact]
    public async Task The_cross_check_matches_on_account_id_first_and_login_second()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        // Id 42 is octocat in the roster whatever name the file declares (a rename); id 500 matches
        // nobody by id, so its login ("Reader", any casing) is what finds the collaborator.
        (_, string output) = await RunMembersAsync(
            project,
            ChainDeclaring(
                (RootNodeId, new DeclaredGitHubAccount(42, "octocat-renamed")), (SecondNodeId, new DeclaredGitHubAccount(500, "Reader"))),
            GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        output.Should().Contain("declared, push confirmed", "the id matched a collaborator with push").And.Contain("declared, read only", "the login matched the read-only collaborator");
        output.Should().NotContain("not a collaborator");
    }

    [Fact]
    public async Task When_gh_cannot_answer_the_stored_roster_is_used_and_dated_under_the_table()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        TrustChain chain = ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat")));
        await RunMembersAsync(project, chain, GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        (int exitCode, string output) = await RunMembersAsync(project, chain, GitHubAccessFakes.Unreachable());

        exitCode.Should().Be(ExitCodes.Ok);
        output.Should().Contain("declared, push confirmed", "the stored roster still lists the account with push");
        output.Should().MatchRegex(@"collaborator roster as of \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z");
    }

    [Fact]
    public async Task A_collaborator_removed_on_github_reads_not_a_collaborator_although_the_stored_mirror_still_holds_them()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        TrustChain chain = ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat")));
        await RunMembersAsync(project, chain, GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        (_, string output) = await RunMembersAsync(
            project, chain, GitHubAccessFakes.GrantingPush(collaboratorsJson: """[{"id":43,"login":"reader","role_name":"read"}]"""));

        output.Should().Contain("declared, not a collaborator").And.NotContain("push confirmed");
        output.Should().NotContain("collaborator roster as of", "the list was read fresh in this very run");
    }

    [Fact]
    public async Task When_gh_answers_but_the_collaborator_list_fails_the_stored_roster_is_dated_rather_than_called_fresh()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        TrustChain chain = ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat")));
        await RunMembersAsync(project, chain, GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        (_, string output) = await RunMembersAsync(project, chain, GitHubAccessFakes.GrantingPushWithFailingCollaboratorList());

        output.Should().MatchRegex(@"collaborator roster as of \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z");
    }

    [Fact]
    public async Task When_gh_cannot_answer_and_nothing_was_ever_stored_the_account_reads_unchecked_and_the_gap_is_said()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);

        (int exitCode, string output) = await RunMembersAsync(
            project, ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat"))), GitHubAccessFakes.Unreachable());

        exitCode.Should().Be(ExitCodes.Ok);
        output.Should().Contain("declared, unchecked here").And.Contain("collaborator roster unavailable");
    }

    [Fact]
    public async Task A_declared_display_name_appears_dimmed_under_the_root_fingerprint()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        TrustChain chain = ChainDeclaring((RootNodeId, new DeclaredGitHubAccount(42, "octocat"))) with
        {
            NodeDisplayNames = new Dictionary<string, NodeDisplayNameDeclaration>
            {
                [RootNodeId.ToString()] = new(RootNodeId.ToString(), "root-a", DisplayName.Parse("Ada Lovelace"), Now),
            },
        };

        (int exitCode, string output) = await RunMembersAsync(
            project, chain, GitHubAccessFakes.GrantingPush(collaboratorsJson: CollaboratorsJson));

        exitCode.Should().Be(ExitCodes.Ok);
        output.Should().Contain("root-a").And.Contain("Ada Lovelace").And.Contain("octocat");
    }

    [Fact]
    public void RenderRoot_appends_a_dimmed_line_when_a_name_is_declared() =>
        ProjectMembersCommand.RenderRoot("fingerprint-a", DisplayName.Parse("Ada Lovelace"))
            .Should().Be("fingerprint-a\n[dim]Ada Lovelace[/]");

    [Fact]
    public void RenderRoot_is_just_the_fingerprint_when_no_name_is_declared() =>
        ProjectMembersCommand.RenderRoot("fingerprint-a", DisplayName.None).Should().Be("fingerprint-a");

    /// <summary>
    /// A member's name comes from their own node file through <c>DisplayName.Trusted</c>, which
    /// skips <c>DisplayName.Parse</c>'s own control-character rule, so a self-signed rewrite of that
    /// file can carry a raw escape byte meant to repaint or overwrite this table's other rows
    /// (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public void RenderRoot_strips_control_characters_a_peer_authored_name_was_never_validated_against()
    {
        DisplayName peerAuthored = DisplayName.Trusted("Ada\u001b[2K\u001b[1ALovelace");

        string rendered = ProjectMembersCommand.RenderRoot("fingerprint-a", peerAuthored);

        rendered.Should().NotContain("\u001b").And.Contain("2K").And.Contain("1A").And.Contain("Lovelace");
    }

    [Fact]
    public void RenderRoot_bounds_the_length_of_a_peer_authored_name_that_bypassed_the_64_character_rule()
    {
        DisplayName peerAuthored = DisplayName.Trusted(new string('a', 500));

        string rendered = ProjectMembersCommand.RenderRoot("fingerprint-a", peerAuthored);

        rendered.Length.Should().BeLessThan(500);
    }

    private async Task<(int ExitCode, string Output)> RunMembersAsync(
        ProjectDetails project, TrustChain chain, ProjectGitHubAccessMirror mirror)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        int exitCode = 0;
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
            exitCode = await ProjectMembersCommand.RunAsync(
                session, new ProjectMembersCommand.Settings { Project = project.Name }, new FakeLedgerChainReader(chain),
                new ProjectGitHubRosterReader(mirror), CancellationToken.None));
        return (exitCode, output);
    }

    /// <summary>One owner root with its own node and a second vouched node, declaring the given accounts (keyed by node id, each signed by the node's own key).</summary>
    private static TrustChain ChainDeclaring(params (Guid NodeId, DeclaredGitHubAccount Account)[] declarations)
    {
        TrustedNode second = new(SecondNodeId.ToString(), "ssh-ed25519 AAAsecond second", "second-fingerprint", Now);
        TrustedOwner owner = new("root-a", "ssh-ed25519 AAA root-a", [second], RootNodeId: RootNodeId.ToString());
        return new TrustChain(
            new Dictionary<string, TrustedOwner> { ["root-a"] = owner },
            [new ProjectMember("root-a", MembershipRole.Owner, Now)])
        {
            NodeDeclarations = declarations.ToDictionary(
                declaration => declaration.NodeId.ToString(),
                declaration => new NodeGitHubDeclaration(
                    declaration.NodeId.ToString(),
                    declaration.NodeId == RootNodeId ? "root-a" : "second-fingerprint",
                    declaration.Account,
                    Now)),
        };
    }

    [Fact]
    public async Task Remove_refuses_before_any_push_when_this_owner_does_not_hold_the_owner_role()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);

        // This owner's own root exists in the chain, but only as a plain member, never owner.
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Member, Now)]));

        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = new string('a', 64) };
        Func<Task> act = () => ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*owner role*");
        ledger.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_refuses_before_any_push_when_this_nodes_key_is_not_a_live_root_key()
    {
        // idea 6be68ee2, trust-ledger finding 2: this node's own owner DOES hold the owner role,
        // but this node's own key is merely vouched into that owner's chain, never the owner's own
        // live root key — a member removal is a members-ref write, so it must refuse before any
        // push. Never checked against the identity fingerprint or TrustChain.RootNodeId (null on
        // an older ledger).
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();

        Guid nodeId;
        NodeSigningKey myKey;
        NodeSigningKey rootKey = await new NodeKeyStore().EnsureAsync(Guid.NewGuid(), CancellationToken.None);
        await using (IDocumentSession bootstrapSession = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None);
            await bootstrapSession.SaveChangesAsync(CancellationToken.None);
            nodeId = context.NodeId;
            myKey = await new NodeKeyStore().EnsureAsync(context.NodeId, CancellationToken.None);

            OwnerAggregate owner = await bootstrapSession.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: CancellationToken.None)
                ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
            bootstrapSession.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, rootKey.Fingerprint, verified: true, Now));
            await bootstrapSession.SaveChangesAsync(CancellationToken.None);
        }

        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine,
                    [new TrustedNode(nodeId.ToString(), myKey.PublicKeyLine, myKey.Fingerprint, Now)]),
            },
            [new ProjectMember(rootKey.Fingerprint, MembershipRole.Owner, Now)]));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = new string('a', 64) };
        Func<Task> act = () => ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*root key*");
        ledger.Deletes.Should().BeEmpty("this node is merely vouched, never a live root key, so nothing is pushed");
    }

    [Fact]
    public async Task Remove_deletes_the_member_file_when_this_owner_holds_the_owner_role()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();
        string targetFingerprint = new string('b', 64);
        await SeedMemberFileAsync(ledger, targetFingerprint);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = targetFingerprint };
        int exitCode = await ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        ledger.Deletes.Should().ContainSingle(d => d.RefName == MembersRefName && d.Path == $"members/{targetFingerprint}.yaml");

        ProjectDetails updated = (await session.LoadAsync<ProjectDetails>(project.Id, CancellationToken.None))!;
        updated.Members.Should().NotContainKey(targetFingerprint);
    }

    [Fact]
    public async Task Remove_refuses_a_fingerprint_that_is_not_currently_a_member()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = new string('c', 64) };
        Func<Task> act = () => ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>();
        ledger.Deletes.Should().BeEmpty();
    }

    private static async Task SeedMemberFileAsync(FakeLedger ledger, string fingerprint)
    {
        string content = $"root_fingerprint: \"{fingerprint}\"\nrole: \"owner\"\nissued_at: \"2026-09-01T00:00:00Z\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, MembersRefName, $"members/{fingerprint}.yaml", content, ExpectedBlobId: null,
                "seed member", new LedgerCommitter("Test", "test@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            CancellationToken.None);
    }

    private async Task<(string Fingerprint, string PublicKeyLine)> EstablishOwnRootAsync(
        IDocumentSession session, CancellationToken cancellationToken)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, key.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (key.Fingerprint, key.PublicKeyLine);
    }

    private async Task<ProjectDetails> SeedProjectAsync(CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, context.ConnectionId, "smoke", RepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }
}
