using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
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
/// <c>h9k plan start</c>, <c>show</c>, <c>list</c>, <c>resume</c>, and <c>cancel</c>, run through the real command tree against a
/// real store: the plan is recorded on a stream of its own and the command returns, every refusal names its reason before any
/// session exists, and the plan's own verbs refuse what they cannot do.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PlanCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-plan-command-").FullName;
    private BootstrapContext _me = null!;
    private Guid _projectId;

    public PlanCommandTests(PostgresFixture postgres) => _postgres = postgres;

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

        _projectId = await SeedProjectAsync("plans", skipPermissions: true, cts.Token);
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

    private async Task<Guid> SeedProjectAsync(string name, bool skipPermissions, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = name,
            RepositoryPath = _checkout,
            BaseBranch = "main",
            SkipPermissions = skipPermissions,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
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
        CancellationToken cancellationToken, Guid? projectId = null, string? context = "Pointers.", IReadOnlyList<string>? criteria = null)
    {
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId ?? _projectId, "Plan me", criteria ?? ["it is planned"], TaskType.Feature, context, null, null, Now, _me.OwnerId);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, added, new TaskScopeSet(taskId, Hall9k.Domain.Shared.ValueObjects.ReplicationScope.Team, Now, _me.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<IReadOnlyList<PlanDetails>> PlansAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return await query.Query<PlanDetails>().ToListAsync(cancellationToken);
    }

    [Fact]
    public async Task Start_records_one_plan_on_a_stream_of_its_own_and_returns_creating_no_run_lease_or_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid first = await SeedDraftAsync(cts.Token);
        Guid second = await SeedDraftAsync(cts.Token);

        (await RunCliAsync(cts.Token, "plan", "start", first.ToString(), second.ToString())).Should().BeNull();

        PlanDetails plan = (await PlansAsync(cts.Token)).Should().ContainSingle().Subject;
        plan.ProjectId.Should().Be(_projectId);
        plan.Drafts.Select(draft => draft.DraftId).Should().Equal(first, second);
        plan.Drafts.Should().OnlyContain(draft => draft.Phase == PlanPhase.Refinement && draft.Round == 0);
        plan.StartedByOwnerId.Should().Be(_me.OwnerId);
        plan.State.Should().Be(PlanState.Live);

        await using IQuerySession query = _postgres.Store.QuerySession();
        IReadOnlyList<object> stream = [.. (await query.Events.FetchStreamAsync(plan.Id, token: cts.Token)).Select(recorded => recorded.Data)];
        stream.Should().ContainSingle().Which.Should().BeOfType<PlanStarted>();
        foreach (Guid draft in new[] { first, second })
        {
            (await query.Events.FetchStreamAsync(draft, token: cts.Token)).Select(recorded => recorded.Data)
                .Should().NotContain(@event => @event is DraftRefinementDispatched || @event is DraftChallengeRequested, "no session starts here");
        }
    }

    [Fact]
    public async Task Start_at_challenge_is_recorded_and_refused_for_a_draft_with_no_criteria()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid bare = await SeedDraftAsync(cts.Token, criteria: []);
        Guid ready = await SeedDraftAsync(cts.Token);

        (await RunCliAsync(cts.Token, "plan", "start", bare.ToString(), "--at", "challenge"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("no acceptance criteria");
        (await PlansAsync(cts.Token)).Should().BeEmpty();

        (await RunCliAsync(cts.Token, "plan", "start", ready.ToString(), "--at", "challenge")).Should().BeNull();
        PlanDetails plan = (await PlansAsync(cts.Token)).Should().ContainSingle().Subject;
        (plan.StartPhase, plan.Drafts[0].Phase, plan.Drafts[0].Round).Should().Be((PlanPhase.Challenge, PlanPhase.Challenge, 1));

        (await RunCliAsync(cts.Token, "plan", "start", ready.ToString(), "--at", "discovery"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("Discovery and drafting are not offered");
    }

    [Fact]
    public async Task Start_is_refused_for_a_draft_already_in_a_live_plan_and_a_cancelled_plan_frees_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString())).Should().BeNull();
        Guid planId = (await PlansAsync(cts.Token)).Single().Id;

        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString()))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("already in plan");

        (await RunCliAsync(cts.Token, "plan", "cancel", planId.ToString(), "--reason", "starting over")).Should().BeNull();
        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString())).Should().BeNull();
        (await PlansAsync(cts.Token)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Start_is_refused_for_a_missing_checkout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid here = await SeedDraftAsync(cts.Token);

        Directory.Delete(_checkout, recursive: true);
        (await RunCliAsync(cts.Token, "plan", "start", here.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("no working checkout");
        Directory.CreateDirectory(_checkout);
    }

    [Fact]
    public async Task Start_on_a_tracking_project_is_refused_without_an_attestation_and_records_it_when_given()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            ProjectDetails project = (await session.LoadAsync<ProjectDetails>(_projectId, cts.Token))!;
            project.BacklogPolicy = BacklogPolicy.GitHubIssues;
            session.Store(project);
            await session.SaveChangesAsync(cts.Token);
        }

        Guid draft = await SeedDraftAsync(cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("--no-existing-item").And.Contain("--untracked");
        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString(), "--no-existing-item", "--untracked"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("opposite things");

        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString(), "--no-existing-item")).Should().BeNull();
        PlanDetails plan = (await PlansAsync(cts.Token)).Should().ContainSingle().Subject;
        plan.NoExistingItemAttested.Should().BeTrue();
        plan.UntrackedAttested.Should().BeFalse();
        plan.StartedByOwnerId.Should().Be(_me.OwnerId, "who gave the attestation");
    }

    [Fact]
    public async Task Show_and_list_read_the_plan_and_resume_and_cancel_refuse_what_they_cannot_do()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString())).Should().BeNull();
        PlanDetails plan = (await PlansAsync(cts.Token)).Single();
        string shortId = DomainId.Short(plan.Id);

        (await RunCliAsync(cts.Token, "plan", "show", shortId)).Should().BeNull();
        (await RunCliAsync(cts.Token, "plan", "list")).Should().BeNull();
        (await RunCliAsync(cts.Token, "plan", "show", "ffffffffffff")).Should().BeOfType<DomainNotFoundException>();

        (await RunCliAsync(cts.Token, "plan", "resume", shortId)).Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("not parked");
        (await RunCliAsync(cts.Token, "plan", "cancel", shortId, "--reason", "  ")).Should().BeOfType<DomainValidationException>();
        (await RunCliAsync(cts.Token, "plan", "cancel", shortId)).Should().BeOfType<DomainValidationException>();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(plan.Id, new PlanParked(plan.Id, plan.ProjectId, draft, PlanParkReason.Question, Guid.NewGuid(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "plan", "resume", shortId)).Should().BeNull();
        (await PlansAsync(cts.Token)).Single().IsParked.Should().BeFalse();

        (await RunCliAsync(cts.Token, "plan", "cancel", shortId, "--reason", "the idea changed")).Should().BeNull();
        PlanDetails cancelled = (await PlansAsync(cts.Token)).Single();
        cancelled.State.Should().Be(PlanState.Cancelled);
        cancelled.CancelReason.Should().Be("the idea changed");
        (await RunCliAsync(cts.Token, "plan", "cancel", shortId, "--reason", "again")).Should().BeOfType<DomainConflictException>();

        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(draft, cts.Token))!;
        task.State.Should().Be(TaskState.Draft, "a cancelled plan leaves its drafts as Drafts");
    }

    [Fact]
    public async Task Status_lists_a_parked_plan_under_needs_you_and_task_show_names_the_plan()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", draft.ToString())).Should().BeNull();
        PlanDetails plan = (await PlansAsync(cts.Token)).Single();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(plan.Id, new PlanParked(plan.Id, plan.ProjectId, draft, PlanParkReason.RoundsExhausted, null, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        string status = await CaptureAsync(() => RunCliAsync(cts.Token, "status"));
        status.Should().Contain("Needs you").And.Contain($"plan {DomainId.Short(plan.Id)} parked on draft {DomainId.Short(draft)}")
            .And.Contain("h9k plan resume");

        string show = await CaptureAsync(() => RunCliAsync(cts.Token, "task", "show", draft.ToString()));
        show.Should().Contain("Plan").And.Contain(DomainId.Short(plan.Id)).And.Contain("parked").And.Contain($"h9k plan show {DomainId.Short(plan.Id)}");
        show.Should().NotContain("h9k task publish", "the Next line points at the plan, which publishes the draft itself");
    }

    [Fact]
    public async Task The_revision_format_the_refinement_prompt_teaches_is_what_h9k_task_revise_file_reads()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(cts.Token);
        string file = Path.Combine(_checkout, "refinement-revision.md");
        await File.WriteAllTextAsync(
            file,
            "---\nobjective: Plans refine drafts in the background\ntype: Feature\ncriteria:\n  - >-\n    The first criterion, checkable by someone else.\n"
            + "  - >-\n    The second criterion.\n---\nPointers are relative to the project home.\n",
            cts.Token);

        (await RunCliAsync(cts.Token, "task", "revise", draft.ToString(), "--file", file)).Should().BeNull();

        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskDetails revised = (await query.LoadAsync<TaskDetails>(draft, cts.Token))!;
        revised.Objective.Should().Be("Plans refine drafts in the background");
        revised.AcceptanceCriteria.Should().Equal("The first criterion, checkable by someone else.", "The second criterion.");
        revised.AgentContext.Should().Contain("Pointers are relative to the project home.");
        revised.Model?.Value.Should().BeNullOrEmpty("a refinement sets no model");
        revised.RevisedWhileRefinementRan.Should().BeFalse();
    }

    [Fact]
    public async Task A_revise_run_for_a_refinement_session_applies_only_to_the_draft_that_session_read_and_is_recorded_as_its_own()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(cts.Token);
        Guid session = DomainId.New();
        await using (IDocumentSession dispatch = _postgres.Store.LightweightSession())
        {
            dispatch.Events.Append(draft, new DraftRefinementDispatched(
                draft, session, _me.NodeId, Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, RefinementPurpose.Refine));
            await dispatch.SaveChangesAsync(cts.Token);
        }

        string file = Path.Combine(_checkout, "refinement-revision.md");
        await File.WriteAllTextAsync(file, "---\nobjective: As the session wrote it\n---\nContext.\n", cts.Token);
        using (EnvironmentVariableScope.Set((TaskRevised.RefinementSessionEnvironmentVariable, session.ToString())))
        {
            (await RunCliAsync(cts.Token, "task", "revise", draft.ToString(), "--file", file)).Should().BeNull();
        }

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            TaskDetails revised = (await query.LoadAsync<TaskDetails>(draft, cts.Token))!;
            revised.Objective.Should().Be("As the session wrote it");
            revised.RevisedWhileRefinementRan.Should().BeFalse("it is the session's own revision");
            revised.RevisedSinceLastDraftSession.Should().BeFalse();
        }

        // A person revises after the session's own revision landed; a second apply for the same session is refused.
        (await RunCliAsync(cts.Token, "task", "revise", draft.ToString(), "--objective", "A person's wording"))
            .Should().BeNull();
        await File.WriteAllTextAsync(file, "---\nobjective: A second time\n---\nContext.\n", cts.Token);

        Exception? refused;
        using (EnvironmentVariableScope.Set((TaskRevised.RefinementSessionEnvironmentVariable, session.ToString())))
        {
            refused = await RunCliAsync(cts.Token, "task", "revise", draft.ToString(), "--file", file);
        }

        refused.Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("was changed after refinement session");
        await using IQuerySession after = _postgres.Store.QuerySession();
        TaskDetails final = (await after.LoadAsync<TaskDetails>(draft, cts.Token))!;
        final.Objective.Should().Be("A person's wording", "nothing from the refused apply landed");
        final.RevisedWhileRefinementRan.Should().BeTrue();
    }

    [Fact]
    public async Task A_publish_run_for_a_plan_refuses_a_draft_a_person_revised_since_a_session_last_read_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid draft = await SeedDraftAsync(cts.Token);
        (await RunCliAsync(cts.Token, "task", "revise", draft.ToString(), "--objective", "A person's wording")).Should().BeNull();

        Exception? refused;
        using (EnvironmentVariableScope.Set((TaskPublished.PlanPublishEnvironmentVariable, "1")))
        {
            refused = await RunCliAsync(cts.Token, "task", "publish", draft.ToString(), "--no-queue");
        }

        refused.Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("its text has not been judged");
        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskDetails after = (await query.LoadAsync<TaskDetails>(draft, cts.Token))!;
        after.State.Should().Be(TaskState.Draft, "the refused publish recorded nothing");
    }

    private static async Task<string> CaptureAsync(Func<Task<Exception?>> run)
    {
        using ScopedAnsiConsoleCapture capture = ScopedAnsiConsoleCapture.Begin();
        Exception? exception = await run();
        exception.Should().BeNull();
        return capture.Text;
    }
}
