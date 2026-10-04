using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
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
/// A member lays hold of an idea before any task exists, and only its assignee, or its creator when it
/// has none, decides its fate (card D of idea 8d0b724b), against the real store and a fake ledger chain
/// for the Owner-role override. The pure halves are in <c>IdeaAssigneeTests</c> and
/// <c>EventReplicationInboxIdeaActGateTests</c>; this proves the commands gather the facts, refuse in
/// the receive gate's own words, and stamp the events.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class IdeaAssigneeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string RyanRoot = new('2', 64);
    private static readonly string TaylorRoot = new('3', 64);
    private static readonly string OtherFleetNodeOwnerRoot = new('4', 64);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private BootstrapContext _me = null!;
    private string _myRoot = string.Empty;
    private OwnerDetailsSeed _ryan = null!;
    private OwnerDetailsSeed _taylor = null!;
    private Guid _projectId;

    public IdeaAssigneeCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            _me = await NodeBootstrap.EnsureAsync(session, cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        // The root is this node's own key, as in h9k project join's root node, so an Owner-role chain
        // built from it passes the same enrolment check a real override does.
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(_me.NodeId, cts.Token);
        _myRoot = key.Fingerprint;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(_me.OwnerId, token: cts.Token))!;
            session.Events.Append(_me.OwnerId, OwnerDecider.ClaimRoot(owner, _myRoot, verified: true, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        _ryan = await SeedOwnerAsync("Ryan", RyanRoot, cts.Token);
        _taylor = await SeedOwnerAsync("Taylor", TaylorRoot, cts.Token);
        _projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(new ProjectDetails
        {
            Id = _projectId,
            Name = "moveproj",
            RepositoryPath = "/does/not/matter",
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await projectSession.SaveChangesAsync(cts.Token);
    }

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    private OwnerDetailsSeed Me => new(_me.OwnerId, "Me", _myRoot);

    [Fact]
    public async Task An_assignee_hands_the_idea_to_another_member_whoever_captured_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, heldBy: Me, replicatedCreatorRoot: RyanRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskOwnerOverrideDecision decision = await AuthorizeAsync(session, ideaId, "assign", cts.Token);
        IdeaAssigneeSet? set = await AppendAsync(session, ideaId, _taylor, decision, cts.Token);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.OwnAct);
        set.Should().NotBeNull();
        set!.AssigneeOwnerRootFingerprint.Should().Be(TaylorRoot);
        await using IQuerySession query = _postgres.Store.QuerySession();
        IdeaAggregate idea = (await query.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        idea.AssigneeOwnerId.Should().Be(_taylor.Id);
        idea.State.Should().Be(IdeaState.Captured);
        (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token))!.AssigneeOwnerFingerprint.Should().Be(TaylorRoot);
    }

    [Fact]
    public async Task A_creator_assigns_an_unassigned_idea_to_a_teammate_and_then_no_longer_decides_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskOwnerOverrideDecision decision = await AuthorizeAsync(session, ideaId, "assign", cts.Token);
            (await AppendAsync(session, ideaId, _ryan, decision, cts.Token)).Should().NotBeNull();
            decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.OwnAct, "an unassigned idea is its creator's to hand on");
        }

        await using IDocumentSession guardSession = _postgres.Store.LightweightSession();
        IdeaAggregate idea = (await guardSession.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        (await IdeaOwnerGuard.MayActAsync(guardSession, idea, _me, new FakeLedgerChainReader(TrustChain.Empty), cts.Token))
            .Should().BeFalse("the creator handed it away, so it is Ryan's now, by the rule the receive gate applies");
    }

    [Fact]
    public async Task A_member_taking_a_teammates_unassigned_idea_for_themselves_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> take = () => AuthorizeAsync(session, ideaId, "assign", cts.Token);

        (await take.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().StartWith("Idea ").And.Contain("belongs to").And.Contain("Owner-role member")
            .And.Contain("--holder").And.Contain("--reason");
    }

    [Fact]
    public async Task A_non_owner_member_naming_someone_else_is_refused_and_so_is_one_handing_on_a_held_idea()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid unassigned = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);
        Guid heldByTaylor = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _taylor);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> namesTaylor = () => AuthorizeAsync(session, unassigned, "assign", cts.Token);
        Func<Task> creatorHandsOnAHeldIdea = () => AuthorizeAsync(session, heldByTaylor, "assign", cts.Token);

        await namesTaylor.Should().ThrowAsync<DomainBusinessRuleException>();
        (await creatorHandsOnAHeldIdea.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain(TaylorRoot[..12], "the refusal names whose idea it is");
    }

    [Fact]
    public async Task A_hand_off_of_a_fleet_scoped_idea_to_a_teammate_is_refused_and_names_idea_share()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, share: false);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> handOff = () => AppendAsync(session, ideaId, _ryan, TaskOwnerOverrideDecision.OwnAct, cts.Token);

        (await handOff.Should().ThrowAsync<DomainConflictException>()).Which.Message.Should().Contain("h9k idea share");
        (await AppendAsync(session, ideaId, Me, TaskOwnerOverrideDecision.OwnAct, cts.Token))
            .Should().NotBeNull("laying hold of it for yourself needs no sharing");
    }

    [Fact]
    public async Task A_member_cannot_be_named_when_this_node_has_no_root_for_them()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token);
        OwnerDetailsSeed rootless = await SeedOwnerAsync("Rootless", root: null, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> handOff = () => AppendAsync(session, ideaId, rootless, TaskOwnerOverrideDecision.OwnAct, cts.Token);

        (await handOff.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Contain("no root fingerprint");
    }

    [Fact]
    public async Task An_owner_role_override_assigns_an_idea_another_member_holds_and_records_whose_behalf_and_why()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        TaskOwnerOverrideDecision decision = await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, _me, "assign", RyanRoot[..8], "Ryan is out this week", await ChainAsync(MembershipRole.Owner, cts.Token),
            new NodeKeyStore(), cts.Token);
        IdeaAssigneeSet? set = await AppendAsync(session, ideaId, Me, decision, cts.Token);

        decision.Outcome.Should().Be(TaskOwnerOverrideOutcome.Override);
        set!.OnBehalfOfOwnerRootFingerprint.Should().Be(RyanRoot);
        set.OverrideReason.Should().Be("Ryan is out this week");
        set.AssigneeOwnerRootFingerprint.Should().Be(_myRoot);
    }

    [Fact]
    public async Task A_member_role_node_cannot_override_even_when_it_names_the_holder()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        Func<Task> act = async () => await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, _me, "assign", RyanRoot[..8], "tidying", await ChainAsync(MembershipRole.Member, cts.Token),
            new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message.Should().Contain("Owner role");
    }

    [Fact]
    public async Task An_idea_with_no_project_has_no_role_to_override_with_and_says_how_to_get_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan, projectId: Guid.Empty);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        Func<Task> act = async () => await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, _me, "conclude", RyanRoot[..8], "tidying", await ChainAsync(MembershipRole.Owner, cts.Token),
            new NodeKeyStore(), cts.Token);

        (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("belongs to no project").And.Contain("h9k idea move");
    }

    [Fact]
    public async Task Only_the_assignee_may_let_go_of_an_idea_and_an_owner_role_override_needs_the_holder_and_a_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid heldByMe = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, heldBy: Me, replicatedCreatorRoot: RyanRoot);
        Guid heldByRyan = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await UnassignAsync(session, heldByMe, holder: null, reason: null, MembershipRole.Member, cts.Token)).Should().Be(ExitCodes.Ok);
        (await session.Events.AggregateStreamAsync<IdeaAggregate>(heldByMe, token: cts.Token))!
            .AssigneeOwnerId.Should().BeNull();

        Func<Task> creatorLetsGo = () => UnassignAsync(session, heldByRyan, holder: null, reason: null, MembershipRole.Owner, cts.Token);
        (await creatorLetsGo.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("--holder").And.Contain("--reason");

        int overridden = await UnassignAsync(session, heldByRyan, RyanRoot[..8], "Ryan left the team", MembershipRole.Owner, cts.Token);
        overridden.Should().Be(ExitCodes.Ok);
        IdeaAssigneeCleared cleared = (await session.Events.FetchStreamAsync(heldByRyan, token: cts.Token))
            .Select(e => e.Data).OfType<IdeaAssigneeCleared>().Single();
        cleared.OnBehalfOfOwnerRootFingerprint.Should().Be(RyanRoot);
        cleared.OverrideReason.Should().Be("Ryan left the team");
    }

    [Fact]
    public async Task Unassigning_an_idea_nobody_holds_refuses_with_the_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> act = () => UnassignAsync(session, ideaId, holder: null, reason: null, MembershipRole.Member, cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>()).Which.Message.Should().Contain("no assignee");
    }

    [Fact]
    public async Task Assigning_names_a_person_and_a_project_name_or_the_project_option_is_refused_naming_idea_move()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> byName = () => IdeaAssignCommand.RunAsync(
            session, new IdeaAssignCommand.Settings { Id = ideaId.ToString(), Member = "moveproj" },
            new FakeLedgerChainReader(TrustChain.Empty), new NodeKeyStore(), Now, cts.Token);
        Action byOption = () => IdeaAssignCommand.RefuseProject(
            new IdeaAssignCommand.Settings { Id = ideaId.ToString(), Project = "moveproj" });
        Func<Task> unknown = () => IdeaAssignCommand.RunAsync(
            session, new IdeaAssignCommand.Settings { Id = ideaId.ToString(), Member = "nobody-by-that-name" },
            new FakeLedgerChainReader(TrustChain.Empty), new NodeKeyStore(), Now, cts.Token);

        (await byName.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("is a project, not a member").And.Contain("h9k idea move").And.Contain("moveproj");
        byOption.Should().Throw<DomainValidationException>().Which.Message
            .Should().Contain($"h9k idea move {ideaId} moveproj");
        (await unknown.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("No owner matches", "a name that is neither a member nor a project is still just unknown");
        (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!
            .AssigneeOwnerId.Should().BeNull("a refused naming appends nothing");
    }

    [Fact]
    public async Task The_assign_command_with_no_member_lays_hold_for_this_nodes_own_owner_and_is_idempotent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        IdeaAssignCommand.Settings settings = new() { Id = ideaId.ToString() };
        (await IdeaAssignCommand.RunAsync(
            session, settings, new FakeLedgerChainReader(TrustChain.Empty), new NodeKeyStore(), Now, cts.Token))
            .Should().Be(ExitCodes.Ok);
        (await IdeaAssignCommand.RunAsync(
            session, settings, new FakeLedgerChainReader(TrustChain.Empty), new NodeKeyStore(), Now, cts.Token))
            .Should().Be(ExitCodes.Ok);

        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        idea.AssigneeOwnerId.Should().Be(_me.OwnerId);
        (await session.Events.FetchStreamAsync(ideaId, token: cts.Token)).Select(e => e.Data).OfType<IdeaAssigneeSet>()
            .Should().ContainSingle("assigning the member who already holds the idea records nothing");
    }

    [Fact]
    public async Task Concluding_archiving_or_promoting_an_idea_a_teammate_holds_or_created_is_refused_and_appends_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid creatorIsRyan = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);
        Guid heldByTaylor = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _taylor);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        foreach (Guid ideaId in new[] { creatorIsRyan, heldByTaylor })
        {
            Func<Task> conclude = () => ConcludeAsync(session, ideaId, "done", holder: null, MembershipRole.Member, cts.Token);
            Func<Task> archive = () => ArchiveAsync(session, ideaId, "no", holder: null, MembershipRole.Member, cts.Token);
            Func<Task> promote = () => PromoteAsync(session, ideaId, holder: null, reason: null, MembershipRole.Member, cts.Token);

            // A conclude or archive always carries --reason (it is the ending's own reason), and the refusal
            // still names the override rather than only saying the two flags go together.
            (await conclude.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
                .Should().StartWith("Idea ").And.Contain("belongs to")
                .And.Contain("Owner-role member may conclude it");
            (await archive.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
                .Should().Contain("Owner-role member may archive it");
            (await promote.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
                .Should().Contain("Owner-role member may promote it");

            (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!
                .State.Should().Be(IdeaState.Captured);
        }
    }

    [Fact]
    public async Task The_creator_cannot_conclude_an_idea_they_handed_away_but_its_assignee_can_even_when_not_the_creator()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid handedAway = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);
        Guid heldByMe = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, heldBy: Me, replicatedCreatorRoot: RyanRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> creatorConcludes = () => ConcludeAsync(session, handedAway, "done", holder: null, MembershipRole.Member, cts.Token);
        await creatorConcludes.Should().ThrowAsync<DomainBusinessRuleException>();

        (await ConcludeAsync(session, heldByMe, "shipped", holder: null, MembershipRole.Member, cts.Token)).Should().Be(ExitCodes.Ok);
        (await session.Events.AggregateStreamAsync<IdeaAggregate>(heldByMe, token: cts.Token))!
            .State.Should().Be(IdeaState.Concluded);
    }

    [Fact]
    public async Task A_creator_concludes_an_idea_captured_on_another_node_of_the_same_fleet_with_no_flag()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        // The idea was captured on a sibling node: its genesis replicated in, and its verified creator root
        // is this owner's own root, which is what the record a direct delivery writes carries.
        Guid ideaId = await SeedIdeaAsync(createdBy: DomainId.New(), cts.Token, replicatedCreatorRoot: _myRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await ConcludeAsync(session, ideaId, "discovery is done", holder: null, MembershipRole.Member, cts.Token)).Should().Be(ExitCodes.Ok);

        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
        idea.State.Should().Be(IdeaState.Concluded);
        IdeaConcluded concluded = (await session.Events.FetchStreamAsync(ideaId, token: cts.Token))
            .Select(e => e.Data).OfType<IdeaConcluded>().Single();
        concluded.OnBehalfOfOwnerRootFingerprint.Should().BeNull("an owner's own act records no override");
    }

    [Fact]
    public async Task An_idea_captured_before_this_change_stays_concludable_and_archivable_by_its_creator_with_no_flag()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        // Exactly the shape of a pre-change stream: capture and a revise, no assignee event anywhere.
        Guid concludeMe = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, legacy: true);
        Guid archiveMe = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, legacy: true);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await ConcludeAsync(session, concludeMe, "cut three tasks", holder: null, MembershipRole.Member, cts.Token)).Should().Be(ExitCodes.Ok);
        (await ArchiveAsync(session, archiveMe, "superseded", holder: null, MembershipRole.Member, cts.Token)).Should().Be(ExitCodes.Ok);

        (await session.Events.AggregateStreamAsync<IdeaAggregate>(concludeMe, token: cts.Token))!.State.Should().Be(IdeaState.Concluded);
        (await session.Events.AggregateStreamAsync<IdeaAggregate>(archiveMe, token: cts.Token))!.State.Should().Be(IdeaState.Archived);
    }

    [Fact]
    public async Task An_idea_whose_creator_this_node_cannot_resolve_refuses_and_names_the_owner_role_override()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        // A relayed genesis no direct act has confirmed yet leaves the creator root empty: unresolved.
        Guid ideaId = await SeedIdeaAsync(createdBy: DomainId.New(), cts.Token, replicatedCreatorRoot: string.Empty);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> conclude = () => ConcludeAsync(session, ideaId, "done", holder: null, MembershipRole.Owner, cts.Token);

        (await conclude.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain("owner is unknown").And.Contain("creator cannot be resolved")
            .And.Contain("Owner-role member may conclude it").And.Contain("--holder");

        Func<Task> wrongName = () => ConcludeAsync(session, ideaId, "stranded", RyanRoot[..8], MembershipRole.Owner, cts.Token);
        (await wrongName.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message.Should().Contain("must be the word unknown");

        (await ConcludeAsync(session, ideaId, "stranded", "unknown", MembershipRole.Owner, cts.Token)).Should().Be(ExitCodes.Ok);
        IdeaConcluded concluded = (await session.Events.FetchStreamAsync(ideaId, token: cts.Token))
            .Select(e => e.Data).OfType<IdeaConcluded>().Single();
        concluded.OnBehalfOfOwnerRootFingerprint.Should().BeNull("an owner that was unknown has no root to record");
        concluded.OverrideReason.Should().Be("stranded");
    }

    [Fact]
    public async Task An_idea_that_replicated_in_from_a_sibling_node_stays_the_creators_to_conclude_with_no_flag()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid sibling = DomainId.New();
        // A relayed genesis with no confirmed creator, and one that replicated in before any record existed.
        Guid staged = await SeedIdeaAsync(
            createdBy: DomainId.New(), cts.Token, replicatedCreatorRoot: string.Empty, claimedOriginNodeId: sibling);
        Guid beforeTheRecord = await SeedIdeaAsync(createdBy: DomainId.New(), cts.Token, replicatedFromNodeId: sibling);
        Guid fromAStranger = await SeedIdeaAsync(createdBy: DomainId.New(), cts.Token, replicatedFromNodeId: DomainId.New());

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await ConcludeAsync(session, staged, "done", holder: null, MembershipRole.Member, cts.Token, sibling)).Should().Be(ExitCodes.Ok);
        (await ConcludeAsync(session, beforeTheRecord, "done", holder: null, MembershipRole.Member, cts.Token, sibling))
            .Should().Be(ExitCodes.Ok);

        Func<Task> refused = () => ConcludeAsync(session, fromAStranger, "done", holder: null, MembershipRole.Member, cts.Token, sibling);
        (await refused.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message.Should().Contain("owner is unknown");
    }

    [Fact]
    public async Task An_owner_role_member_concludes_archives_or_promotes_a_teammates_idea_by_naming_whose_it_is_and_why()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid toConclude = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);
        Guid toArchive = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);
        Guid toPromote = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, replicatedCreatorRoot: RyanRoot);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (await ConcludeAsync(session, toConclude, "Ryan is out; it shipped", RyanRoot[..8], MembershipRole.Owner, cts.Token)).Should().Be(ExitCodes.Ok);
        (await ArchiveAsync(session, toArchive, "Ryan left; superseded", RyanRoot[..8], MembershipRole.Owner, cts.Token)).Should().Be(ExitCodes.Ok);
        (await PromoteAsync(session, toPromote, RyanRoot[..8], "Ryan is out this week", MembershipRole.Owner, cts.Token)).Should().Be(ExitCodes.Ok);

        IdeaConcluded concluded = (await session.Events.FetchStreamAsync(toConclude, token: cts.Token))
            .Select(e => e.Data).OfType<IdeaConcluded>().Single();
        concluded.OnBehalfOfOwnerRootFingerprint.Should().Be(RyanRoot);
        concluded.OverrideReason.Should().Be("Ryan is out; it shipped");
        IdeaArchived archived = (await session.Events.FetchStreamAsync(toArchive, token: cts.Token))
            .Select(e => e.Data).OfType<IdeaArchived>().Single();
        archived.OnBehalfOfOwnerRootFingerprint.Should().Be(RyanRoot);
        IdeaConcluded promoted = (await session.Events.FetchStreamAsync(toPromote, token: cts.Token))
            .Select(e => e.Data).OfType<IdeaConcluded>().Single();
        promoted.OverrideReason.Should().Be("Ryan is out this week");
    }

    [Fact]
    public async Task Cutting_a_task_from_an_idea_stays_open_to_any_member_and_never_copies_the_assignee_onto_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(createdBy: _ryan.Id, cts.Token, heldBy: _taylor, replicatedCreatorRoot: RyanRoot);
        Guid taskId = DomainId.New();

        // What h9k task add --from-idea writes, by a member who is neither the creator nor the assignee.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            IdeaTaskCut cut = IdeaDecider.CutTask(idea, taskId, "Give ideas a workspace", Now, _me.OwnerId);
            TaskAdded added = TaskDecider.Add(
                taskId, _projectId, cut.Objective, acceptanceCriteria: [], TaskType.Feature, agentContext: null,
                constraints: null, externalReference: null, Now, _me.OwnerId, model: null, blockedBy: null,
                sourceIdeaId: idea.Id);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(idea.Id, cut);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        task.AssigneeOwnerId.Should().BeNull("a task carries an assignee only when one is set on it directly");
        task.AssigneeOwnerFingerprint.Should().BeNull();
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.AssigneeOwnerId.Should().BeNull();
        (await query.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!
            .AssigneeOwnerId.Should().Be(_taylor.Id, "cutting a task leaves who holds the idea alone");
    }

    [Fact]
    public async Task Show_and_list_say_who_holds_an_idea_and_the_honest_absence_when_nobody_does()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid held = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token, heldBy: _ryan);
        Guid nobody = await SeedIdeaAsync(createdBy: _me.OwnerId, cts.Token);

        await using IQuerySession query = _postgres.Store.QuerySession();
        IdeaDetails heldDetails = (await query.LoadAsync<IdeaDetails>(held, cts.Token))!;
        IdeaDetails nobodyDetails = (await query.LoadAsync<IdeaDetails>(nobody, cts.Token))!;

        (await IdeaShowCommand.AssigneeMarkupAsync(query, heldDetails, cts.Token)).Should().Be("Ryan");
        (await IdeaShowCommand.AssigneeMarkupAsync(query, nobodyDetails, cts.Token))
            .Should().Contain("nobody").And.Contain("its creator decides it").And.Contain("h9k idea assign");
        nobodyDetails.State = IdeaState.Concluded;
        (await IdeaShowCommand.AssigneeMarkupAsync(query, nobodyDetails, cts.Token))
            .Should().NotContain("h9k idea assign", "an idea that has ended has nothing left to hold");

        Dictionary<Guid, OwnerDetails> owners = (await query.Query<OwnerDetails>().ToListAsync(cts.Token)).ToDictionary(o => o.Id);
        IdeaRow.AssigneeLabel(heldDetails, owners, new Dictionary<Guid, ProjectMemberLabels>()).Should().Be("Ryan");
        IdeaRow.AssigneeLabel(nobodyDetails, owners, new Dictionary<Guid, ProjectMemberLabels>()).Should().BeNull();
    }

    private async Task<TaskOwnerOverrideDecision> AuthorizeAsync(
        IDocumentSession session, Guid ideaId, string verb, CancellationToken cancellationToken)
    {
        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken))!;
        return await IdeaOwnerGuard.AuthorizeAsync(
            session, idea, _me, verb, holder: null, reason: null, new FakeLedgerChainReader(TrustChain.Empty),
            new NodeKeyStore(), cancellationToken);
    }

    private async Task<IdeaAssigneeSet?> AppendAsync(
        IDocumentSession session, Guid ideaId, OwnerDetailsSeed target, TaskOwnerOverrideDecision decision,
        CancellationToken cancellationToken)
    {
        IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken))!;
        IdeaAssigneeSet? set = IdeaAssignCommand.AppendAssignee(
            session, idea, (await session.LoadAsync<OwnerDetails>(target.Id, cancellationToken))!, _me.OwnerId, decision, Now);
        await session.SaveChangesAsync(cancellationToken);
        return set;
    }

    private async Task<int> UnassignAsync(
        IDocumentSession session, Guid ideaId, string? holder, string? reason, MembershipRole role,
        CancellationToken cancellationToken) =>
        await IdeaUnassignCommand.RunAsync(
            session, new IdeaUnassignCommand.Settings { Id = ideaId.ToString(), Holder = holder, Reason = reason },
            await ChainAsync(role, cancellationToken), new NodeKeyStore(), Now, cancellationToken);

    private async Task<int> ConcludeAsync(
        IDocumentSession session, Guid ideaId, string reason, string? holder, MembershipRole role,
        CancellationToken cancellationToken, Guid? siblingNodeId = null) =>
        await IdeaConcludeCommand.RunAsync(
            session, new IdeaConcludeCommand.Settings { Id = ideaId.ToString(), Reason = reason, Holder = holder },
            await ChainAsync(role, cancellationToken, siblingNodeId), new NodeKeyStore(), Now, cancellationToken);

    private async Task<int> ArchiveAsync(
        IDocumentSession session, Guid ideaId, string reason, string? holder, MembershipRole role,
        CancellationToken cancellationToken) =>
        await IdeaArchiveCommand.RunAsync(
            session, new IdeaArchiveCommand.Settings { Id = ideaId.ToString(), Reason = reason, Holder = holder },
            await ChainAsync(role, cancellationToken), new NodeKeyStore(), Now, cancellationToken);

    private async Task<int> PromoteAsync(
        IDocumentSession session, Guid ideaId, string? holder, string? reason, MembershipRole role,
        CancellationToken cancellationToken) =>
        await IdeaPromoteCommand.RunAsync(
            session, new IdeaPromoteCommand.Settings { Id = ideaId.ToString(), Holder = holder, Reason = reason },
            await ChainAsync(role, cancellationToken), new NodeKeyStore(), cancellationToken);

    private async Task<FakeLedgerChainReader> ChainAsync(
        MembershipRole role, CancellationToken cancellationToken, Guid? siblingNodeId = null)
    {
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(_me.NodeId, cancellationToken);
        return new FakeLedgerChainReader(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [_myRoot] = new(_myRoot, key.PublicKeyLine, [], RootNodeId: siblingNodeId?.ToString()),
            },
            [new ProjectMember(_myRoot, role, Now)]));
    }

    /// <summary>
    /// An idea captured by <paramref name="createdBy"/>, optionally held by <paramref name="heldBy"/>, shared with the
    /// team unless <paramref name="share"/> is false. An idea created by a teammate or a sibling node replicated in
    /// here, so its creator is the verified record a replicated genesis writes
    /// (<paramref name="replicatedCreatorRoot"/>, empty for a relayed one nobody has confirmed).
    /// </summary>
    private async Task<Guid> SeedIdeaAsync(
        Guid createdBy, CancellationToken cancellationToken, OwnerDetailsSeed? heldBy = null, bool share = true,
        string? replicatedCreatorRoot = null, Guid? projectId = null, bool legacy = false,
        Guid? claimedOriginNodeId = null, Guid? replicatedFromNodeId = null)
    {
        Guid ideaId = DomainId.New();
        Guid? ideaProject = projectId == Guid.Empty ? null : projectId ?? _projectId;
        List<object> events =
        [
            IdeaDecider.Capture(ideaId, createdBy, "Ideas deserve a discovery phase", ideaProject, Now, ProjectHome.None),
        ];
        if (legacy)
        {
            events.Add(new IdeaRevised(ideaId, "Ideas deserve a discovery phase, sharpened", Now, createdBy));
        }

        if (share && !legacy)
        {
            events.Add(new IdeaScopeSet(ideaId, ReplicationScope.Team, Now, createdBy));
        }

        if (heldBy is not null)
        {
            events.Add(new IdeaAssigneeSet(ideaId, heldBy.Id, heldBy.Root, Now, createdBy));
        }

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        if (replicatedFromNodeId is { } replicatedFrom)
        {
            // What an idea that replicated in before the creator record existed carries: the origin its
            // genesis was applied with, and no record.
            session.SetHeader(ReplicationEventHeaders.OriginNodeId, replicatedFrom.ToString());
        }

        session.Events.StartStream<IdeaAggregate>(ideaId, [.. events]);
        if (replicatedCreatorRoot is not null)
        {
            session.Store(new IdeaCreatorRootRecord
            {
                Id = ideaId,
                ProjectId = _projectId,
                ClaimedOriginNodeId = claimedOriginNodeId ?? DomainId.New(),
                CreatorRootFingerprint = replicatedCreatorRoot,
            });
        }

        await session.SaveChangesAsync(cancellationToken);
        return ideaId;
    }

    private async Task<OwnerDetailsSeed> SeedOwnerAsync(string name, string? root, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        OwnerAggregate owner = new();
        OwnerRegistered registered = OwnerDecider.Register(id, name, $"{name.ToLowerInvariant()}@test.local", Now);
        owner.Apply(registered);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        if (root is null)
        {
            session.Events.StartStream<OwnerAggregate>(id, registered);
        }
        else
        {
            session.Events.StartStream<OwnerAggregate>(id, registered, OwnerDecider.ClaimRoot(owner, root, verified: true, Now));
        }

        await session.SaveChangesAsync(cancellationToken);
        return new OwnerDetailsSeed(id, name, root);
    }

    private sealed record OwnerDetailsSeed(Guid Id, string Name, string? Root);
}
