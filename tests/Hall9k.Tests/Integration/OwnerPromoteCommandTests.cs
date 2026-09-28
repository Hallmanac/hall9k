using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k owner promote</c> (idea 6be68ee2, PR B of the succession chain) against a real
/// Marten/Postgres session — Owner and Node are real event streams — but never a real git
/// repository: <see cref="FakeLedger"/> stands in for A1 and <see cref="FakeLedgerChainReader"/>
/// stands in for the chain reader, and <see cref="FakeInteractiveConfirmation"/> stands in for the
/// house interactive-confirmation check, per Brian's 2026-09-13 testing rule and the identical
/// "drive it over fakes" shape <see cref="NodeVouchCommand"/> and <see cref="NodeRevokeCommand"/>
/// already use.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class OwnerPromoteCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string SecondRepositoryPath = "/does/not/matter/on/a/fake/ledger/second";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public OwnerPromoteCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Refuses_before_any_push_when_this_node_is_revoked()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine, [],
                    RevokedNodeIds: new HashSet<string> { context.NodeId.ToString() }),
            },
            []));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        Func<Task> act = () => OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*revoked*");
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_before_any_push_when_this_node_is_not_vouched_anywhere()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (_, _, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [rootKey.Fingerprint] = new(rootKey.Fingerprint, rootKey.PublicKeyLine, []) }, []));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        Func<Task> act = () => OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*not currently vouched*");
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_before_any_push_when_this_node_is_vouched_but_has_no_successor_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine,
                    [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)]),
            },
            []));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        Func<Task> act = () => OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*no successor record*");
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_when_this_node_already_holds_a_live_root_key_everywhere()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine,
                    [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)],
                    RootKeys:
                    [
                        new LiveRootKey(rootKey.PublicKeyLine, rootKey.Fingerprint, null),
                        new LiveRootKey(key.PublicKeyLine, key.Fingerprint, context.NodeId.ToString()),
                    ]),
            },
            []));

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        Func<Task> act = () => OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*already holds a live root key*");
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_in_a_non_interactive_session_without_yes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = EligibleChainReader(context, key, rootKey);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = false };
        FakeInteractiveConfirmation confirmation = new(isInteractive: false, confirmResult: true);
        int exitCode = await OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), confirmation, cts.Token);

        exitCode.Should().Be(ExitCodes.Error, "a session with no terminal to ask on must refuse without --yes");
        ledger.Writes.Should().BeEmpty("nothing is touched when the confirmation is refused");
        confirmation.ConfirmCalls.Should().Be(0, "the house Interactive check is read before ever prompting");
    }

    [Fact]
    public async Task Refuses_when_interactive_but_the_operator_declines()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = EligibleChainReader(context, key, rootKey);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = false };
        FakeInteractiveConfirmation confirmation = new(isInteractive: true, confirmResult: false);
        int exitCode = await OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), confirmation, cts.Token);

        exitCode.Should().Be(ExitCodes.Error);
        ledger.Writes.Should().BeEmpty();
        confirmation.ConfirmCalls.Should().Be(1);
    }

    [Fact]
    public async Task Proceeds_without_prompting_when_yes_is_passed_even_in_a_non_interactive_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();
        FakeLedgerChainReader chainReader = EligibleChainReader(context, key, rootKey);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        FakeInteractiveConfirmation confirmation = new(isInteractive: false, confirmResult: false);
        int exitCode = await OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), confirmation, cts.Token);

        exitCode.Should().Be(ExitCodes.Ok);
        confirmation.ConfirmCalls.Should().Be(0, "--yes skips the prompt entirely");
        LedgerWriteRequest write = ledger.Writes.Single(
            w => w.RefName == $"refs/hall9k/ledger/owners/{rootKey.Fingerprint}"
                && w.Path == $"owners/{rootKey.Fingerprint}/rotations/1.yaml");
        write.Content.Should().Contain(context.NodeId.ToString()).And.Contain(key.PublicKeyLine).And.Contain(rootKey.PublicKeyLine);
    }

    [Fact]
    public async Task An_unreadable_project_in_the_dry_run_is_named_and_forces_a_non_zero_exit_even_though_the_other_project_lands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        await SeedSecondProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();

        TrustChain eligibleChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine,
                    [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)],
                    SuccessorNodeIds: [context.NodeId.ToString()]),
            },
            []);

        // The second project's own fetch fails during the dry run — this must be named and force a
        // non-zero exit, never silently dropped with a bare `continue` while the first project's
        // own promotion still lands and the closing summary reports full success (independent
        // pre-PR review, cycle 1, both lenses, medium).
        FakeLedgerChainReader chainReader = new(path => path == SecondRepositoryPath
            ? throw new InvalidOperationException("git ls-remote failed: could not resolve host")
            : eligibleChain);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        Func<Task> act = () => OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*failed in 1: smoke-second*");
        ledger.Writes.Should().ContainSingle(
            w => w.RepositoryPath == RepositoryPath && w.Path == $"owners/{rootKey.Fingerprint}/rotations/1.yaml",
            "the reachable project's own promotion must still land");
    }

    [Fact]
    public async Task A_fan_out_skips_an_ineligible_project_and_an_idempotent_rerun_writes_only_the_missing_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        await SeedSecondProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();

        // First run: eligible in the first project, not yet vouched in the second.
        FakeLedgerChainReader firstAttempt = new(new Dictionary<string, TrustChain>
        {
            [RepositoryPath] = new(
                new Dictionary<string, TrustedOwner>
                {
                    [rootKey.Fingerprint] = new(
                        rootKey.Fingerprint, rootKey.PublicKeyLine,
                        [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)],
                        SuccessorNodeIds: [context.NodeId.ToString()]),
                },
                []),
            [SecondRepositoryPath] = new(
                new Dictionary<string, TrustedOwner> { [rootKey.Fingerprint] = new(rootKey.Fingerprint, rootKey.PublicKeyLine, []) }, []),
        });

        await using IDocumentSession firstSession = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        int firstExitCode = await OwnerPromoteCommand.RunAsync(
            firstSession, settings, ledger, firstAttempt, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        firstExitCode.Should().Be(ExitCodes.Ok, "the first project's own promotion must land even though the second is not ready yet");
        ledger.Writes.Should().ContainSingle(
            w => w.RefName == $"refs/hall9k/ledger/owners/{rootKey.Fingerprint}"
                && w.Path == $"owners/{rootKey.Fingerprint}/rotations/1.yaml");

        // Second run (the operator re-runs after fixing the second project — e.g. it caught up on
        // its own vouch and successor records): the first project's own copy now shows this node's
        // key as a live root key already, and the second project is now eligible.
        FakeLedgerChainReader secondAttempt = new(new Dictionary<string, TrustChain>
        {
            [RepositoryPath] = new(
                new Dictionary<string, TrustedOwner>
                {
                    [rootKey.Fingerprint] = new(
                        rootKey.Fingerprint, rootKey.PublicKeyLine,
                        [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)],
                        RootKeys:
                        [
                            new LiveRootKey(rootKey.PublicKeyLine, rootKey.Fingerprint, null),
                            new LiveRootKey(key.PublicKeyLine, key.Fingerprint, context.NodeId.ToString()),
                        ]),
                },
                []),
            [SecondRepositoryPath] = new(
                new Dictionary<string, TrustedOwner>
                {
                    [rootKey.Fingerprint] = new(
                        rootKey.Fingerprint, rootKey.PublicKeyLine,
                        [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)],
                        SuccessorNodeIds: [context.NodeId.ToString()]),
                },
                []),
        });

        await using IDocumentSession secondSession = _postgres.Store.LightweightSession();
        int secondExitCode = await OwnerPromoteCommand.RunAsync(
            secondSession, settings, ledger, secondAttempt, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        secondExitCode.Should().Be(ExitCodes.Ok);
        ledger.Writes.Should().ContainSingle(
            w => w.RefName == $"refs/hall9k/ledger/owners/{rootKey.Fingerprint}"
                && w.Path == $"owners/{rootKey.Fingerprint}/rotations/1.yaml" && w.RepositoryPath == SecondRepositoryPath,
            "the retry must write only to the project still missing the rotation");
        ledger.Writes.Should().HaveCount(2, "the already-rotated first project must not be written to again");
    }

    [Fact]
    public async Task The_compare_and_swap_loser_is_refused_and_never_retried()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger fakeLedger = new();

        // Another heir's rotation lands in the exact instant this node's own attempt reads the
        // rotations prefix to pick its own next open slot — both computed the identical starting
        // ledger state (nothing under rotations/ yet), the "two valid promotions" race
        // (HALL9K-P2P-DESIGN.md §6.5).
        LedgerWriteRequest racingWrite = new(
            RepositoryPath, $"refs/hall9k/ledger/owners/{rootKey.Fingerprint}",
            $"owners/{rootKey.Fingerprint}/rotations/1.yaml",
            "node_id: \"a-different-heir\"\npublic_key: \"ssh-ed25519 AAAA other\"\nsupersedes_public_key: \""
            + $"{rootKey.PublicKeyLine}\"\n",
            ExpectedBlobId: null, "Rotate to a different heir",
            new LedgerCommitter("Other Heir", "other@test.local"), new LedgerSigningKey("/does/not/matter/key"));
        RacingLedger ledger = new(fakeLedger, racingWrite);

        FakeLedgerChainReader chainReader = EligibleChainReader(context, key, rootKey);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        Func<Task> act = () => OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*Nothing was promoted*");
        fakeLedger.Writes.Should().ContainSingle(
            w => w.Path == $"owners/{rootKey.Fingerprint}/rotations/1.yaml",
            "the other heir's own write is the only one that ever landed there — this node's own attempt must "
            + "never overwrite it, and never retry at a later slot");
    }

    [Fact]
    public async Task A_revoked_rotations_file_never_permanently_occupies_the_next_slot()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        await SeedProjectAsync(cts.Token);
        (BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) = await EstablishSurvivingNodeAsync(cts.Token);
        FakeLedger ledger = new();

        // Heir A's own rotation landed at rotations/1.yaml and was later voided (h9k node revoke
        // undoing a hijacked or otherwise bad promotion) — the file itself is never removed from
        // the ledger, only its content stops counting toward the chain's own live root-key set, so
        // this project's own RootKeys collapses back to [K0] alone even though rotations/1.yaml
        // still physically occupies that path (independent pre-PR review, cycle 1, both lenses,
        // high).
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/owners/{rootKey.Fingerprint}",
                $"owners/{rootKey.Fingerprint}/rotations/1.yaml",
                "node_id: \"heir-a\"\npublic_key: \"ssh-ed25519 AAAA heir-a\"\nsupersedes_public_key: \""
                + $"{rootKey.PublicKeyLine}\"\n",
                ExpectedBlobId: null, "Rotate to heir A",
                new LedgerCommitter("Heir A", "heir-a@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            cts.Token);

        // This node's own chain, as it reads today: only K0 is a live root key (heir A's own
        // rotation no longer counts), exactly the state a real GitLedgerChainReader would report
        // once heir A is revoked.
        FakeLedgerChainReader chainReader = EligibleChainReader(context, key, rootKey);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerPromoteCommand.Settings settings = new() { Yes = true };
        int exitCode = await OwnerPromoteCommand.RunAsync(
            session, settings, ledger, chainReader, new NodeKeyStore(), new FakeInteractiveConfirmation(true, true), cts.Token);

        exitCode.Should().Be(ExitCodes.Ok, "a stale, voided rotation file at the next slot must never permanently block every later promotion");
        ledger.Writes.Should().ContainSingle(
            w => w.Path == $"owners/{rootKey.Fingerprint}/rotations/2.yaml",
            "the next open slot is past every file currently in the ledger, not this project's own count of validated root keys");
    }

    /// <summary>Wraps a <see cref="FakeLedger"/> and lands <paramref name="racingWrite"/> the
    /// instant <see cref="OwnerPromoteCommand"/>'s own write attempt reads the rotations prefix to
    /// pick its own next open slot — the same window a genuine "two nodes race the identical
    /// starting ledger state" scenario needs (HALL9K-P2P-DESIGN.md §6.5): the read this wraps still
    /// sees the ledger as it stood before the race, computes its own slot from that, and only then
    /// discovers, on its own write, that the identical slot is already gone.</summary>
    private sealed class RacingLedger(FakeLedger inner, LedgerWriteRequest racingWrite) : ILedger
    {
        private bool raced;

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

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public async Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(
            string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken)
        {
            IReadOnlyList<LedgerEntry> snapshot = await inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
            if (!raced)
            {
                raced = true;
                await inner.WriteAsync(racingWrite, cancellationToken);
            }

            return snapshot;
        }
    }

    /// <summary>The identical eligible-to-promote chain (vouched, listed successor, RootKeys=[K0]
    /// only) every straightforwardly-successful test above shares.</summary>
    private static FakeLedgerChainReader EligibleChainReader(BootstrapContext context, NodeSigningKey key, NodeSigningKey rootKey) =>
        new(new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [rootKey.Fingerprint] = new(
                    rootKey.Fingerprint, rootKey.PublicKeyLine,
                    [new TrustedNode(context.NodeId.ToString(), key.PublicKeyLine, key.Fingerprint, Now)],
                    SuccessorNodeIds: [context.NodeId.ToString()]),
            },
            []));

    /// <summary>Bootstraps this node and claims a DIFFERENT node's key as this owner's root — the
    /// shape a surviving heir actually has: its own owner root is K0, a key this node never holds,
    /// only ever a vouched successor candidate for.</summary>
    private async Task<(BootstrapContext Context, NodeSigningKey Key, NodeSigningKey RootKey)> EstablishSurvivingNodeAsync(
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);
        NodeSigningKey rootKey = await new NodeKeyStore().EnsureAsync(Guid.NewGuid(), cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, rootKey.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (context, key, rootKey);
    }

    private async Task SeedProjectAsync(CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(projectId, context.OwnerId, DomainId.New(), "smoke", RepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedSecondProjectAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(projectId, context.OwnerId, DomainId.New(), "smoke-second", SecondRepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);
    }
}
