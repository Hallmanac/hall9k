using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k plan start --idea</c>, <c>pursue</c>, <c>stop</c>, <c>resume</c>, and <c>cancel</c> on a plan in discovery, run through the
/// real command tree against a real store: the plan is recorded with no drafts, the gate is answered once, a pursue cuts exactly
/// the drafts the report named in one save, and every refusal names its reason with nothing cut and the plan still at the gate.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PlanFromIdeaCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private const string PursueReport = "Recap.\n\nDISCOVERY: pursue\nTASK: Build the first piece\nTASK: Build the second piece\n";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-plan-idea-command-").FullName;
    private BootstrapContext _me = null!;
    private Guid _projectId;

    public PlanFromIdeaCommandTests(PostgresFixture postgres) => _postgres = postgres;

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

        _projectId = await SeedProjectAsync(cts.Token);
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

    private async Task<Guid> SeedProjectAsync(CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = "ideas",
            RepositoryPath = _checkout,
            BaseBranch = "main",
            SkipPermissions = true,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    private async Task UpdateProjectAsync(Action<ProjectDetails> change, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(_projectId, cancellationToken))!;
        change(project);
        session.Store(project);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Exception?> RunCliAsync(CancellationToken cancellationToken, params string[] arguments)
    {
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

    private static async Task<string> CaptureAsync(Func<Task<Exception?>> run)
    {
        using ScopedAnsiConsoleCapture capture = ScopedAnsiConsoleCapture.Begin();
        Exception? exception = await run();
        exception.Should().BeNull();
        return capture.Text;
    }

    private async Task<Guid> SeedIdeaAsync(CancellationToken cancellationToken, string text = "Let a plan start from an idea", bool withWorkspace = true)
    {
        Guid ideaId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(ideaId, _me.OwnerId, text, _projectId, Now, ProjectHome.None));
        await session.SaveChangesAsync(cancellationToken);
        if (withWorkspace)
        {
            IdeaPaths.EnsureWorkspace(IdeaPaths.GlobalDirectory(ideaId));
        }

        return ideaId;
    }

    private async Task<IdeaAggregate> IdeaAsync(Guid ideaId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken))!;
    }

    private async Task<PlanDetails> OnlyPlanAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Query<PlanDetails>().ToListAsync(cancellationToken)).OrderByDescending(plan => plan.StartedAt).First();
    }

    /// <summary>Starts a plan on the idea by the command and records what the daemon would: a session dispatched and the report landed at the gate.</summary>
    private async Task<(Guid PlanId, string ReportPath)> PlanAtGateAsync(
        Guid ideaId, string? report, CancellationToken cancellationToken)
    {
        (await RunCliAsync(cancellationToken, "plan", "start", "--idea", ideaId.ToString())).Should().BeNull();
        PlanDetails plan = await OnlyPlanAsync(cancellationToken);
        Guid session = DomainId.New();
        await using (IDocumentSession events = _postgres.Store.LightweightSession())
        {
            events.Events.Append(plan.Id, new PlanDiscoveryDispatched(plan.Id, plan.ProjectId, ideaId, session, Now));
            events.Events.Append(plan.Id, new PlanDiscoveryParked(
                plan.Id, plan.ProjectId, ideaId, PlanParkReason.Gate, session, Hall9k.Domain.Features.Tasks.RefinementFailureReason.Unknown, Now));
            await events.SaveChangesAsync(cancellationToken);
        }

        string reportPath = DiscoveryWorkspace.ReportPathOf(await IdeaAsync(ideaId, cancellationToken), plan.Id)!;
        if (report is not null)
        {
            await File.WriteAllTextAsync(reportPath, report, cancellationToken);
        }

        return (plan.Id, reportPath);
    }

    private async Task<IReadOnlyList<TaskDetails>> DraftsOfAsync(Guid ideaId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return [.. (await query.Query<TaskDetails>().ToListAsync(cancellationToken)).Where(task => task.SourceIdeaId == ideaId).OrderBy(task => task.Objective)];
    }

    [Fact]
    public async Task Start_records_a_plan_in_discovery_with_no_drafts_returns_and_creates_no_run_lease_or_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);

        string output = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "start", "--idea", DomainId.Short(ideaId)));

        PlanDetails plan = await OnlyPlanAsync(cts.Token);
        (plan.IdeaId, plan.InDiscovery, plan.Drafts.Count, plan.StartPhase, plan.ProjectId, plan.StartedByOwnerId)
            .Should().Be((ideaId, true, 0, PlanPhase.Discovery, _projectId, _me.OwnerId));
        output.Should().Contain("started").And.Contain("returns without waiting").And.Contain($"discovery-{DomainId.Short(plan.Id)}.md")
            .And.Contain("h9k plan pursue").And.Contain("h9k plan stop");
        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Events.FetchStreamAsync(plan.Id, token: cts.Token)).Select(recorded => recorded.Data).Should().ContainSingle().Which.Should().BeOfType<PlanStarted>();
        (await query.Query<TaskDetails>().CountAsync(cts.Token)).Should().Be(0);
        (await IdeaAsync(ideaId, cts.Token)).CutTaskIds.Should().BeEmpty("starting a plan cuts nothing and decides nothing about the idea");
    }

    [Fact]
    public async Task Capturing_revising_moving_or_assigning_an_idea_starts_no_plan()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = await IdeaAsync(ideaId, cts.Token);
            session.Events.Append(ideaId, IdeaDecider.Revise(idea, "A sharper note", Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "idea", "assign", ideaId.ToString())).Should().BeNull();

        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Query<PlanDetails>().CountAsync(cts.Token)).Should().Be(0);
    }

    [Fact]
    public async Task Start_refuses_draft_ids_at_and_the_attestations_alongside_idea_and_a_second_start_while_one_is_live()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);

        foreach (string[] extra in new[] { new[] { Guid.NewGuid().ToString() }, ["--at", "challenge"], ["--no-existing-item"], ["--untracked"] })
        {
            (await RunCliAsync(cts.Token, [.. new[] { "plan", "start", "--idea", ideaId.ToString() }, .. extra]))
                .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("--idea starts a plan in discovery");
        }

        // A blank --idea is still --idea: with draft ids beside it the refusal is the same, not a plan started from the drafts.
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", "", Guid.NewGuid().ToString()))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("--idea starts a plan in discovery");

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            (await query.Query<PlanDetails>().CountAsync(cts.Token)).Should().Be(0);
        }

        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ideaId.ToString())).Should().BeNull();
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ideaId.ToString()))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("already in plan");
    }

    [Fact]
    public async Task Start_with_neither_a_draft_nor_an_idea_says_what_it_needs_rather_than_failing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        (await RunCliAsync(cts.Token, "plan", "start"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("--idea").And.Contain("at least one draft");
    }

    [Fact]
    public async Task Start_refuses_an_idea_with_no_workspace_here_a_concluded_one_and_a_project_that_cannot_run_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid noWorkspace = await SeedIdeaAsync(cts.Token, "No workspace on this node", withWorkspace: false);
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", noWorkspace.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("node that captured the idea");
        Directory.Exists(IdeaPaths.GlobalDirectory(noWorkspace)).Should().BeFalse("the start never creates a workspace");

        Guid concluded = await SeedIdeaAsync(cts.Token, "Already concluded");
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(concluded, IdeaDecider.Conclude(await IdeaAsync(concluded, cts.Token), "done", Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "plan", "start", "--idea", concluded.ToString())).Should().BeAssignableTo<DomainException>();

        Guid ordinary = await SeedIdeaAsync(cts.Token, "An ordinary idea");
        await UpdateProjectAsync(project => project.SkipPermissions = false, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ordinary.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("skip-permissions");
        await UpdateProjectAsync(project =>
        {
            project.SkipPermissions = true;
            project.IsArchived = true;
        }, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ordinary.ToString())).Should().BeOfType<DomainConflictException>();
        await UpdateProjectAsync(project =>
        {
            project.IsArchived = false;
            project.PurgeAt = Now.AddDays(3);
        }, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ordinary.ToString()))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("permanent deletion");

        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.Query<PlanDetails>().CountAsync(cts.Token)).Should().Be(0);
    }

    [Fact]
    public async Task Pursue_reads_the_report_now_and_cuts_one_draft_per_task_line_in_one_save_and_moves_the_plan_to_refinement()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token, "Let a plan start from an idea. It needs a gate.");
        (Guid planId, string reportPath) = await PlanAtGateAsync(ideaId, "Recap.\n\nDISCOVERY: pursue\nTASK: A task the session proposed\n", cts.Token);
        // A person's edit to the file counts: what it says when they pursue is what is cut.
        await File.WriteAllTextAsync(reportPath, PursueReport, cts.Token);

        string output = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "pursue", DomainId.Short(planId)));

        IReadOnlyList<TaskDetails> drafts = await DraftsOfAsync(ideaId, cts.Token);
        drafts.Select(draft => draft.Objective).Should().Equal("Build the first piece", "Build the second piece");
        drafts.Should().OnlyContain(draft => draft.State == TaskState.Draft && draft.ProjectId == _projectId && draft.EpicId == null);
        drafts.Should().OnlyContain(draft => draft.AcceptanceCriteria.Count == 0);
        TaskDetails first = drafts[0];
        first.AgentContext.Should().Contain("Let a plan start from an idea. It needs a gate.").And.Contain(IdeaPaths.WorkspaceDirectory(IdeaPaths.GlobalDirectory(ideaId)))
            .And.Contain($"discovery of idea {DomainId.Short(ideaId)}", "the context names the idea")
            .And.Contain(DiscoveryWorkspace.ReportFileName(planId), "the context names the report file").And.NotContain(reportPath, "a draft replicates, so it carries the file name and not this node's path");

        PlanDetails plan = await OnlyPlanAsync(cts.Token);
        (plan.InDiscovery, plan.IsParked, plan.State, plan.Phase).Should().Be((false, false, PlanState.Live, PlanPhase.Refinement));
        plan.Drafts.Select(draft => draft.DraftId).Should().BeEquivalentTo(drafts.Select(draft => draft.Id));
        (await IdeaAsync(ideaId, cts.Token)).CutTaskIds.Should().BeEquivalentTo(drafts.Select(draft => draft.Id), "the idea's own fan-out records each cut");
        (await IdeaAsync(ideaId, cts.Token)).State.Should().Be(IdeaState.Captured, "no plan concludes an idea");
        output.Should().Contain("pursued").And.Contain("Build the first piece").And.Contain("Build the second piece");

        // The same answer cannot be given twice.
        (await RunCliAsync(cts.Token, "plan", "pursue", DomainId.Short(planId))).Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("already cut its drafts");
        (await DraftsOfAsync(ideaId, cts.Token)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_pursues_at_once_cut_one_set_of_drafts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        // One pursue first, on another idea, so every projection table a cut writes to exists before the two race: two commands
        // creating the same table at once is a schema race of the test database, not the fence under test.
        Guid warmUp = await SeedIdeaAsync(cts.Token, "Warm up the projections");
        (Guid warmUpPlan, _) = await PlanAtGateAsync(warmUp, "DISCOVERY: pursue\nTASK: Warm up\n", cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", warmUpPlan.ToString())).Should().BeNull();

        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (Guid planId, _) = await PlanAtGateAsync(ideaId, PursueReport, cts.Token);

        Exception?[] results = await Task.WhenAll(
            RunCliAsync(cts.Token, "plan", "pursue", planId.ToString()), RunCliAsync(cts.Token, "plan", "pursue", planId.ToString()));

        results.Count(result => result is null).Should().Be(1, "exactly one of the two pursues won");
        results.Single(result => result is not null).Should().BeAssignableTo<DomainConflictException>();
        (await DraftsOfAsync(ideaId, cts.Token)).Should().HaveCount(2);
        (await IdeaAsync(ideaId, cts.Token)).CutTaskIds.Should().HaveCount(2);
        await using IQuerySession query = _postgres.Store.QuerySession();
        (await query.LoadAsync<PlanDetails>(planId, cts.Token))!.Drafts.Should().HaveCount(2);
    }

    [Fact]
    public async Task Epic_title_creates_one_plain_epic_holding_every_cut_draft_and_is_refused_for_a_single_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid single = await SeedIdeaAsync(cts.Token, "A one task idea");
        (Guid singlePlan, _) = await PlanAtGateAsync(single, "DISCOVERY: pursue\nTASK: The only task\n", cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", singlePlan.ToString(), "--epic-title", "Grouped"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("two or more");
        (await DraftsOfAsync(single, cts.Token)).Should().BeEmpty("nothing is cut when the pursue is refused");
        (await OnlyPlanAsync(cts.Token)).AtGate.Should().BeTrue("and the plan is still at the gate");

        Guid several = await SeedIdeaAsync(cts.Token, "A two task idea");
        (Guid severalPlan, _) = await PlanAtGateAsync(several, PursueReport, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", severalPlan.ToString(), "--epic-title", "Sharper discovery")).Should().BeNull();

        IReadOnlyList<TaskDetails> drafts = await DraftsOfAsync(several, cts.Token);
        Guid epicId = drafts[0].EpicId.Should().NotBeNull().And.Subject!.Value;
        drafts.Should().OnlyContain(draft => draft.EpicId == epicId);
        await using IQuerySession query = _postgres.Store.QuerySession();
        EpicAggregate epic = (await query.Events.AggregateStreamAsync<EpicAggregate>(epicId, token: cts.Token))!;
        epic.Title.Should().Be("Sharper discovery");
        (await query.Query<PlanDetails>().Where(plan => plan.Id == severalPlan).SingleAsync(cts.Token)).EpicId.Should().Be(epicId);
    }

    [Fact]
    public async Task Pursue_on_a_tracking_project_is_refused_without_an_attestation_and_records_it_the_way_start_does()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await UpdateProjectAsync(project => project.BacklogPolicy = BacklogPolicy.GitHubIssues, cts.Token);
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (Guid planId, _) = await PlanAtGateAsync(ideaId, PursueReport, cts.Token);

        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("--no-existing-item").And.Contain("--untracked");
        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString(), "--no-existing-item", "--untracked"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("opposite things");
        (await DraftsOfAsync(ideaId, cts.Token)).Should().BeEmpty();
        (await OnlyPlanAsync(cts.Token)).AtGate.Should().BeTrue();

        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString(), "--untracked")).Should().BeNull();
        PlanDetails plan = await OnlyPlanAsync(cts.Token);
        (plan.UntrackedAttested, plan.NoExistingItemAttested, plan.StartedByOwnerId).Should().Be((true, false, _me.OwnerId));
    }

    [Fact]
    public async Task Pursue_is_refused_with_nothing_cut_and_the_plan_still_at_the_gate_for_every_report_and_idea_that_cannot_be_cut_from()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (Guid planId, string reportPath) = await PlanAtGateAsync(ideaId, null, cts.Token);

        async Task StillAtGateWithNothingCutAsync()
        {
            (await DraftsOfAsync(ideaId, cts.Token)).Should().BeEmpty();
            (await OnlyPlanAsync(cts.Token)).AtGate.Should().BeTrue();
        }

        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("not at").And.Contain(reportPath);
        await StillAtGateWithNothingCutAsync();

        await File.WriteAllTextAsync(reportPath, "An analysis with no closing block at all.", cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("not readable").And.Contain("DISCOVERY:");
        await StillAtGateWithNothingCutAsync();

        await File.WriteAllTextAsync(reportPath, "Nothing to build.\n\nDISCOVERY: stop\n", cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("stop").And.Contain("h9k plan stop");
        await StillAtGateWithNothingCutAsync();

        await File.WriteAllTextAsync(reportPath, PursueReport, cts.Token);
        await UpdateProjectAsync(project => project.IsArchived = true, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString())).Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("archived");
        await UpdateProjectAsync(project =>
        {
            project.IsArchived = false;
            project.PurgeAt = Now.AddDays(2);
        }, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString())).Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("permanent deletion");
        await StillAtGateWithNothingCutAsync();

        await UpdateProjectAsync(project => project.PurgeAt = null, cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(ideaId, IdeaDecider.Conclude(await IdeaAsync(ideaId, cts.Token), "done another way", Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "plan", "pursue", planId.ToString())).Should().BeAssignableTo<DomainException>().Which.Message.Should().Contain("concluded");
        await StillAtGateWithNothingCutAsync();
    }

    [Fact]
    public async Task Pursue_and_stop_are_refused_for_a_plan_that_is_not_at_the_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ideaId.ToString())).Should().BeNull();
        PlanDetails plan = await OnlyPlanAsync(cts.Token);

        (await RunCliAsync(cts.Token, "plan", "pursue", plan.Id.ToString())).Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("has not finished discovery");
        (await RunCliAsync(cts.Token, "plan", "stop", plan.Id.ToString(), "--reason", "no")).Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("cannot be stopped");
    }

    [Fact]
    public async Task Stop_names_the_state_the_idea_is_in_when_it_was_concluded_while_the_plan_waited_at_the_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (Guid planId, _) = await PlanAtGateAsync(ideaId, "Nothing to build.\n\nDISCOVERY: stop\n", cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(ideaId, IdeaDecider.Conclude(await IdeaAsync(ideaId, cts.Token), "done another way", Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        string output = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "stop", DomainId.Short(planId), "--reason", "done elsewhere"));

        output.Should().Contain("stopped").And.Contain("is Concluded").And.NotContain("is Captured");
    }

    [Fact]
    public async Task Stop_ends_the_plan_as_a_normal_ending_leaves_the_idea_and_its_workspace_alone_and_a_new_plan_may_follow()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (Guid planId, string reportPath) = await PlanAtGateAsync(ideaId, "Nothing to build.\n\nDISCOVERY: stop\n", cts.Token);

        (await RunCliAsync(cts.Token, "plan", "stop", planId.ToString())).Should().BeOfType<DomainValidationException>();
        string output = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "stop", DomainId.Short(planId), "--reason", "an exploration"));

        PlanDetails plan = await OnlyPlanAsync(cts.Token);
        (plan.State, plan.StopReason, plan.IsLive).Should().Be((PlanState.Stopped, "an exploration", false));
        output.Should().Contain("stopped").And.Contain("h9k idea conclude").And.Contain("h9k idea archive").And.Contain("h9k plan start --idea");
        (await IdeaAsync(ideaId, cts.Token)).State.Should().Be(IdeaState.Captured);
        File.ReadAllText(reportPath).Should().Contain("DISCOVERY: stop");

        string list = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "list", "--all"));
        list.Should().Contain("stopped").And.NotContain("cancelled").And.NotContain("failed");
        string show = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "show", DomainId.Short(planId)));
        show.Should().Contain("stopped").And.Contain("a normal ending").And.Contain("reason given: an exploration");

        // Starting a new plan on the idea is allowed, and it will write a report of its own beside the first.
        (await RunCliAsync(cts.Token, "plan", "start", "--idea", ideaId.ToString())).Should().BeNull();
        (await OnlyPlanAsync(cts.Token)).Id.Should().NotBe(planId);
    }

    [Fact]
    public async Task Resume_at_the_gate_is_refused_naming_the_two_answers_and_cancel_ends_a_plan_with_no_drafts_leaving_the_idea_as_it_was()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token);
        (Guid planId, string reportPath) = await PlanAtGateAsync(ideaId, PursueReport, cts.Token);

        (await RunCliAsync(cts.Token, "plan", "resume", planId.ToString()))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("h9k plan pursue").And.Contain("h9k plan stop");
        (await OnlyPlanAsync(cts.Token)).AtGate.Should().BeTrue();

        (await RunCliAsync(cts.Token, "plan", "cancel", planId.ToString(), "--reason", "changed my mind")).Should().BeNull();
        (await OnlyPlanAsync(cts.Token)).State.Should().Be(PlanState.Cancelled);
        (await IdeaAsync(ideaId, cts.Token)).State.Should().Be(IdeaState.Captured);
        File.Exists(reportPath).Should().BeTrue("a cancel touches nothing in the workspace");
    }

    [Fact]
    public async Task Show_status_and_the_idea_name_the_gate_and_the_two_answers_and_show_reads_the_report_in_the_workspace_now()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ideaId = await SeedIdeaAsync(cts.Token, "Let a plan start from an idea");
        (Guid planId, string reportPath) = await PlanAtGateAsync(ideaId, PursueReport, cts.Token);
        string plan = DomainId.Short(planId);

        string show = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "show", plan));
        show.Should().Contain($"idea: {DomainId.Short(ideaId)}").And.Contain("the report says pursue, proposing 2 tasks").And.Contain("Build the first piece")
            .And.Contain(reportPath).And.Contain("claude --resume").And.Contain("h9k plan pursue");

        await File.WriteAllTextAsync(reportPath, "no closing block", cts.Token);
        (await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "show", plan))).Should().Contain("not readable").And.Contain(reportPath).And.Contain("claude --resume");
        File.Delete(reportPath);
        (await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "show", plan))).Should().Contain("is not there").And.Contain(reportPath).And.Contain("claude --resume");

        string status = await CaptureAsync(() => RunCliAsync(cts.Token, "status"));
        status.Should().Contain("Needs you").And.Contain($"plan {plan} parked in discovery of idea {DomainId.Short(ideaId)}")
            .And.Contain($"h9k plan pursue {plan}").And.Contain($"h9k plan stop {plan}");

        string idea = await CaptureAsync(() => RunCliAsync(cts.Token, "idea", "show", ideaId.ToString()));
        idea.Should().Contain("Plan").And.Contain(plan).And.Contain("discovery").And.Contain($"h9k plan pursue {plan}").And.Contain("h9k plan start --idea");

        string list = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "list"));
        list.Should().Contain(DomainId.Short(ideaId)).And.Contain("parked (gate)");
        (await CaptureAsync(() => RunCliAsync(cts.Token, "idea", "list"))).Should().Contain("h9k plan start --idea");
    }
}
