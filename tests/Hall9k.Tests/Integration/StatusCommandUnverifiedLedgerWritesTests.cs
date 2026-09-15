using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k status</c> naming an unverifiable ledger writer (idea 202383dc, T1 criterion 3: "the
/// writer is named in h9k status") off the standing record
/// <c>Hall9k.Daemon.Messaging.MessageSweepEngine.PersistUnverifiedWritesAsync</c> writes — never a
/// live ledger chain walk from this command itself. This test persists the identical event that
/// sweep appends directly against a real Marten/Postgres store (Brian's 2026-09-13 testing rule),
/// then proves <see cref="StatusCommand.WriteUnverifiedLedgerWritesAsync"/> renders it with the
/// writer, the project, and the reason all on the line.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class StatusCommandUnverifiedLedgerWritesTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-status-unverified-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public StatusCommandUnverifiedLedgerWritesTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task A_persisted_unverifiable_write_is_named_with_its_project_and_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ProjectDetails project = await SeedProjectAsync(cts.Token);
        Guid droppedNodeId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            Guid streamId = UnverifiedLedgerWriteStreamId.For(project.Id, "vouch", droppedNodeId.ToString(), "owner-a-fingerprint");
            session.Events.StartStream<UnverifiedLedgerWriteAggregate>(
                streamId,
                UnverifiedLedgerWriteDecider.Observe(
                    project.Id, "vouch", droppedNodeId.ToString(), "owner-a-fingerprint",
                    "commit abc123 is not signed by root owner-a-fingerprint or any node currently enrolled in it", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteUnverifiedLedgerWritesAsync(session, cts.Token));
        }

        output.Should().Contain("vouch").And.Contain(droppedNodeId.ToString())
            .And.Contain(project.Name)
            .And.Contain("owner-a-fingerprint")
            .And.Contain("not signed by root owner-a-fingerprint");
    }

    [Fact]
    public async Task No_persisted_writes_renders_nothing()
    {
        await SeedProjectAsync(CancellationToken.None);

        string output;
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteUnverifiedLedgerWritesAsync(session, CancellationToken.None));
        }

        output.Should().BeEmpty("a quiet pane says nothing, the same posture every other pane in this command follows");
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
                projectId, context.OwnerId, DomainId.New(), "smoke", RepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }

    /// <summary>The global console, swapped for a writer and put back — mirrors LaunchLineWrappingTests's own capture.</summary>
    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 4096;
        AnsiConsole.Console = captured;
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}
