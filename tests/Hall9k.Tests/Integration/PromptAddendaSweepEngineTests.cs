using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon;
using Hall9k.Daemon.PromptAddenda;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The whole prompt-addenda seam, end to end against real Marten/Postgres but a
/// <see cref="FakeLedger"/> stand-in for the ledger (idea b9b09779, piece 6; Brian's 2026-09-13
/// testing rule): <c>h9k project prompt-addendum set/show/list/remove</c> append only an event,
/// never touching the ledger themselves; <see cref="PromptAddendaSweepEngine"/> is the only thing
/// that ever writes or deletes the ledger file and materializes this node's own local copy; and
/// <see cref="ProjectPromptAddendaLoader"/> is what a prompt builder actually reads.
/// <para>
/// Idea 6be68ee2, trust-ledger finding 6: every seeded node here is claimed as the
/// <see cref="DefaultTrustChain"/>'s own Owner-role member (<see cref="OwnerRootFingerprint"/>), so
/// the push and materialize mechanics the first seven cases below exercise keep behaving exactly as
/// they did before the owner test shipped. The owner test itself — a member's overwrite or delete
/// skipped, an unsigned or revoked-node commit skipped, the owner's own last state restored — is
/// exercised separately below, through a purpose-built <see cref="FakeLedgerCommitReader"/> commit
/// history per scenario rather than through <see cref="FakeLedger"/>'s own current-content-only
/// model, which has no notion of commit history at all.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PromptAddendaSweepEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string ProjectName = "hall9k";

    /// <summary>The fingerprint every seeded node's own owner claims (<see cref="SeedAsync"/>) and
    /// <see cref="DefaultTrustChain"/> recognizes as this project's Owner-role member — so
    /// <c>PromptAddendaSweepEngine.PushAsync</c>'s own owner-role gate passes for every test below
    /// that predates the owner test itself.</summary>
    private const string OwnerRootFingerprint = "owner-root-fingerprint";

    private const string OwnerRootPublicKeyLine = "ssh-ed25519 AAAAFAKEownerroot test";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    private string _home => _scopedHome.Home;

    public PromptAddendaSweepEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Set_show_list_remove_round_trip_and_a_second_set_replaces_the_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);
        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");

        await SetAsync("work", "Prefer squash commits.", overCapReason: null, cts.Token);

        PromptAddendaSweepResult firstSweep = await engine.SweepOnceAsync(cts.Token);
        firstSweep.Pushed.Should().Be(1);

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Exists.Should().BeTrue();
        stored.Content.Should().Be("Prefer squash commits.");
        File.Exists(materializedFile).Should().BeTrue();
        File.ReadAllText(materializedFile).Should().Be("Prefer squash commits.");

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            ProjectDetails project = (await query.LoadAsync<ProjectDetails>(projectId, cts.Token))!;
            LoadedPromptAddendum? loaded = ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Work);
            loaded.Should().NotBeNull();
            loaded!.Content.Should().Be("Prefer squash commits.");
            loaded.OverCap.Should().BeFalse();
        }

        (await ShowExitCodeAsync("work", cts.Token)).Should().Be(ExitCodes.Ok);
        (await ListExitCodeAsync(cts.Token)).Should().Be(ExitCodes.Ok);

        // A second set replaces the whole file rather than merging with the first.
        await SetAsync("work", "Second version entirely.", overCapReason: null, cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Content.Should().Be("Second version entirely.");
        File.ReadAllText(materializedFile).Should().Be("Second version entirely.");

        // remove clears both the ledger file and the local materialized copy.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int exitCode = await ProjectPromptAddendumRemoveCommand.RunAsync(
                session, new ProjectPromptAddendumRemoveCommand.Settings { Project = ProjectName, Builder = "work" },
                new FakeLedgerChainReader(DefaultTrustChain()), cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);
        }

        await engine.SweepOnceAsync(cts.Token);

        stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Exists.Should().BeFalse();
        File.Exists(materializedFile).Should().BeFalse();
    }

    /// <summary>
    /// A replicated change can land on the stream behind a newer one for the same builder key,
    /// because a catch-up answer serves a pre-switch-on head after the tail. The projection keeps
    /// the newer text, and the sweep must not push the older one over it in the ledger.
    /// </summary>
    [Fact]
    public async Task A_change_stamped_older_than_the_applied_one_is_never_pushed_over_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, _) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);
        Guid ownerId = DomainId.New();

        await AppendAsync(projectId, new ProjectPromptAddendumSet(
            projectId, "work", "newer text", false, null, Now.AddDays(2), ownerId), cts.Token);
        (await engine.SweepOnceAsync(cts.Token)).Pushed.Should().Be(1);

        await AppendAsync(projectId, new ProjectPromptAddendumSet(
            projectId, "work", "older text", false, null, Now, ownerId), cts.Token);
        (await engine.SweepOnceAsync(cts.Token)).Pushed.Should().Be(0, "the older change lost on its own stamp");

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Content.Should().Be("newer text");
    }

    /// <summary>
    /// Idea 6be68ee2, trust-ledger finding 6: <c>WriteAsync</c>'s own no-op shortcut used to compare
    /// only against the ledger tip's raw content, so an owner re-asserting byte-identical text after
    /// the node that had authorized the tip was revoked (idea 6be68ee2's own lost-node-then-reinstate
    /// flow) saw its push silently swallowed — the unauthorized tip stayed the permanent tip forever,
    /// even on this same node's own next materialize. The fix is content-equal only counts as a
    /// no-op when the tip's own newest commit is itself owner-authorized (independent pre-PR review,
    /// cycle 1, conformance and adversarial lenses, medium).
    /// </summary>
    [Fact]
    public async Task WriteAsync_reissues_an_owner_commit_when_the_tip_content_matches_but_was_never_owner_authorized()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, _) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        string path = LedgerRefRegistry.PromptAddendumPath("work");
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, path, "Identical text.", null,
                "a member's own overwrite", new LedgerCommitter("member", "member@hall9k.local"),
                new LedgerSigningKey("/dev/null/member")),
            cts.Token);
        int writesBefore = ledger.Writes.Count;

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [path] = [new LedgerPathCommit("Identical text.", "sha-member", "raw-member-overwrite")],
            },
            (raw, key) => false);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        await SetAsync("work", "Identical text.", overCapReason: null, cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        ledger.Writes.Count.Should().Be(
            writesBefore + 1,
            "the tip's own newest commit was never owner-authorized, so a content match alone must never skip the write");
    }

    /// <summary>
    /// The mirror of the write-side case above: <c>DeleteAsync</c>'s own no-op shortcut used to
    /// compare only against whether the path exists at the tip at all, so a member's own delete
    /// sitting at the tip made the owner's own subsequent removal a pure no-op — the sync position
    /// still advanced past the event, so it was never retried, and the member's earlier content kept
    /// materializing on every node forever, restored by <c>MaterializeAsync</c>'s own owner test
    /// (independent pre-PR review, cycle 1, conformance and adversarial lenses, medium).
    /// </summary>
    [Fact]
    public async Task DeleteAsync_reissues_an_owner_commit_when_the_path_is_already_absent_but_was_never_owner_authorized()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, _) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine setupEngine = CreateEngine(ledger, node);
        await SetAsync("work", "Owner's original text.", overCapReason: null, cts.Token);
        await setupEngine.SweepOnceAsync(cts.Token);

        string path = LedgerRefRegistry.PromptAddendumPath("work");
        LedgerFile ownersTip = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, path, cts.Token);
        await ledger.DeleteAsync(
            new LedgerDeleteRequest(
                RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, path, ownersTip.BlobId,
                "a member's own delete", new LedgerCommitter("member", "member@hall9k.local"),
                new LedgerSigningKey("/dev/null/member")),
            cts.Token);
        int deletesBefore = ledger.Deletes.Count;

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int exitCode = await ProjectPromptAddendumRemoveCommand.RunAsync(
                session, new ProjectPromptAddendumRemoveCommand.Settings { Project = ProjectName, Builder = "work" },
                new FakeLedgerChainReader(DefaultTrustChain()), cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);
        }

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [path] = [new LedgerPathCommit(null, "sha-member-delete", "raw-member-delete")],
            },
            (raw, key) => false);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        await engine.SweepOnceAsync(cts.Token);

        ledger.Deletes.Count.Should().Be(
            deletesBefore + 1,
            "the tip's own newest commit for this path was a member's unauthorized delete, so the path already "
            + "reading absent must never skip the owner's own removal");
    }

    /// <summary>
    /// Idea 6be68ee2, trust-ledger finding 6: a teammate's own addendum lands on this identical
    /// stream through <c>EventReplicationInbox</c>, which stamps <see cref="ReplicationEventHeaders.ReceivedFromNodeId"/>
    /// on the event it merges — never something the sender claims. This sweep must never push it to
    /// the ledger: doing so would re-sign the teammate's own content under this node's own committer
    /// and signing key, as if this install had authored it.
    /// </summary>
    [Fact]
    public async Task A_replicated_addendum_is_never_pushed_by_the_receiver()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, _) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);
        Guid ownerId = DomainId.New();
        Guid teammateNodeId = DomainId.New();

        await AppendReplicatedAsync(
            projectId, new ProjectPromptAddendumSet(projectId, "work", "a teammate's own text", false, null, Now, ownerId),
            teammateNodeId, cts.Token);

        PromptAddendaSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        sweep.Pushed.Should().Be(0, "a replicated addendum is never re-signed under this node's own key");

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Exists.Should().BeFalse("nothing was ever pushed to the ledger for a replicated addendum");
    }

    [Fact]
    public async Task An_over_cap_set_stops_naming_the_cap_and_succeeds_with_over_cap_recording_the_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);
        string tooLong = new('x', ProjectDecider.PromptAddendumMaximumLength + 1);

        Func<Task> withoutOverCap = () => SetAsync("agent", tooLong, overCapReason: null, cts.Token);
        (await withoutOverCap.Should().ThrowAsync<DomainValidationException>())
            .Which.Message.Should().Contain(ProjectDecider.PromptAddendumMaximumLength.ToString())
            .And.Contain("--over-cap");

        await SetAsync("agent", tooLong, overCapReason: "house style needs the extra examples", cts.Token);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            ProjectAggregate project = (await query.Events.AggregateStreamAsync<ProjectAggregate>(
                projectId, token: cts.Token))!;
            project.PromptAddenda["agent"].OverCap.Should().BeTrue();
            project.PromptAddenda["agent"].OverCapReason.Should().Be("house style needs the extra examples");
        }

        await engine.SweepOnceAsync(cts.Token);

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("agent"),
            cts.Token);
        stored.Content.Should().StartWith(ProjectPromptAddendaLoader.OverCapMarker);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            ProjectDetails project = (await query.LoadAsync<ProjectDetails>(projectId, cts.Token))!;
            LoadedPromptAddendum? loaded = ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Agent);
            loaded!.OverCap.Should().BeTrue("the ledger file's own marker line is what the loader — and so the prompt builder's own log — reads back");
            loaded.Content.Should().Be(tooLong);
        }

        Directory.Exists(projectHome).Should().BeTrue();
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance lens, medium: a home-less project
    /// (<c>h9k project add --no-home</c>) could set an addendum that reported success but could
    /// never actually reach a prompt — <c>PromptAddendaSweepEngine.MaterializeAsync</c> returns 0
    /// immediately for a project with no home directory, so no local copy is ever written. Refused
    /// at set time instead.
    /// </summary>
    [Fact]
    public async Task Set_refuses_a_project_with_no_home_directory_yet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), ProjectName, RepositoryPath, null, null, Now,
                    homeDirectory: null));
            await session.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => SetAsync("work", "Prefer squash commits.", overCapReason: null, cts.Token);
        (await act.Should().ThrowAsync<DomainValidationException>())
            .Which.Message.Should().Contain("no home directory").And.Contain("h9k project init");

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            ProjectDetails project = (await query.LoadAsync<ProjectDetails>(projectId, cts.Token))!;
            project.PromptAddenda.Should().BeEmpty("the refusal happens before any event is appended");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, medium: a permanently failing ledger
    /// push otherwise left the whole feature silently inert — the CLI reports success,
    /// <c>list</c>/<c>show</c> keep reporting the addendum as set — with nothing visible short of
    /// reading daemon logs. Wraps a real <see cref="FakeLedger"/> so every other seam (reads,
    /// materialize) behaves exactly as it always has; only writes are made to fail on demand.
    /// </summary>
    [Fact]
    public async Task A_persistently_failing_ledger_push_is_recorded_and_cleared_once_it_recovers()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FlakyWriteLedger ledger = new(new FakeLedger()) { FailWrites = true };
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);

        await SetAsync("work", "Prefer squash commits.", overCapReason: null, cts.Token);

        PromptAddendaSweepResult failedSweep = await engine.SweepOnceAsync(cts.Token);
        failedSweep.Pushed.Should().Be(0);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            PromptAddendaSyncPosition? position = await query.LoadAsync<PromptAddendaSyncPosition>(projectId, cts.Token);
            position.Should().NotBeNull();
            position!.LastPushError.Should().NotBeNullOrEmpty();
            position.LastPushErrorAt.Should().NotBeNull();
        }

        // list/show still succeed rather than crash while a failure is outstanding — the audit
        // trail is accurate, only what has actually reached the ledger is in question.
        (await ListExitCodeAsync(cts.Token)).Should().Be(ExitCodes.Ok);
        (await ShowExitCodeAsync("work", cts.Token)).Should().Be(ExitCodes.Ok);

        ledger.FailWrites = false;
        PromptAddendaSweepResult recoveredSweep = await engine.SweepOnceAsync(cts.Token);
        recoveredSweep.Pushed.Should().Be(1, "the same un-advanced position that kept retrying the failed push retries it again once writes recover");

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            PromptAddendaSyncPosition? position = await query.LoadAsync<PromptAddendaSyncPosition>(projectId, cts.Token);
            position!.LastPushError.Should().BeNull("a push that actually lands clears whatever failure preceded it");
            position.LastPushErrorAt.Should().BeNull();
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance and adversarial lenses, both medium:
    /// <c>MaterializeAsync</c> used to bank the ledger tip into its own cache before writing a single
    /// local file, keyed only on the repository path. Re-homing a project
    /// (<c>h9k project set --home</c>, or <c>h9k project init</c> repairing a wiped one) moves the
    /// local materialize target without moving the ledger tip at all, so the old, repository-only key
    /// still matched and the new home's own <c>prompt-addenda/</c> stayed empty forever. Asserts the
    /// new home actually gets materialized into on the very next sweep, even though the ledger's own
    /// tip never changes between the two sweeps.
    /// </summary>
    [Fact]
    public async Task A_re_homed_project_still_materializes_into_the_new_home_even_though_the_ledger_tip_never_moved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);

        await SetAsync("work", "Prefer squash commits.", overCapReason: null, cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        string originalMaterialized = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        File.Exists(originalMaterialized).Should().BeTrue();

        string newHome = Path.Combine(_home, "projects", "hall9k-rehomed");
        Directory.CreateDirectory(newHome);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            ProjectAggregate aggregate = (await session.Events.AggregateStreamAsync<ProjectAggregate>(
                projectId, token: cts.Token))!;
            session.Events.Append(
                projectId,
                ProjectDecider.ChangeSettings(
                    aggregate,
                    verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
                    skipPermissions: Optional<bool>.None,
                    contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                    changedAt: Now.AddMinutes(1),
                    changedByOwnerId: node.OwnerId,
                    homeDirectory: ProjectHome.Parse(newHome)));
            await session.SaveChangesAsync(cts.Token);
        }

        // The ledger's own tip has not moved since the sweep above — no set/remove happened in
        // between — so this is exactly the scenario the premature tip-bank left unrepaired.
        PromptAddendaSweepResult rehomedSweep = await engine.SweepOnceAsync(cts.Token);
        rehomedSweep.Materialized.Should().BeGreaterThan(0, "the new home starts empty and must be materialized into, even though the ledger tip never moved");

        string newMaterialized = ProjectHomePaths.PromptAddendumFile(newHome, "work");
        File.Exists(newMaterialized).Should().BeTrue();
        File.ReadAllText(newMaterialized).Should().Be("Prefer squash commits.");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, medium: a failing
    /// <see cref="ILedger.ListRefsAsync"/> (an unreachable <c>origin</c> — offline, no remote
    /// configured) was retried on every single tick forever, with no backoff, each one paying for a
    /// network round trip and a warning log line. Asserts a tick immediately following a failure does
    /// not call <see cref="ILedger.ListRefsAsync"/> again — the backoff window (floor 30 seconds)
    /// comfortably covers the whole test's own wall-clock time, so a second call landing anyway would
    /// mean the backoff never engaged.
    /// </summary>
    [Fact]
    public async Task A_failing_list_refs_call_backs_off_instead_of_retrying_on_the_very_next_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FlakyListRefsLedger ledger = new(new FakeLedger()) { FailListRefs = true };
        PromptAddendaSweepEngine engine = CreateEngine(ledger, node);

        PromptAddendaSweepResult firstSweep = await engine.SweepOnceAsync(cts.Token);
        firstSweep.Materialized.Should().Be(0, "the failing ListRefsAsync call is caught per-project rather than thrown out of the sweep");
        ledger.ListRefsCalls.Should().Be(1);

        PromptAddendaSweepResult secondSweep = await engine.SweepOnceAsync(cts.Token);
        secondSweep.Materialized.Should().Be(0);
        ledger.ListRefsCalls.Should().Be(1, "the backoff window from the first failure has not elapsed, so this tick must skip the network call rather than retry it immediately");
    }

    /// <summary>Idea 6be68ee2, trust-ledger finding 6: <c>PushAsync</c> checks this node's own owner
    /// against the live chain before ever touching its sync position, so a node whose owner has been
    /// demoted to Member (or was never Owner-role at all) never pushes what it recorded locally — the
    /// ledger holds one file per builder for the whole project, so a member's own push would be
    /// refused everywhere it is read back.</summary>
    [Fact]
    public async Task PushAsync_skips_when_this_nodes_owner_is_not_owner_role()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, _) = await SeedAsync(cts.Token);
        await SetAsync("work", "Guidance a member's own node cannot push.", overCapReason: null, cts.Token);

        FakeLedger ledger = new();
        TrustChain memberOnlyChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [OwnerRootFingerprint] = new TrustedOwner(OwnerRootFingerprint, OwnerRootPublicKeyLine, Nodes: []),
            },
            [new ProjectMember(OwnerRootFingerprint, MembershipRole.Member, Now)]);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(memberOnlyChain), new LedgerBackedCommitReader(ledger));

        PromptAddendaSweepResult sweep = await engine.SweepOnceAsync(cts.Token);

        sweep.Pushed.Should().Be(0, "this node's own owner is not an Owner-role member of the project");
        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Exists.Should().BeFalse("the push gate skips before ever touching the ledger");
    }

    /// <summary>The tip-gated cache (<c>_lastKnownPromptAddendaTips</c>) is keyed on the owner-role
    /// key set as well as the tip, precisely so a node revoked between two ticks forces a
    /// re-materialize even though nothing pushed to the prompt-addenda ref itself moved (idea
    /// 6be68ee2, trust-ledger finding 6). A regression that dropped the key set from that cache
    /// tuple would keep serving this stale content forever, since the tip alone never changes here.</summary>
    [Fact]
    public async Task MaterializeAsync_rematerializes_when_only_the_owner_role_key_set_changes_and_the_tip_stays_put()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);
        const string vouchedNodePublicKeyLine = "ssh-ed25519 AAAAFAKEvouchednode test";

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                    [new LedgerPathCommit("A vouched node's own guidance.", "sha-vouched", "raw-vouched-commit")],
            },
            (raw, key) => raw == "raw-vouched-commit" && key == vouchedNodePublicKeyLine);
        TrustedNode vouchedNode = new("vouched-node-id", vouchedNodePublicKeyLine, "vouched-fingerprint", Now);
        MutableChainReader chainReader = new(
            new TrustChain(
                new Dictionary<string, TrustedOwner>
                {
                    [OwnerRootFingerprint] = new TrustedOwner(OwnerRootFingerprint, OwnerRootPublicKeyLine, Nodes: [vouchedNode]),
                },
                [new ProjectMember(OwnerRootFingerprint, MembershipRole.Owner, Now)]));
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            chainReader, commitReader);

        await engine.SweepOnceAsync(cts.Token);
        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        File.ReadAllText(materializedFile).Should().Be("A vouched node's own guidance.");

        // The vouched node is revoked (absent from Nodes) between ticks. Neither the ledger nor the
        // commit reader changes at all, so the tip this sweep sees is identical to the last one —
        // only ComputeOwnerRoleKeySetSignature's own change can be what forces the next sweep to
        // re-walk this path's history.
        chainReader.Chain = new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [OwnerRootFingerprint] = new TrustedOwner(OwnerRootFingerprint, OwnerRootPublicKeyLine, Nodes: []),
            },
            [new ProjectMember(OwnerRootFingerprint, MembershipRole.Owner, Now)]);

        await engine.SweepOnceAsync(cts.Token);

        File.Exists(materializedFile).Should().BeFalse(
            "the revoked node's own commit no longer authorizes anything, and a cache keyed on the tip alone would have kept serving its stale content");
    }

    /// <summary>A failed trust-chain read is caught per project in <c>SweepOnceAsync</c> and retried
    /// next sweep — never allowed to wipe or overwrite what the last successful sweep already
    /// materialized, the identical fail-closed pattern the ledger-tip cache's own doc comment
    /// describes for a throw partway through <c>MaterializeAsync</c> itself.</summary>
    [Fact]
    public async Task A_failed_chain_read_leaves_the_last_good_materialized_files_standing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                    [new LedgerPathCommit("Owner's own guidance.", "sha-owner", "raw-owner-commit")],
            },
            (raw, key) => raw == "raw-owner-commit" && key == OwnerRootPublicKeyLine);
        FlakyChainReader chainReader = new(new FakeLedgerChainReader(DefaultTrustChain()));
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            chainReader, commitReader);

        await engine.SweepOnceAsync(cts.Token);
        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        File.ReadAllText(materializedFile).Should().Be("Owner's own guidance.");

        chainReader.FailCompute = true;
        await engine.SweepOnceAsync(cts.Token);

        File.Exists(materializedFile).Should().BeTrue("a failed trust-chain read must never wipe the last good materialize");
        File.ReadAllText(materializedFile).Should().Be("Owner's own guidance.");
    }

    /// <summary>Idea 6be68ee2, trust-ledger finding 6: the newest commit, signed by the owner's own
    /// root key, is what materializes — the ordinary case every other scenario below is a variation
    /// on.</summary>
    [Fact]
    public async Task MaterializeAsync_materializes_the_owners_own_newest_authorized_commit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                    [new LedgerPathCommit("Owner's own guidance.", "sha-owner", "raw-owner-commit")],
            },
            (raw, key) => raw == "raw-owner-commit" && key == OwnerRootPublicKeyLine);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        PromptAddendaSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        sweep.Materialized.Should().BeGreaterThan(0);

        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        File.Exists(materializedFile).Should().BeTrue();
        File.ReadAllText(materializedFile).Should().Be("Owner's own guidance.");
    }

    /// <summary>A member's own overwrite sits newest in the ledger's own history, but it is never
    /// signed by the owner's own chain — it is skipped over, and the owner's own last authorized
    /// state materializes instead.</summary>
    [Fact]
    public async Task MaterializeAsync_skips_a_members_overwrite_and_keeps_the_owners_content()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                [
                    new LedgerPathCommit("A member's own overwrite.", "sha-member", "raw-member-commit"),
                    new LedgerPathCommit("Owner's own guidance.", "sha-owner", "raw-owner-commit"),
                ],
            },
            (raw, key) => raw == "raw-owner-commit" && key == OwnerRootPublicKeyLine);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        await engine.SweepOnceAsync(cts.Token);

        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        File.Exists(materializedFile).Should().BeTrue();
        File.ReadAllText(materializedFile).Should().Be(
            "Owner's own guidance.", "the member's own overwrite is skipped, and the owner's own last state is restored");
    }

    /// <summary>A member's own delete sits newest, unsigned by the owner's own chain — skipped over,
    /// restoring the owner's own content rather than removing it (the objective this whole sweep
    /// exists for: a member's push can neither reach another node's agents nor delete or suppress
    /// the owner's guidance).</summary>
    [Fact]
    public async Task MaterializeAsync_skips_a_members_delete_and_restores_the_owners_content()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                [
                    new LedgerPathCommit(null, "sha-member-delete", "raw-member-delete"),
                    new LedgerPathCommit("Owner's own guidance.", "sha-owner", "raw-owner-commit"),
                ],
            },
            (raw, key) => raw == "raw-owner-commit" && key == OwnerRootPublicKeyLine);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        await engine.SweepOnceAsync(cts.Token);

        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        File.Exists(materializedFile).Should().BeTrue("the member's own delete never reaches this node's own agents");
        File.ReadAllText(materializedFile).Should().Be("Owner's own guidance.");
    }

    /// <summary>A node that WAS vouched but is now revoked is simply absent from
    /// <see cref="TrustedOwner.Nodes"/> (the live chain's own shape) — its own commit, even though it
    /// verifies against that node's own key, authorizes nothing because that key is no longer a
    /// candidate the owner test tries at all.</summary>
    [Fact]
    public async Task MaterializeAsync_skips_a_revoked_nodes_commit_and_materializes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);
        const string revokedNodePublicKeyLine = "ssh-ed25519 AAAAFAKErevokednode test";

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                    [new LedgerPathCommit("A revoked node's own text.", "sha-revoked", "raw-revoked-commit")],
            },
            (raw, key) => raw == "raw-revoked-commit" && key == revokedNodePublicKeyLine);
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [OwnerRootFingerprint] = new TrustedOwner(
                    OwnerRootFingerprint, OwnerRootPublicKeyLine, Nodes: [],
                    RevokedNodeIds: new HashSet<string> { "revoked-node-id" }),
            },
            [new ProjectMember(OwnerRootFingerprint, MembershipRole.Owner, Now)]);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(trustChain), commitReader);

        await engine.SweepOnceAsync(cts.Token);

        File.Exists(ProjectHomePaths.PromptAddendumFile(projectHome, "work")).Should().BeFalse();
    }

    /// <summary>Two commits, neither ever authorized — the whole history is walked, and the result is
    /// still absent rather than falling back to whichever commit merely happens to be newest.</summary>
    [Fact]
    public async Task MaterializeAsync_materializes_absent_when_nothing_in_the_whole_history_is_authorized()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                [
                    new LedgerPathCommit("Second bad edit.", "sha-member-2", "raw-member-2"),
                    new LedgerPathCommit("First bad edit.", "sha-member-1", "raw-member-1"),
                ],
            },
            (raw, key) => false);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        await engine.SweepOnceAsync(cts.Token);

        File.Exists(ProjectHomePaths.PromptAddendumFile(projectHome, "work")).Should().BeFalse();
    }

    /// <summary>
    /// A home file already holding a refused version — the shape a member's overwrite left behind
    /// before this owner test shipped — is replaced by the owner's own verified content on the very
    /// first sweep that computes it, never left standing because a file already happens to exist
    /// there.
    /// </summary>
    [Fact]
    public async Task A_home_file_holding_a_refused_version_is_replaced_by_the_verified_version_on_the_next_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, _, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        await SeedPromptAddendaRefTipAsync(ledger, cts.Token);

        string materializedFile = ProjectHomePaths.PromptAddendumFile(projectHome, "work");
        Directory.CreateDirectory(ProjectHomePaths.PromptAddendaDirectory(projectHome));
        await File.WriteAllTextAsync(materializedFile, "A previously-refused member version.", cts.Token);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, IReadOnlyList<LedgerPathCommit>>
            {
                [LedgerRefRegistry.PromptAddendumPath("work")] =
                    [new LedgerPathCommit("Owner's own real guidance.", "sha-owner", "raw-owner-commit")],
            },
            (raw, key) => raw == "raw-owner-commit" && key == OwnerRootPublicKeyLine);
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), commitReader);

        await engine.SweepOnceAsync(cts.Token);

        File.ReadAllText(materializedFile).Should().Be("Owner's own real guidance.");
    }

    /// <summary>The default trust chain every non-owner-test scenario above uses: one Owner-role
    /// member, <see cref="OwnerRootFingerprint"/>, matching what <see cref="SeedAsync"/> claims for
    /// every seeded node's own owner — so push and materialize both behave exactly as they did
    /// before the owner test shipped.</summary>
    private static TrustChain DefaultTrustChain() =>
        new(
            new Dictionary<string, TrustedOwner>
            {
                [OwnerRootFingerprint] = new TrustedOwner(OwnerRootFingerprint, OwnerRootPublicKeyLine, Nodes: []),
            },
            [new ProjectMember(OwnerRootFingerprint, MembershipRole.Owner, Now)]);

    /// <summary>Every non-owner-test scenario's own engine: the identical <see cref="FakeLedger"/> (or
    /// a decorator over one) both drives push assertions against and feeds
    /// <see cref="LedgerBackedCommitReader"/>, so materialize sees exactly what push most recently
    /// wrote without a second, independently-maintained fixture to keep in sync.</summary>
    private PromptAddendaSweepEngine CreateEngine(ILedger ledger, NodeContext node) =>
        new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance,
            new FakeLedgerChainReader(DefaultTrustChain()), new LedgerBackedCommitReader(ledger));

    /// <summary>Gives <see cref="ILedger.ListRefsAsync"/> a non-null tip for the prompt-addenda ref
    /// so a scenario driving <see cref="ILedgerCommitReader"/> directly (rather than through
    /// <see cref="LedgerBackedCommitReader"/>) still reaches <c>MaterializeAsync</c>'s own
    /// per-builder walk — the content of this seed write itself is never read by any of those
    /// scenarios, which supply their own commit history straight to <see cref="FakeLedgerCommitReader"/>.</summary>
    private static async Task SeedPromptAddendaRefTipAsync(FakeLedger ledger, CancellationToken cancellationToken) =>
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
                "placeholder — never read by the owner-test scenarios, which supply their own commit history",
                null, "seed the ref tip", new LedgerCommitter("seed", "seed@hall9k.local"),
                new LedgerSigningKey("/dev/null/seed")),
            cancellationToken);

    /// <summary>An <see cref="ILedgerChainReader"/> whose own <see cref="Chain"/> a test can swap
    /// between two <c>SweepOnceAsync</c> calls on the same engine instance — <see cref="FakeLedgerChainReader"/>
    /// itself has no settable chain, only a fixed one baked in at construction, which cannot express
    /// a chain that changes between two ticks against the identical engine and its own cache.</summary>
    private sealed class MutableChainReader(TrustChain chain) : ILedgerChainReader
    {
        public TrustChain Chain { get; set; } = chain;

        public Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult(Chain);
    }

    /// <summary>A thin <see cref="ILedgerChainReader"/> decorator whose <see cref="ComputeAsync"/>
    /// call can be switched to always throw on demand — the real failure mode an unreachable trust
    /// chain remote raises, while passing straight through to <paramref name="inner"/> otherwise.</summary>
    private sealed class FlakyChainReader(ILedgerChainReader inner) : ILedgerChainReader
    {
        public bool FailCompute { get; set; }

        public Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            FailCompute
                ? throw new InvalidOperationException("simulated unreachable trust-chain read")
                : inner.ComputeAsync(repositoryPath, cancellationToken);
    }

    /// <summary>An <see cref="ILedgerCommitReader"/> over a <see cref="FakeLedger"/>'s own current
    /// content: since that fake tracks no per-path commit history at all, every path reads as either
    /// no history (the path was never written) or a single "commit" at its current content — enough
    /// for every scenario above the dedicated owner-test cases, none of which drives more than one
    /// authorized state per path, and every one of which trusts whatever it just pushed
    /// unconditionally (<see cref="IsSignedByAsync"/> always answers true).</summary>
    private sealed class LedgerBackedCommitReader(ILedger ledger) : ILedgerCommitReader
    {
        public Task<LedgerSignedCommit?> ReadSignedCommitAsync(
            string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                $"{nameof(LedgerBackedCommitReader)} backs {nameof(PromptAddendaSweepEngine)}'s own tests, which "
                + $"only ever call {nameof(ReadCommitsTouchingPathAsync)}.");

        public Task<bool> IsSignedByAsync(
            string repositoryPath, string rawCommitBytes, string publicKeyLine, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public async Task<IReadOnlyList<LedgerPathCommit>> ReadCommitsTouchingPathAsync(
            string repositoryPath, string refName, string path,
            Func<string, CancellationToken, Task<bool>> isAuthorizedAsync, CancellationToken cancellationToken)
        {
            LedgerFile file = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            return file.Exists ? [new LedgerPathCommit(file.Content, file.BlobId!, file.BlobId!)] : [];
        }

        /// <summary>Trusts unconditionally, the same as <see cref="IsSignedByAsync"/> above: every
        /// scenario driving this reader trusts whatever it just pushed.</summary>
        public Task<bool> IsRefTipAuthorizedAsync(
            string repositoryPath, string refName,
            Func<string, CancellationToken, Task<bool>> isAuthorizedAsync, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    /// <summary>A thin <see cref="ILedger"/> decorator whose <see cref="ListRefsAsync"/> call can be
    /// switched to always throw on demand, and counts every call it actually made — the real failure
    /// mode an unreachable ledger remote raises, while every other operation passes straight through
    /// to <paramref name="inner"/> unchanged.</summary>
    private sealed class FlakyListRefsLedger(ILedger inner) : ILedger
    {
        public bool FailListRefs { get; set; }

        public int ListRefsCalls { get; private set; }

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            inner.WriteAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            inner.WriteManyAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken)
        {
            ListRefsCalls++;
            return FailListRefs
                ? throw new InvalidOperationException("simulated unreachable ledger remote")
                : inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);
        }

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
    }

    /// <summary>A thin <see cref="ILedger"/> decorator whose writes can be switched to always throw
    /// <see cref="LedgerPushRejectedException"/> on demand — the real failure mode a push exhausting
    /// every retry attempt raises — while every other operation passes straight through to
    /// <paramref name="inner"/> unchanged.</summary>
    private sealed class FlakyWriteLedger(ILedger inner) : ILedger
    {
        public bool FailWrites { get; set; }

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            FailWrites
                ? throw new LedgerPushRejectedException(request.RefName, 5, "simulated push rejection")
                : inner.WriteAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            FailWrites
                ? throw new LedgerPushRejectedException(request.RefName, 5, "simulated push rejection")
                : inner.WriteManyAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            FailWrites
                ? throw new LedgerPushRejectedException(request.RefName, 5, "simulated push rejection")
                : inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
    }

    private async Task AppendAsync(Guid projectId, object @event, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.Append(projectId, @event);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Appends exactly the way <c>EventReplicationInbox.ApplyAsync</c> does for a
    /// project-aggregate-stream event it merges: the same header, stamped from the verified sender,
    /// never anything a wire record could claim on its own.</summary>
    private async Task AppendReplicatedAsync(
        Guid projectId, object @event, Guid receivedFromNodeId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        StreamAction action = session.Events.Append(projectId, @event);
        action.Events[^1].SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, receivedFromNodeId.ToString());
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task SetAsync(string builder, string content, string? overCapReason, CancellationToken cancellationToken)
    {
        string file = Path.Combine(Path.GetTempPath(), $"prompt-addendum-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(file, content, cancellationToken);
        try
        {
            await using IDocumentSession session = _postgres.Store.LightweightSession();
            int exitCode = await ProjectPromptAddendumSetCommand.RunAsync(
                session,
                new ProjectPromptAddendumSetCommand.Settings
                {
                    Project = ProjectName, Builder = builder, File = file, OverCapReason = overCapReason,
                },
                new FakeLedgerChainReader(DefaultTrustChain()),
                cancellationToken);
            exitCode.Should().Be(ExitCodes.Ok);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task<int> ShowExitCodeAsync(string builder, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        return await ProjectPromptAddendumShowCommand.RunAsync(
            session, new ProjectPromptAddendumShowCommand.Settings { Project = ProjectName, Builder = builder },
            cancellationToken);
    }

    private async Task<int> ListExitCodeAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        return await ProjectPromptAddendumListCommand.RunAsync(
            session, new ProjectPromptAddendumListCommand.Settings { Project = ProjectName }, cancellationToken);
    }

    private async Task<(NodeContext Node, Guid ProjectId, string ProjectHome)> SeedAsync(CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cancellationToken);

        Guid projectId = DomainId.New();
        string projectHome = Path.Combine(_home, "projects", "hall9k");
        Directory.CreateDirectory(projectHome);

        await using IDocumentSession session = _postgres.Store.LightweightSession();

        // Idea 6be68ee2, trust-ledger finding 6: PushAsync now skips on a node whose owner is not
        // Owner-role, so every seeded node claims OwnerRootFingerprint — the same fingerprint
        // DefaultTrustChain recognizes as this project's own Owner-role member — to keep every
        // scenario above the dedicated owner-test cases behaving exactly as it did before that gate
        // shipped.
        OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(node.OwnerId, token: cancellationToken))!;
        session.Events.Append(node.OwnerId, OwnerDecider.ClaimRoot(owner, OwnerRootFingerprint, verified: true, Now));

        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), ProjectName, RepositoryPath, null, null, Now,
                homeDirectory: ProjectHome.Parse(projectHome)));
        await session.SaveChangesAsync(cancellationToken);

        return (node, projectId, projectHome);
    }
}
