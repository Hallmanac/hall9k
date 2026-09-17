using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon;
using Hall9k.Daemon.PromptAddenda;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
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
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class PromptAddendaSweepEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string ProjectName = "hall9k";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-prompt-addenda-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public PromptAddendaSweepEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Set_show_list_remove_round_trip_and_a_second_set_replaces_the_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance);
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
                cts.Token);
            exitCode.Should().Be(ExitCodes.Ok);
        }

        await engine.SweepOnceAsync(cts.Token);

        stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.PromptAddenda.RefspecSource, LedgerRefRegistry.PromptAddendumPath("work"),
            cts.Token);
        stored.Exists.Should().BeFalse();
        File.Exists(materializedFile).Should().BeFalse();
    }

    [Fact]
    public async Task An_over_cap_set_stops_naming_the_cap_and_succeeds_with_over_cap_recording_the_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string projectHome) = await SeedAsync(cts.Token);
        FakeLedger ledger = new();
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance);
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
        PromptAddendaSweepEngine engine = new(
            _postgres.Store, node, ledger, new NodeKeyStore(), NullLogger<PromptAddendaSweepEngine>.Instance);

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
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), ProjectName, RepositoryPath, null, null, Now,
                homeDirectory: ProjectHome.Parse(projectHome)));
        await session.SaveChangesAsync(cancellationToken);

        return (node, projectId, projectHome);
    }
}
