using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Daemon;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Owner;
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
    public async Task A_connection_with_no_observed_github_account_leaves_the_file_untouched()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: false);
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, OldFile);
        int writesBefore = ledger.Writes.Count;

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesBefore);
    }

    [Fact]
    public async Task A_stale_display_name_is_brought_up_to_the_effective_value_alongside_the_github_declaration()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: true);
        await SeedDefaultDisplayNameAsync(node, "New Name");
        string stale = OldFile.Replace("invite_proof", "display_name: \"Old Name\"\ninvite_proof");
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, stale);

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        LedgerWriteRequest write = ledger.Writes[^1];
        write.CommitMessage.Should().Be("Update node facts");
        write.Content.Should().Contain("display_name: \"New Name\"").And.Contain("github_login: \"test-user\"");
    }

    [Fact]
    public async Task A_node_with_no_effective_display_name_leaves_an_undeclared_file_untouched_on_that_field()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: false);
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, OldFile);
        int writesBefore = ledger.Writes.Count;

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        ledger.Writes.Should().HaveCount(writesBefore, "no GitHub account and no display name means nothing to write");
    }

    /// <summary>
    /// The branch a missing GitHub account no longer bails out of before this task: the display name
    /// refresh runs, and writes, on its own regardless of whether the GitHub declaration had
    /// anything to say (independent pre-PR review, cycle 1, both lenses, test-hygiene finding: the
    /// prior version of this test only duplicated <see cref="A_connection_with_no_observed_github_account_leaves_the_file_untouched"/>
    /// and would still have passed with the removed early return restored).
    /// </summary>
    [Fact]
    public async Task A_display_name_is_written_even_with_no_observed_github_account()
    {
        (NodeContext node, ProjectDetails project) = await SeedAsync(connectionHasIdentity: false);
        await SeedDefaultDisplayNameAsync(node, "New Name");
        FakeLedger ledger = await LedgerWithNodeFileAsync(node, OldFile);

        await OneShot(node, ledger).RunOnceAsync(project, Identity(), CancellationToken.None);

        LedgerWriteRequest write = ledger.Writes[^1];
        write.CommitMessage.Should().Be("Update node facts");
        write.Content.Should().Contain("display_name: \"New Name\"").And.NotContain("github_login");
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

    private static MessageNodeIdentity Identity() => new("root", Committer, SigningKey, "ssh-ed25519 AAAAkey node");

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

    private async Task SeedDefaultDisplayNameAsync(NodeContext node, string name)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(node.OwnerId, token: CancellationToken.None)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        session.Events.Append(
            node.OwnerId,
            OwnerDecider.ChangeSettings(
                owner, Optional<ReviewRerequestPolicy>.None, Now,
                defaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse(name))));
        await session.SaveChangesAsync();
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
