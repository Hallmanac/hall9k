using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k owner set --display-name</c> (task e6744304) against a real Marten/Postgres session
/// (Owner is a real event stream), but never a real git repository: <see cref="FakeLedger"/> stands
/// in for A1, per Brian's 2026-09-13 testing rule.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class OwnerSetCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string SecondRepositoryPath = "/does/not/matter/on/a/fake/ledger/second";

    private readonly PostgresFixture _postgres;

    public OwnerSetCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task With_project_it_records_that_projects_own_entry_and_writes_its_node_file()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        ProjectDetails project = await SeedProjectAsync(node.OwnerId, "smoke", RepositoryPath);
        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, node.NodeId, RepositoryPath);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerSetCommand.Settings settings = new() { DisplayName = "Ada Lovelace", Project = project.Name };
        int exitCode = await OwnerSetCommand.RunAsync(session, settings, ledger, new NodeKeyStore(), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        LedgerWriteRequest write = ledger.Writes.Should().ContainSingle(w => w.CommitMessage == "Update node facts").Which;
        write.Content.Should().Contain("display_name: \"Ada Lovelace\"");

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.ProjectDisplayNames[project.Id].Value.Should().Be("Ada Lovelace");
        owner.DefaultDisplayName.Should().Be(DisplayName.None, "no --project means this is a per-project entry, not the default");
    }

    [Fact]
    public async Task Without_project_it_records_this_machines_default_and_reaches_every_joined_project()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        ProjectDetails first = await SeedProjectAsync(node.OwnerId, "first", RepositoryPath);
        ProjectDetails second = await SeedProjectAsync(node.OwnerId, "second", SecondRepositoryPath);
        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, node.NodeId, RepositoryPath);
        await SeedNodeFileAsync(ledger, node.NodeId, SecondRepositoryPath);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerSetCommand.Settings settings = new() { DisplayName = "Ada Lovelace" };
        int exitCode = await OwnerSetCommand.RunAsync(session, settings, ledger, new NodeKeyStore(), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        ledger.Writes.Count(w => w.CommitMessage == "Update node facts").Should().Be(2);
        ledger.Writes.Should().Contain(w =>
            w.CommitMessage == "Update node facts" && w.RepositoryPath == RepositoryPath
            && w.Content.Contains("display_name: \"Ada Lovelace\""));
        ledger.Writes.Should().Contain(w =>
            w.CommitMessage == "Update node facts" && w.RepositoryPath == SecondRepositoryPath
            && w.Content.Contains("display_name: \"Ada Lovelace\""));

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.DefaultDisplayName.Value.Should().Be("Ada Lovelace");
        owner.EffectiveDisplayName(first.Id).Value.Should().Be("Ada Lovelace");
        owner.EffectiveDisplayName(second.Id).Value.Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task A_projects_own_entry_takes_precedence_over_the_default_when_both_are_set()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        ProjectDetails first = await SeedProjectAsync(node.OwnerId, "first", RepositoryPath);
        ProjectDetails second = await SeedProjectAsync(node.OwnerId, "second", SecondRepositoryPath);
        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, node.NodeId, RepositoryPath);
        await SeedNodeFileAsync(ledger, node.NodeId, SecondRepositoryPath);

        await using IDocumentSession firstSession = _postgres.Store.LightweightSession();
        await OwnerSetCommand.RunAsync(
            firstSession, new OwnerSetCommand.Settings { DisplayName = "Default Name" }, ledger, new NodeKeyStore(),
            CancellationToken.None);

        await using IDocumentSession secondSession = _postgres.Store.LightweightSession();
        await OwnerSetCommand.RunAsync(
            secondSession, new OwnerSetCommand.Settings { DisplayName = "First Project Name", Project = first.Name },
            ledger, new NodeKeyStore(), CancellationToken.None);

        OwnerDetails owner = (await secondSession.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.EffectiveDisplayName(first.Id).Value.Should().Be("First Project Name");
        owner.EffectiveDisplayName(second.Id).Value.Should().Be("Default Name", "second has no entry of its own");

        ledger.Writes.Should().Contain(w => w.RepositoryPath == RepositoryPath && w.Content.Contains("display_name: \"First Project Name\""));
        ledger.Writes.Should().Contain(w => w.RepositoryPath == SecondRepositoryPath && w.Content.Contains("display_name: \"Default Name\""));
    }

    [Fact]
    public async Task Clearing_a_projects_own_entry_falls_back_to_the_default_in_its_node_file()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        ProjectDetails project = await SeedProjectAsync(node.OwnerId, "smoke", RepositoryPath);
        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, node.NodeId, RepositoryPath);

        await using IDocumentSession session1 = _postgres.Store.LightweightSession();
        await OwnerSetCommand.RunAsync(
            session1, new OwnerSetCommand.Settings { DisplayName = "Default Name" }, ledger, new NodeKeyStore(), CancellationToken.None);
        await using IDocumentSession session2 = _postgres.Store.LightweightSession();
        await OwnerSetCommand.RunAsync(
            session2, new OwnerSetCommand.Settings { DisplayName = "Project Name", Project = project.Name }, ledger,
            new NodeKeyStore(), CancellationToken.None);

        await using IDocumentSession session3 = _postgres.Store.LightweightSession();
        await OwnerSetCommand.RunAsync(
            session3, new OwnerSetCommand.Settings { DisplayName = "", Project = project.Name }, ledger, new NodeKeyStore(),
            CancellationToken.None);

        ledger.Writes[^1].RepositoryPath.Should().Be(RepositoryPath);
        ledger.Writes[^1].Content.Should().Contain("display_name: \"Default Name\"");

        OwnerDetails owner = (await session3.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.ProjectDisplayNames.Should().NotContainKey(project.Id);
        owner.EffectiveDisplayName(project.Id).Value.Should().Be("Default Name");
    }

    [Fact]
    public async Task Clearing_the_default_removes_the_line_from_a_project_with_no_entry_of_its_own()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        ProjectDetails project = await SeedProjectAsync(node.OwnerId, "smoke", RepositoryPath);
        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, node.NodeId, RepositoryPath);

        await using IDocumentSession session1 = _postgres.Store.LightweightSession();
        await OwnerSetCommand.RunAsync(
            session1, new OwnerSetCommand.Settings { DisplayName = "Default Name" }, ledger, new NodeKeyStore(), CancellationToken.None);

        await using IDocumentSession session2 = _postgres.Store.LightweightSession();
        int exitCode = await OwnerSetCommand.RunAsync(
            session2, new OwnerSetCommand.Settings { DisplayName = "" }, ledger, new NodeKeyStore(), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        ledger.Writes[^1].Content.Should().NotContain("display_name");

        OwnerDetails owner = (await session2.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.DefaultDisplayName.Should().Be(DisplayName.None);
        owner.EffectiveDisplayName(project.Id).Should().Be(DisplayName.None);
    }

    [Fact]
    public async Task One_projects_failed_write_does_not_stop_the_other_and_the_local_setting_is_still_saved()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        await SeedProjectAsync(node.OwnerId, "first", RepositoryPath);
        ProjectDetails second = await SeedProjectAsync(node.OwnerId, "second", SecondRepositoryPath);
        FakeLedger inner = new();
        await SeedNodeFileAsync(inner, node.NodeId, RepositoryPath);
        await SeedNodeFileAsync(inner, node.NodeId, SecondRepositoryPath);
        PerRepositoryFailingLedger ledger = new(inner, failingRepositoryPath: RepositoryPath);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerSetCommand.Settings settings = new() { DisplayName = "Ada Lovelace" };
        Func<Task> act = () => OwnerSetCommand.RunAsync(session, settings, ledger, new NodeKeyStore(), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*first*");
        inner.Writes.Should().ContainSingle(
            w => w.CommitMessage == "Update node facts" && w.RepositoryPath == SecondRepositoryPath,
            "the project that did not fail still landed");
        inner.Writes.Should().NotContain(w => w.CommitMessage == "Update node facts" && w.RepositoryPath == RepositoryPath);

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.DefaultDisplayName.Value.Should().Be("Ada Lovelace", "the local setting is saved regardless of the node-file failure");
        owner.EffectiveDisplayName(second.Id).Value.Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task A_project_this_node_has_not_joined_is_refused_and_nothing_is_recorded()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerSetCommand.Settings settings = new() { DisplayName = "Ada Lovelace", Project = "never-joined" };
        Func<Task> act = () => OwnerSetCommand.RunAsync(session, settings, ledger, new NodeKeyStore(), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*not*joined*");
        ledger.Writes.Should().BeEmpty();

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.DefaultDisplayName.Should().Be(DisplayName.None);
        owner.ProjectDisplayNames.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_value_is_refused_with_a_message_echoing_it_and_nothing_is_recorded()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, CancellationToken.None);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        OwnerSetCommand.Settings settings = new() { DisplayName = "Ada\nLovelace" };
        Func<Task> act = () => OwnerSetCommand.RunAsync(session, settings, ledger, new NodeKeyStore(), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*Ada?Lovelace*");
        ledger.Writes.Should().BeEmpty();

        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(node.OwnerId, CancellationToken.None))!;
        owner.DefaultDisplayName.Should().Be(DisplayName.None);
    }

    private async Task<ProjectDetails> SeedProjectAsync(Guid ownerId, string name, string repositoryPath)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            projectId, ProjectDecider.Register(projectId, ownerId, DomainId.New(), name, repositoryPath, null, null, Now));
        await session.SaveChangesAsync(CancellationToken.None);
        return (await session.LoadAsync<ProjectDetails>(projectId))!;
    }

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, string repositoryPath)
    {
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(nodeId, CancellationToken.None);
        string refName = $"refs/hall9k/ledger/nodes/{nodeId}";
        string path = $"nodes/{nodeId}/node.yaml";
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"{key.PublicKeyLine}\"\nowner_fingerprint: \"root\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, ExpectedBlobId: null, "seed node file",
                new LedgerCommitter("Test Node", "node@test.local"), new LedgerSigningKey(key.PrivateKeyPath)),
            CancellationToken.None);
    }

    /// <summary>Wraps a real <see cref="FakeLedger"/> and refuses every write bound for one named
    /// repository the way a push that keeps losing does, while every other repository behaves
    /// normally: the shape needed to prove one project's failed write never stops another's.</summary>
    private sealed class PerRepositoryFailingLedger(FakeLedger inner, string failingRepositoryPath) : ILedger
    {
        public List<LedgerWriteRequest> Writes => inner.Writes;

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken) =>
            inner.ReadAsync(repositoryPath, refName, path, cancellationToken);

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            request.RepositoryPath == failingRepositoryPath
                ? throw new LedgerPushRejectedException(request.RefName, 5, "rejected")
                : inner.WriteAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            inner.WriteManyAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(
            string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
    }
}
