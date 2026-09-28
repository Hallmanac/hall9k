using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k status</c> naming a root-key rotation (idea 6be68ee2, PR B of the succession chain) off
/// the standing record <c>Hall9k.Daemon.Messaging.MessageSweepEngine.PersistRootRotationsAsync</c>
/// writes — never a live ledger chain walk from this command itself. Mirrors
/// <c>StatusCommandUnverifiedLedgerWritesTests</c>'s own shape exactly: this test persists the
/// identical events that sweep appends directly against a real Marten/Postgres store (Brian's
/// 2026-09-13 testing rule), then proves <see cref="StatusCommand.WriteRootRotationsAsync"/> renders
/// them.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class StatusCommandRootRotationsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string SecondRepositoryPath = "/does/not/matter/on/a/fake/ledger/second";
    private const string ThirdRepositoryPath = "/does/not/matter/on/a/fake/ledger/third";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public StatusCommandRootRotationsTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_live_rotation_reads_root_key_rotated_by_the_promoting_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ProjectDetails project = await SeedProjectAsync(RepositoryPath, "smoke", cts.Token);
        Guid promotedNodeId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            Guid streamId = RootRotationStreamId.For(project.Id, "owner-a-fingerprint", promotedNodeId);
            session.Events.StartStream<RootRotationAggregate>(
                streamId, RootRotationDecider.Observe(project.Id, "owner-a-fingerprint", promotedNodeId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(() => StatusCommand.WriteRootRotationsAsync(session, cts.Token));
        }

        output.Should().Contain("root key rotated by node").And.Contain(project.Name);
    }

    [Fact]
    public async Task A_voided_rotation_reads_revoked_by_an_earlier_root_key()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ProjectDetails project = await SeedProjectAsync(RepositoryPath, "smoke", cts.Token);
        Guid promotedNodeId = DomainId.New();
        Guid revokedByNodeId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            Guid streamId = RootRotationStreamId.For(project.Id, "owner-a-fingerprint", promotedNodeId);
            session.Events.StartStream<RootRotationAggregate>(
                streamId, RootRotationDecider.Observe(project.Id, "owner-a-fingerprint", promotedNodeId, Now));
            session.Events.Append(
                streamId, RootRotationDecider.Revoke(project.Id, "owner-a-fingerprint", promotedNodeId, revokedByNodeId, Now.AddMinutes(5)));
            await session.SaveChangesAsync(cts.Token);
        }

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(() => StatusCommand.WriteRootRotationsAsync(session, cts.Token));
        }

        output.Should().Contain("revoked by an earlier root key");
    }

    [Fact]
    public async Task A_promoted_node_names_the_project_still_missing_the_rotation()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ProjectDetails rotatedProject = await SeedProjectAsync(RepositoryPath, "smoke", cts.Token);
        ProjectDetails laggingProject = await SeedProjectAsync(SecondRepositoryPath, "smoke-second", cts.Token);

        Guid nodeId;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cts.Token);
            nodeId = context.NodeId;
            session.Store(new NodeRootKeyProjectDetails
            {
                Id = rotatedProject.Id, NodeId = nodeId, RootFingerprint = "owner-a-fingerprint", IsLiveRootKey = true, UpdatedAt = Now,
            });
            session.Store(new NodeRootKeyProjectDetails
            {
                Id = laggingProject.Id, NodeId = nodeId, RootFingerprint = "owner-a-fingerprint", IsLiveRootKey = false, UpdatedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(() => StatusCommand.WriteRootRotationsAsync(session, cts.Token));
        }

        output.Should().Contain($"rotation missing in '{laggingProject.Name}'").And.Contain("h9k owner promote");
        output.Should().NotContain($"rotation missing in '{rotatedProject.Name}'");
    }

    [Fact]
    public async Task An_archived_projects_own_rotation_and_gap_are_both_omitted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ProjectDetails rotatedProject = await SeedProjectAsync(RepositoryPath, "smoke-rotated", cts.Token);
        ProjectDetails archivedProject = await SeedProjectAsync(SecondRepositoryPath, "smoke-archived", cts.Token);
        ProjectDetails laggingProject = await SeedProjectAsync(ThirdRepositoryPath, "smoke-lagging", cts.Token);
        Guid promotedNodeId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            // The archived project's own rotation (idea 6be68ee2) is stale news the moment it is
            // archived — the sweep stops refreshing it, and h9k owner promote itself skips an
            // archived project outright, so this loop's own re-run hint could never be acted on
            // (independent pre-PR review, cycle 1, both lenses, medium).
            Guid streamId = RootRotationStreamId.For(archivedProject.Id, "owner-a-fingerprint", promotedNodeId);
            session.Events.StartStream<RootRotationAggregate>(
                streamId, RootRotationDecider.Observe(archivedProject.Id, "owner-a-fingerprint", promotedNodeId, Now));

            BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cts.Token);
            session.Store(new NodeRootKeyProjectDetails
            {
                Id = rotatedProject.Id, NodeId = context.NodeId, RootFingerprint = "owner-a-fingerprint", IsLiveRootKey = true, UpdatedAt = Now,
            });
            session.Store(new NodeRootKeyProjectDetails
            {
                Id = archivedProject.Id, NodeId = context.NodeId, RootFingerprint = "owner-a-fingerprint", IsLiveRootKey = false, UpdatedAt = Now,
            });
            session.Store(new NodeRootKeyProjectDetails
            {
                Id = laggingProject.Id, NodeId = context.NodeId, RootFingerprint = "owner-a-fingerprint", IsLiveRootKey = false, UpdatedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession archiveSession = _postgres.Store.LightweightSession())
        {
            ProjectAggregate project =
                (await archiveSession.Events.AggregateStreamAsync<ProjectAggregate>(archivedProject.Id, token: cts.Token))!;
            archiveSession.Events.Append(archivedProject.Id, ProjectDecider.Archive(project, null, Now, project.OwnerId));
            await archiveSession.SaveChangesAsync(cts.Token);
        }

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(() => StatusCommand.WriteRootRotationsAsync(session, cts.Token));
        }

        output.Should().NotContain(
            archivedProject.Name, "an archived project's own rotation and gap are both stale news nothing can act on");
        output.Should().Contain($"rotation missing in '{laggingProject.Name}'", "a still-live project's own gap must still be named");
    }

    [Fact]
    public async Task No_persisted_rotation_or_gap_renders_nothing()
    {
        await SeedProjectAsync(RepositoryPath, "smoke", CancellationToken.None);

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await ScopedAnsiConsoleCapture.CaptureAsync(() => StatusCommand.WriteRootRotationsAsync(session, CancellationToken.None));
        }

        output.Should().BeEmpty("a quiet pane says nothing, the same posture every other pane in this command follows");
    }

    private async Task<ProjectDetails> SeedProjectAsync(string repositoryPath, string name, CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(projectId, context.OwnerId, DomainId.New(), name, repositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }
}
