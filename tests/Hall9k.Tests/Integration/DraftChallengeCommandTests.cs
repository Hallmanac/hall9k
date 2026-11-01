using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task challenge</c> and the publish gate, run through the real command tree against a real store: the request is
/// recorded and returns, the refusals name their reasons, and a project that has recorded no value requires a verdict.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class DraftChallengeCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly string MyRoot = new('1', 64);
    private static readonly string JordanRoot = new('2', 64);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-challenge-command-").FullName;
    private BootstrapContext _me = null!;
    private Guid _jordanId;
    private Guid _projectId;

    public DraftChallengeCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            _me = await NodeBootstrap.EnsureAsync(session, cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(_me.OwnerId, token: cts.Token))!;
            session.Events.Append(_me.OwnerId, OwnerDecider.ClaimRoot(owner, MyRoot, verified: true, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        _jordanId = DomainId.New();
        OwnerAggregate jordan = new();
        OwnerRegistered registered = OwnerDecider.Register(_jordanId, "Jordan", "jordan@test.local", Now);
        jordan.Apply(registered);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<OwnerAggregate>(
                _jordanId, registered, OwnerDecider.ClaimRoot(jordan, JordanRoot, verified: true, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        _projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Store(new ProjectDetails
        {
            Id = _projectId,
            Name = "gate",
            RepositoryPath = _checkout,
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await projectSession.SaveChangesAsync(cts.Token);
    }

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        if (Directory.Exists(_checkout))
        {
            Directory.Delete(_checkout, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<Exception?> RunCliAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        // The override is flow-scoped, so it is opened where the command runs and not in the fixture's setup.
        using ScopedConnectionString connection = new(_postgres.ConnectionString);
        CommandApp app = new();
        app.Configure(config => CliCommandTree.Configure(config, _ => null));
        try
        {
            await app.RunAsync(arguments, cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception is CommandRuntimeException { InnerException: { } inner } ? inner : exception;
        }
    }

    private async Task<Guid> SeedDraftAsync(
        CancellationToken cancellationToken, TaskType? type = null, IReadOnlyList<string>? criteria = null,
        Guid? heldBy = null, params object[] more)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, _projectId, "Gate me", criteria ?? ["it is gated"], type ?? TaskType.Chore, null, null, null, Now, _me.OwnerId,
            spikeKind: type == TaskType.Spike ? SpikeKind.Research : null,
            exitCriterion: type == TaskType.Spike ? "one sentence" : null);
        List<object> events = [added, new TaskScopeSet(taskId, ReplicationScope.Team, Now, _me.OwnerId)];
        if (heldBy is { } holder)
        {
            events.Add(new TaskAssigneeSet(taskId, holder, JordanRoot, Now, _me.OwnerId));
        }

        events.AddRange(more);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<IReadOnlyList<object>> StreamAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return [.. (await query.Events.FetchStreamAsync(taskId, token: cancellationToken)).Select(recorded => recorded.Data)];
    }

    private async Task SetRequirementAsync(bool? value, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(_projectId, cancellationToken))!;
        project.RequireChallengeRecorded = value;
        session.Store(project);
        await session.SaveChangesAsync(cancellationToken);
    }

    // ---- the verb ----

    [Fact]
    public async Task The_verb_records_one_node_scoped_request_and_returns_without_waiting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(cts.Token);

        (await RunCliAsync(cts.Token, "task", "challenge", taskId.ToString())).Should().BeNull();

        (await StreamAsync(taskId, cts.Token)).OfType<DraftChallengeRequested>().Should().ContainSingle()
            .Which.RequestedByOwnerId.Should().Be(_me.OwnerId);
        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.ChallengeRequested.Should().BeTrue();

        Exception? again = await RunCliAsync(cts.Token, "task", "challenge", taskId.ToString());
        again.Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("already has a challenge");
    }

    [Fact]
    public async Task The_verb_is_refused_with_the_reason_for_a_published_task_a_missing_checkout_and_an_archived_project()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        Guid publishedId = await SeedDraftAsync(cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(publishedId, token: cts.Token))!;
            session.Events.Append(publishedId, TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "task", "challenge", publishedId.ToString()))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("not Draft");

        Guid draft = await SeedDraftAsync(cts.Token);
        Directory.Delete(_checkout, recursive: true);
        (await RunCliAsync(cts.Token, "task", "challenge", draft.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("no working checkout");
        Directory.CreateDirectory(_checkout);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            ProjectDetails project = (await session.LoadAsync<ProjectDetails>(_projectId, cts.Token))!;
            project.IsArchived = true;
            session.Store(project);
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "task", "challenge", draft.ToString()))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("archived");
    }

    [Fact]
    public async Task Someone_who_may_not_act_on_the_task_may_not_challenge_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(cts.Token, heldBy: _jordanId);

        Exception? refusal = await RunCliAsync(cts.Token, "task", "challenge", taskId.ToString());

        refusal.Should().BeOfType<DomainBusinessRuleException>();
        (await StreamAsync(taskId, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty();
    }

    // ---- the gate, through the real publish command ----

    [Fact]
    public async Task A_project_that_recorded_nothing_requires_a_verdict_and_the_refusal_names_all_three_ways_out()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(cts.Token);

        Exception? refusal = await RunCliAsync(cts.Token, "task", "publish", taskId.ToString(), "--no-queue");

        refusal.Should().BeOfType<DomainBusinessRuleException>().Which.Message
            .Should().Contain("h9k task challenge").And.Contain("--skip-challenge").And.Contain("--require-challenge");
        (await StreamAsync(taskId, cts.Token)).OfType<TaskPublished>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_stated_reason_publishes_past_the_gate_and_is_recorded_with_who_gave_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = await SeedDraftAsync(cts.Token);

        (await RunCliAsync(cts.Token, "task", "publish", taskId.ToString(), "--no-queue", "--skip-challenge", "a typo fix"))
            .Should().BeNull();

        TaskPublished published = (await StreamAsync(taskId, cts.Token)).OfType<TaskPublished>().Should().ContainSingle().Subject;
        published.ChallengeSkipReason.Should().Be("a typo fix");
        published.PublishedByOwnerId.Should().Be(_me.OwnerId);
        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.ChallengeSkipReason.Should().Be("a typo fix");
        details.ChallengeSkippedByOwnerId.Should().Be(_me.OwnerId);
    }

    [Fact]
    public async Task With_the_requirement_off_publish_asks_for_nothing_and_a_passing_verdict_needs_no_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SetRequirementAsync(false, cts.Token);
        Guid off = await SeedDraftAsync(cts.Token);
        (await RunCliAsync(cts.Token, "task", "publish", off.ToString(), "--no-queue")).Should().BeNull();

        await SetRequirementAsync(null, cts.Token);
        Guid withVerdict = await SeedDraftAsync(cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(withVerdict, new DraftChallengeRecorded(
                withVerdict, DomainId.New(), _me.NodeId, ChallengeVerdict.PublishAsWritten, ChallengeFailureReason.Unknown,
                0, 0, 1, AgentModel.ChallengeDefault, AgentEffort.High, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "task", "publish", withVerdict.ToString(), "--no-queue")).Should().BeNull();
    }

    [Fact]
    public async Task Project_set_records_the_requirement_and_publish_then_asks_for_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid registeredId = DomainId.New();
        string name = $"gate-set-{registeredId:N}";
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(registeredId, ProjectDecider.Register(
                registeredId, _me.OwnerId, DomainId.New(), name, _checkout, null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "project", "set", name, "--require-challenge", "sometimes"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("'on' or 'off'");
        (await RunCliAsync(cts.Token, "project", "set", name, "--require-challenge", "off")).Should().BeNull();

        await using IQuerySession query = _postgres.Store.QuerySession();
        ProjectDetails project = (await query.LoadAsync<ProjectDetails>(registeredId, cts.Token))!;
        project.RequireChallengeRecorded.Should().BeFalse();
        project.ChallengeRequirementSetting.Should().Be(new ChallengeRequirement(false, Recorded: true));
        (await query.Events.AggregateStreamAsync<ProjectAggregate>(registeredId, token: cts.Token))!
            .RequireChallengeRecorded.Should().BeFalse();
    }
}
