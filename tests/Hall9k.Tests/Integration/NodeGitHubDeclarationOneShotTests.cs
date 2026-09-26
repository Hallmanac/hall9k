using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Daemon;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The daemon's one-shot GitHub declaration for a node that joined before node files carried one,
/// against a real Marten store for the connection's observed identity and a <see cref="FakeLedger"/>.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class NodeGitHubDeclarationOneShotTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private static readonly LedgerCommitter Committer = new("Test", "test@hall9k.local");
    private static readonly LedgerSigningKey SigningKey = new("/keys/id_ed25519");

    private const string OldFile =
        "node_id: \"n\"\npublic_key: \"ssh-ed25519 AAAAkey node\"\nowner_fingerprint: \"root\"\ninvite_proof: \"pending-proof\"\n";

    private readonly PostgresFixture _postgres;

    public NodeGitHubDeclarationOneShotTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_existing_node_file_gains_the_declaration_and_keeps_its_pending_invite_proof()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: true);
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, OldFile);

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        LedgerWriteRequest write = ledger.Writes[^1];
        write.CommitMessage.Should().Be("Update node facts");
        write.SigningKey.Should().Be(SigningKey);
        write.Content.Should().Be(OldFile + "github_login: \"test-user\"\ngithub_account_id: \"1\"\n");
    }

    [Fact]
    public async Task A_node_file_that_already_matches_is_not_rewritten()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: true);
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, OldFile + "github_login: \"test-user\"\ngithub_account_id: \"1\"\n");
        int writesBefore = ledger.Writes.Count;

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesBefore);
    }

    [Fact]
    public async Task No_node_file_is_created_for_a_project_this_node_has_no_file_in()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: true);
        FakeLedger ledger = new();

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_with_no_observed_github_account_leaves_the_file_untouched()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: false);
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, OldFile);
        int writesBefore = ledger.Writes.Count;

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesBefore);
    }

    [Fact]
    public async Task A_failed_push_is_swallowed_and_not_retried_within_the_same_process()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: true);
        RejectingLedger ledger = new(await LedgerWithNodeFileAsync(node, OldFile));
        NodeGitHubDeclarationOneShot oneShot = OneShot(node, ledger);

        await oneShot.RunOnceAsync(project, Identity(), CancellationToken.None);
        await oneShot.RunOnceAsync(project, Identity(), CancellationToken.None);

        ledger.WriteAttempts.Should().Be(1, "a failure waits for the next daemon start rather than retrying every tick");
    }

    private NodeGitHubDeclarationOneShot OneShot(NodeContext node, ILedger ledger) =>
        new(_postgres.Store, node, ledger, NullLogger<NodeGitHubDeclarationOneShot>.Instance);

    private static MessageNodeIdentity Identity() => new("root", Committer, SigningKey);

    private static async Task<FakeLedger> LedgerWithNodeFileAsync(NodeContext node, string content)
    {
        FakeLedger ledger = new();
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{node.NodeId}", $"nodes/{node.NodeId}/node.yaml", content, null,
                "seed", Committer, SigningKey),
            CancellationToken.None);
        return ledger;
    }

    private async Task<(NodeContext Node, ProjectDetails Project)> SeedAsync(bool connectionHasIdentity)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        Guid connectionId = (await NodeBootstrap.EnsureAsync(bootstrapSession, CancellationToken.None)).ConnectionId;
        if (!connectionHasIdentity)
        {
            connectionId = DomainId.New();
            await using IDocumentSession connectionSession = _postgres.Store.LightweightSession();
            ConnectionRegistered registered = ConnectionDecider.Register(
                connectionId, Guid.Empty, WorkItemProvider.GitHub, "placeholder", CredentialReference.GhCli, Now);
            connectionSession.Events.StartStream<ConnectionAggregate>(connectionId, registered);
            await connectionSession.SaveChangesAsync();
        }

        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(projectId, node.OwnerId, connectionId, "smoke", RepositoryPath, null, null, Now));
        await session.SaveChangesAsync();
        return (node, (await session.LoadAsync<ProjectDetails>(projectId))!);
    }

    /// <summary>Wraps a ledger and refuses every write the way a push that keeps losing does.</summary>
    private sealed class RejectingLedger(FakeLedger inner) : ILedger
    {
        public int WriteAttempts { get; private set; }

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken)
        {
            WriteAttempts++;
            throw new LedgerPushRejectedException(request.RefName, 5, "rejected");
        }

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
