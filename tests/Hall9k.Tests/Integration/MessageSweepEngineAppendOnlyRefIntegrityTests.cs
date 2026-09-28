using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Connectors.Ledger;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="MessageSweepEngine"/>'s own append-only exact-ref integrity check
/// (<c>WithAppendOnlyExactRefIntegrityAsync</c>) against a REAL bare repository — the identical
/// throwaway hub/node pair <c>GitLedgerTests</c> and <c>LedgerAppendOnlyRefFetcherTests</c> already
/// use (Brian's 2026-09-13 rule) — rather than only through the pure, hand-built
/// <see cref="TrustChain.UnverifiedWrites"/> fixtures the rest of <c>MessageSweepEngineTests</c>
/// drives <see cref="MessageSweepEngine.PersistUnverifiedWritesAsync"/> with. Before this class, no
/// test drove <see cref="LedgerRefRegistry.Records"/> through a real <see cref="GitLedger.ReadAsync"/>
/// or through the sweep itself (independent pre-PR review, cycle 1, conformance lens, medium): a
/// regression dropping <c>records</c> from <see cref="LedgerRefRegistry.AppendOnlyExactRefs"/>, or
/// breaking the sweep's own fold into <see cref="TrustChain.UnverifiedWrites"/>, would have passed
/// the rest of the suite while a rewound <c>records</c> ref stopped reaching <c>h9k status</c>
/// entirely. Both this project's own README.md's <c>RealProcessSpawn</c> (real git) and
/// <c>RequiresDocker</c> (real Postgres) traits apply, since this scenario genuinely needs both.
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
[Trait("Category", "RequiresDocker")]
public sealed class MessageSweepEngineAppendOnlyRefIntegrityTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly LedgerTestRepo _repo = new();

    public MessageSweepEngineAppendOnlyRefIntegrityTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        _repo.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_sweep_catches_a_rewind_of_the_records_ref_and_persists_it_as_a_standing_refusal()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        string refName = LedgerRefRegistry.Records.RefspecSource;
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        (string keyPath, _) = GenerateSshKeypair();
        try
        {
            LedgerCommitter committer = new("Sweep Test", "sweep-test@hall9k.local");
            LedgerSigningKey signingKey = new(keyPath);
            GitLedger ledger = new(NullLogger<GitLedger>.Instance);

            // The identical rewind shape GitLedgerTests.WriteAsync_AfterAPureRewind_... reproduces,
            // driven here through GitLedger.ReadAsync against the real records ref so the sweep's
            // own fetch — not a hand-built TrustChain — is what has to catch it.
            await ledger.WriteAsync(
                new LedgerWriteRequest(node, refName, "records/rewind-test.yaml", "v1\n", null, "v1", committer, signingKey),
                cts.Token);
            (int c1Exit, string commit1Output, string c1Error) = LedgerTestRepo.RevParseQuiet(node, refName);
            c1Exit.Should().Be(0, c1Error);
            string commit1 = commit1Output.Trim();

            LedgerFile baseline = await ledger.ReadAsync(node, refName, "records/rewind-test.yaml", cts.Token);
            LedgerWriteOutcome second = await ledger.WriteAsync(
                new LedgerWriteRequest(node, refName, "records/rewind-test.yaml", "v2\n", baseline.BlobId, "v2", committer, signingKey),
                cts.Token);
            second.Verdict.Should().Be(LedgerWriteVerdict.Written);

            // Moves this node's own verified tip on to the second commit — the trust anchor the
            // rewind below tries to discard.
            LedgerFile afterSecond = await ledger.ReadAsync(node, refName, "records/rewind-test.yaml", cts.Token);
            afterSecond.Content.Should().Be("v2\n");

            (int rewindExit, _, string rewindError) = LedgerTestRepo.RunGit(hub, "update-ref", refName, commit1);
            rewindExit.Should().Be(0, rewindError);

            NodeContext nodeB = await NodeBootstrapSeed.NewIsolatedNodeAsync(_postgres.Store, cts.Token);
            Guid projectId = DomainId.New();
            await using (IDocumentSession claimSession = _postgres.Store.LightweightSession())
            {
                OwnerAggregate owner =
                    (await claimSession.Events.AggregateStreamAsync<OwnerAggregate>(nodeB.OwnerId, token: cts.Token))!;
                claimSession.Events.Append(
                    nodeB.OwnerId, OwnerDecider.ClaimRoot(owner, "owner-sweep-ref-integrity-fingerprint", verified: true, Now));

                claimSession.Events.StartStream<ProjectAggregate>(
                    projectId,
                    ProjectDecider.Register(
                        projectId, nodeB.OwnerId, DomainId.New(), "sweep-ref-integrity", node, null, null, Now));
                await claimSession.SaveChangesAsync(cts.Token);
            }

            InMemoryMessageTransport transport = new(new FakeLedger());
            MessageSweepEngine engine = new(
                _postgres.Store, nodeB, new MessageOutbox(transport), new MessageInbox(transport), transport,
                new FakeLedgerChainReader(TrustChain.Empty), new MessageNodeIdentityResolver(new NodeKeyStore()),
                Options.Create(new DaemonOptions()), NullLogger<MessageSweepEngine>.Instance,
                new EventReplicationOutbox(new ReplicationProjectResolver()), new EventReplicationInbox(transport),
                new EventCatchUpInbox(transport, new EventCatchUpResponder(new ReplicationProjectResolver(), new FakeLedger())),
                new EventCatchUpCoordinator());

            await engine.SweepOnceAsync(cts.Token);

            await using (IDocumentSession verifySession = _postgres.Store.LightweightSession())
            {
                IReadOnlyList<UnverifiedLedgerWriteDetails> observed = await verifySession.Query<UnverifiedLedgerWriteDetails>()
                    .Where(details => details.ProjectId == projectId)
                    .ToListAsync(cts.Token);
                observed.Should().ContainSingle(
                    write => write.Kind == "ref" && write.Identifier == refName && !write.Resolved,
                    "the sweep's own append-only ref integrity check must catch the rewind and persist it "
                    + "as the standing record h9k status reads");
            }
        }
        finally
        {
            File.Delete(keyPath);
            File.Delete($"{keyPath}.pub");
        }
    }

    private static (string PrivateKeyPath, string PublicKey) GenerateSshKeypair()
    {
        string keyPath = Path.Combine(Path.GetTempPath(), $"h9k-sweep-ref-integrity-signing-key-{Guid.NewGuid():N}");
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add("ed25519");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(keyPath);
        process.StartInfo.ArgumentList.Add("-N");
        process.StartInfo.ArgumentList.Add(string.Empty);
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add("sweep-ref-integrity-test");
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keygen failed: {output}{error}");
        }

        return (keyPath, File.ReadAllText($"{keyPath}.pub").Trim());
    }
}
