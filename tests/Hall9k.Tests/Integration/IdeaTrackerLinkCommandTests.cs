using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// An idea that carries a tracker link, against the real store: the capture and its refusals through
/// <c>IdeaAddCommand.RunAsync</c>, the project move, the promote refusal, and the cut rules through
/// <c>LinkedIdeaCut.ResolveAsync</c>. The importer is a fake provider that counts its reads and the ledger
/// is the in-memory fake, so no test touches a tracker, GitHub, or a branch.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class IdeaTrackerLinkCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly FakeLedger _ledger = new();
    private BootstrapContext _me = null!;
    private ProjectDetails _linear = null!;
    private ProjectDetails _plain = null!;
    private ProjectDetails _secondLinear = null!;

    public IdeaTrackerLinkCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        _me = await NodeBootstrap.EnsureAsync(session, cts.Token);
        _linear = NewProject("tracked-in-linear", LinearTeamKey.Parse("ENG"));
        _secondLinear = NewProject("also-in-linear", LinearTeamKey.Parse("OPS"));
        _plain = NewProject("tracked-elsewhere", LinearTeamKey.None);
        session.Store(_linear, _secondLinear, _plain);
        await session.SaveChangesAsync(cts.Token);
    }

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    private static ProjectDetails NewProject(string name, LinearTeamKey team) => new()
    {
        Id = DomainId.New(),
        Name = name,
        RepositoryPath = "/does/not/matter",
        BaseBranch = "main",
        BranchNameTemplate = BranchNameTemplate.Default,
        LinearTeam = team,
    };

    private sealed class FakeProvider(WorkItemProvider provider, Func<ImportedWorkItem> read) : IWorkItemProvider
    {
        public int Reads { get; private set; }

        public WorkItemProvider Provider => provider;

        public Task<ImportedWorkItem> ImportAsync(WorkItemImportRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(read());
        }

        public Uri? WebUrl(ExternalReference reference) => null;
    }

    private static ImportedWorkItem Linear(
        WorkItemStatus status, string identifier = "ENG-123", string title = "Add rate limiting",
        params ExternalReference[] former) =>
        new(new ExternalReference(WorkItemProvider.Linear, identifier), title, "Needs a limiter.", status,
            new Uri($"https://linear.app/hall9k/issue/{identifier}"), Now)
        {
            FormerReferences = former,
        };

    private static FakeProvider LinearProvider(ImportedWorkItem item) => new(WorkItemProvider.Linear, () => item);

    private async Task<Guid> CaptureAsync(
        FakeProvider provider, ProjectDetails project, string? text, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        await IdeaAddCommand.RunAsync(
            session,
            new IdeaAddCommand.Settings { FromLinear = "ENG-123", Project = project.Name, Text = text },
            new WorkItemImporter(provider), _ledger, cancellationToken);
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Query<IdeaDetails>().ToListAsync(cancellationToken))
            .OrderByDescending(idea => idea.CapturedAt).First().Id;
    }

    private async Task<IdeaAggregate> IdeaAsync(Guid ideaId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken))!;
    }

    private async Task<int> IdeaCountAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Query<IdeaDetails>().ToListAsync(cancellationToken)).Count;
    }

    [Fact]
    public async Task A_triage_issue_becomes_an_idea_in_discovery_with_the_link_and_the_workspace_file_in_one_transaction()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ImportedWorkItem triage = Linear(WorkItemStatus.Triage.As("Triage"), title: "Fix login timeout, resolves #500");
        FakeProvider provider = LinearProvider(triage);

        Guid ideaId = await CaptureAsync(provider, _linear, text: null, cts.Token);

        await using IQuerySession query = _postgres.Store.QuerySession();
        IReadOnlyList<JasperFx.Events.IEvent> events = await query.Events.FetchStreamAsync(ideaId, token: cts.Token);
        events.Select(e => e.Data.GetType()).Should().Equal(typeof(IdeaCaptured), typeof(IdeaWorkItemLinked));
        IdeaAggregate idea = await IdeaAsync(ideaId, cts.Token);
        idea.Text.Should().Be(TaskAddCommand.ObjectiveSeed(triage.Title)).And.NotContain("resolves #500");
        idea.State.Should().Be(IdeaState.Captured);
        idea.ProjectId.Should().Be(_linear.Id);
        idea.WorkItemReference.Should().Be(triage.Reference);
        idea.WorkItemObservedStatus.Should().Be("triage");
        idea.WorkItemObservedTitle.Should().Be(triage.Title);
        idea.WorkItemObservedAt.Should().Be(Now);
        IdeaDetails details = (await query.LoadAsync<IdeaDetails>(ideaId, cts.Token))!;
        details.WorkItemReference.Should().Be("linear:ENG-123");

        string file = Path.Combine(WorkspaceOf(idea), IdeaPaths.LinkedItemFileName);
        File.Exists(file).Should().BeTrue();
        (await File.ReadAllTextAsync(file, cts.Token)).Should().Be(WorkItemContext.Compose(triage))
            .And.Contain("quoted whole").And.Contain("https://linear.app/hall9k/issue/ENG-123");
        provider.Reads.Should().Be(1);
    }

    private static string WorkspaceOf(IdeaAggregate idea) =>
        IdeaPaths.WorkspaceDirectory(IdeaPaths.ResolveDirectory(
            idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id));

    [Fact]
    public async Task Text_given_wins_over_the_title()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(
            LinearProvider(Linear(WorkItemStatus.Open.As("Todo"))), _linear, "Throttle auth before launch", cts.Token);

        (await IdeaAsync(ideaId, cts.Token)).Text.Should().Be("Throttle auth before launch");
    }

    [Fact]
    public async Task A_link_needs_a_project_and_reads_nothing_without_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeProvider provider = LinearProvider(Linear(WorkItemStatus.Open));
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        Func<Task> add = () => IdeaAddCommand.RunAsync(
            session, new IdeaAddCommand.Settings { FromLinear = "ENG-123" }, new WorkItemImporter(provider), _ledger, cts.Token);

        (await add.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Contain("--project <name>");
        provider.Reads.Should().Be(0);
        (await IdeaCountAsync(cts.Token)).Should().Be(0);
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Canceled")]
    [InlineData("Duplicate")]
    public async Task A_finished_linear_issue_is_refused_with_its_state_named_and_nothing_is_written(string state)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        Func<Task> add = () => IdeaAddCommand.RunAsync(
            session, new IdeaAddCommand.Settings { FromLinear = "ENG-123", Project = _linear.Name },
            new WorkItemImporter(LinearProvider(Linear(WorkItemStatus.Closed.As(state)))), _ledger, cts.Token);

        (await add.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain($"{state} (closed)").And.Contain("was not open");
        (await IdeaCountAsync(cts.Token)).Should().Be(0);
    }

    [Fact]
    public async Task The_one_tracker_rule_refuses_a_link_the_project_does_not_track_before_any_read()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeProvider linear = LinearProvider(Linear(WorkItemStatus.Open));
        FakeProvider jira = new(WorkItemProvider.Jira, () => throw new InvalidOperationException("must not be read"));
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        Func<Task> linearOnPlain = () => IdeaAddCommand.RunAsync(
            session, new IdeaAddCommand.Settings { FromLinear = "ENG-123", Project = _plain.Name },
            new WorkItemImporter(linear), _ledger, cts.Token);
        Func<Task> jiraOnLinear = () => IdeaAddCommand.RunAsync(
            session, new IdeaAddCommand.Settings { FromJira = "PROJ-1", Project = _linear.Name },
            new WorkItemImporter(jira), _ledger, cts.Token);

        (await linearOnPlain.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("needs Linear to be the project's tracker");
        (await jiraOnLinear.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("tracks its work in Linear");
        linear.Reads.Should().Be(0);
        (await IdeaCountAsync(cts.Token)).Should().Be(0);
    }

    [Fact]
    public async Task One_item_is_one_idea_until_the_first_is_concluded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        ImportedWorkItem item = Linear(WorkItemStatus.Open.As("Todo"));
        Guid first = await CaptureAsync(LinearProvider(item), _linear, null, cts.Token);

        Func<Task> second = () => CaptureAsync(LinearProvider(item), _linear, null, cts.Token);
        (await second.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(first)).And.Contain("One item is one idea");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = await IdeaAsync(first, cts.Token);
            session.Events.Append(first, IdeaDecider.Conclude(idea, "Cut into tasks", Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid released = await CaptureAsync(LinearProvider(item), _linear, null, cts.Token);
        released.Should().NotBe(first, "concluding an idea releases its item with no further act");
    }

    [Fact]
    public async Task An_item_a_task_holds_is_refused_naming_the_task_and_a_former_name_counts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(
                taskId,
                TaskDecider.Add(
                    taskId, _linear.Id, "Add rate limiting to auth endpoints", ["429 past the limit"], TaskType.Feature,
                    null, null, new ExternalReference(WorkItemProvider.Linear, "ENG-9"), Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        ImportedWorkItem moved = Linear(
            WorkItemStatus.Open, identifier: "PLAT-9", former: new ExternalReference(WorkItemProvider.Linear, "ENG-9"));
        await using IDocumentSession add = _postgres.Store.LightweightSession();
        Func<Task> capture = () => IdeaAddCommand.RunAsync(
            add, new IdeaAddCommand.Settings { FromLinear = "PLAT-9", Project = _linear.Name },
            new WorkItemImporter(LinearProvider(moved)), _ledger, cts.Token);

        (await capture.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(taskId)).And.Contain("used to call linear:ENG-9");
        (await IdeaCountAsync(cts.Token)).Should().Be(0);
    }

    [Fact]
    public async Task Moving_a_linked_idea_is_held_to_the_destination_projects_tracker()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(LinearProvider(Linear(WorkItemStatus.Open)), _linear, null, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> toPlain = () => IdeaMoveCommand.RunAsync(
            session, new IdeaMoveCommand.Settings { Id = ideaId.ToString(), Project = _plain.Name }, cts.Token);
        (await toPlain.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("h9k idea move").And.Contain("needs Linear to be the project's tracker");
        (await IdeaAsync(ideaId, cts.Token)).ProjectId.Should().Be(_linear.Id);

        (await IdeaMoveCommand.RunAsync(
            session, new IdeaMoveCommand.Settings { Id = ideaId.ToString(), Project = _secondLinear.Name }, cts.Token))
            .Should().Be(ExitCodes.Ok);
        (await IdeaAsync(ideaId, cts.Token)).ProjectId.Should().Be(_secondLinear.Id);
    }

    [Fact]
    public async Task Promoting_a_linked_idea_is_refused_and_appends_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(LinearProvider(Linear(WorkItemStatus.Open)), _linear, null, cts.Token);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        Func<Task> promote = () => IdeaPromoteCommand.RunAsync(
            session, new IdeaPromoteCommand.Settings { Id = ideaId.ToString() },
            new FakeLedgerChainReader(TrustChain.Empty), new NodeKeyStore(), cts.Token);

        (await promote.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("h9k task add --from-idea").And.Contain("h9k idea conclude");
        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Events.FetchStreamAsync(ideaId, token: cts.Token)).Should().HaveCount(2);
    }

    private async Task<LinkedIdeaCut.Resolution> CutAsync(
        Guid ideaId, Func<WorkItemImporter> importer, bool unlinked, CancellationToken cancellationToken,
        ProjectDetails? project = null)
    {
        int calls = 0;
        IdeaAggregate idea = await IdeaAsync(ideaId, cancellationToken);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        LinkedIdeaCut.Resolution resolution = await LinkedIdeaCut.ResolveAsync(
            session, _ledger,
            _ =>
            {
                calls++;
                return Task.FromResult(importer());
            },
            idea, project ?? _linear, epicId: null, _me.NodeId, ownerRootFingerprint: null, unlinked, cancellationToken);
        _factoryCalls = calls;
        return resolution;
    }

    private int _factoryCalls;

    [Fact]
    public async Task The_first_cut_reads_the_item_afresh_and_adopts_it_even_while_it_is_still_in_triage()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(
            LinearProvider(Linear(WorkItemStatus.Triage.As("Triage"))), _linear, null, cts.Token);
        ImportedWorkItem fresh = Linear(
            WorkItemStatus.Triage.As("Triage"), identifier: "PLAT-7", title: "Add rate limiting, reworded");
        FakeProvider provider = LinearProvider(fresh);

        LinkedIdeaCut.Resolution cut = await CutAsync(ideaId, () => new WorkItemImporter(provider), false, cts.Token);

        cut.Item.Should().Be(fresh, "the reference the read returned is what the task records, not the one the idea stored");
        cut.Sibling.Should().BeNull();
        provider.Reads.Should().Be(1);
        _factoryCalls.Should().Be(1);
        LinkedIdeaCut.TriageNotice(fresh).Should().Contain("still in Triage").And.Contain("does not move it");
    }

    [Fact]
    public async Task Unlinked_reads_no_tracker()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(LinearProvider(Linear(WorkItemStatus.Open)), _linear, null, cts.Token);

        LinkedIdeaCut.Resolution cut = await CutAsync(
            ideaId, () => throw new InvalidOperationException("no tracker"), true, cts.Token);

        cut.Item.Should().BeNull();
        cut.Unlinked.Should().BeTrue();
        _factoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_closed_or_missing_item_is_refused_naming_the_cause_and_both_exits()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(LinearProvider(Linear(WorkItemStatus.Open)), _linear, null, cts.Token);
        string shortId = TaskListCommand.ShortId(ideaId);

        Func<Task> closed = () => CutAsync(
            ideaId, () => new WorkItemImporter(LinearProvider(Linear(WorkItemStatus.Closed.As("Done")))), false, cts.Token);
        Func<Task> missing = () => CutAsync(
            ideaId,
            () => new WorkItemImporter(new FakeProvider(
                WorkItemProvider.Linear, () => throw new DomainNotFoundException("Could not find ENG-123 in hall9k."))),
            false, cts.Token);

        string closedMessage = (await closed.Should().ThrowAsync<DomainValidationException>()).Which.Message;
        closedMessage.Should().Contain("Done (closed)")
            .And.Contain($"h9k task add --from-idea {shortId} --objective \"…\" --unlinked")
            .And.Contain($"h9k idea conclude {shortId}").And.Contain($"h9k idea archive {shortId}")
            .And.NotContain("link-linear", "that step belongs to a not-found refusal");
        string missingMessage = (await missing.Should().ThrowAsync<DomainNotFoundException>()).Which.Message;
        missingMessage.Should().Contain("Could not find ENG-123").And.Contain("--unlinked")
            .And.Contain("h9k task link-linear <new task> <new identifier>").And.Contain("h9k idea archive");
    }

    [Fact]
    public async Task No_connection_a_wrong_tracker_and_a_task_that_is_not_a_sibling_each_name_both_exits()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(LinearProvider(Linear(WorkItemStatus.Open)), _linear, null, cts.Token);
        WorkItemImporter noConnection = new(new FakeProvider(WorkItemProvider.Jira, () => Linear(WorkItemStatus.Open)))
        {
            Unusable = new Dictionary<WorkItemProvider, Func<DomainException>>
            {
                [WorkItemProvider.Linear] = () => new DomainNotFoundException(WorkItemConnections.NoLinearConnection),
            },
        };

        Func<Task> offline = () => CutAsync(ideaId, () => noConnection, false, cts.Token);
        Func<Task> wrongTracker = () => CutAsync(
            ideaId, () => new WorkItemImporter(LinearProvider(Linear(WorkItemStatus.Open))), false, cts.Token, _plain);

        string offlineMessage = (await offline.Should().ThrowAsync<DomainNotFoundException>()).Which.Message;
        offlineMessage.Should().Contain("No Linear connection").And.Contain("--unlinked").And.Contain("h9k idea conclude")
            .And.NotContain("link-linear <new task>");
        (await wrongTracker.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("needs Linear to be the project's tracker").And.Contain("--unlinked");

        Guid strangerTask = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(
                strangerTask,
                TaskDecider.Add(
                    strangerTask, _linear.Id, "Somebody else's work", ["done"], TaskType.Feature, null, null,
                    new ExternalReference(WorkItemProvider.Linear, "ENG-123"), Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        Func<Task> held = () => CutAsync(
            ideaId, () => new WorkItemImporter(LinearProvider(Linear(WorkItemStatus.Open))), false, cts.Token);
        (await held.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(strangerTask)).And.Contain("--unlinked").And.Contain("h9k idea archive");
    }

    [Fact]
    public async Task A_cut_whose_item_a_sibling_holds_is_made_unlinked_without_a_read_even_after_the_item_closed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await CaptureAsync(LinearProvider(Linear(WorkItemStatus.Open)), _linear, null, cts.Token);
        Guid siblingId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = await IdeaAsync(ideaId, cts.Token);
            TaskAdded added = TaskDecider.Add(
                siblingId, _linear.Id, "Build the limiter", ["429 past the limit"], TaskType.Feature, null, null,
                new ExternalReference(WorkItemProvider.Linear, "ENG-123"), Now, _me.OwnerId, sourceIdeaId: ideaId);
            session.Events.StartStream<TaskAggregate>(siblingId, added);
            session.Events.Append(ideaId, IdeaDecider.CutTask(idea, siblingId, added.Objective, Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        LinkedIdeaCut.Resolution cut = await CutAsync(
            ideaId, () => throw new InvalidOperationException("the item is closed and no tracker may be read"), false, cts.Token);

        cut.Item.Should().BeNull();
        cut.Sibling!.Id.Should().Be(siblingId);
        _factoryCalls.Should().Be(0);
        LinkedIdeaCut.SiblingNotice(await IdeaAsync(ideaId, cts.Token), cut.Sibling)
            .Should().Contain(TaskListCommand.ShortId(siblingId)).And.Contain("--untracked").And.Contain("h9k epic add");
    }
}
