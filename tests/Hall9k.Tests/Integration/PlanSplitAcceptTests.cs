using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k plan split accept</c>, run through the real command tree against a real store: a plan pursued from an idea, one of its
/// drafts parked on the split its refinement session proposed, and the accept that turns the increments into stacked drafts of
/// the same plan, or refuses with nothing recorded.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PlanSplitAcceptTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private const string SplitReport =
        "The draft is three increments.\n\nREFINEMENT: split\nTASK: Read the flag\nTASK: Act on the flag\nTASK: Report the flag\n";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-plan-split-").FullName;
    private BootstrapContext _me = null!;
    private Guid _projectId;

    public PlanSplitAcceptTests(PostgresFixture postgres) => _postgres = postgres;

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

        _projectId = DomainId.New();
        await using IDocumentSession projects = _postgres.Store.LightweightSession();
        projects.Store(new ProjectDetails
        {
            Id = _projectId,
            Name = "splits",
            RepositoryPath = _checkout,
            BaseBranch = "main",
            SkipPermissions = true,
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await projects.SaveChangesAsync(cts.Token);
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

    private async Task UpdateProjectAsync(Action<ProjectDetails> change, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(_projectId, cancellationToken))!;
        change(project);
        session.Store(project);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<PlanDetails> PlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.LoadAsync<PlanDetails>(planId, cancellationToken))!;
    }

    private async Task<TaskDetails> TaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!;
    }

    private async Task<IdeaAggregate> IdeaAsync(Guid ideaId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken))!;
    }

    /// <summary>
    /// An idea pursued by the command into a plan of two drafts in one epic, then what the daemon records when the first draft's
    /// refinement session ends in a split: the session dispatched, its report written, and the plan parked on the draft.
    /// </summary>
    private async Task<(Guid PlanId, Guid IdeaId, Guid Original, Guid Sibling, string ReportPath)> ParkedOnSplitAsync(
        CancellationToken cancellationToken, string report = SplitReport)
    {
        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(
                ideaId, IdeaDecider.Capture(ideaId, _me.OwnerId, "Let a flag steer the feature", _projectId, Now, ProjectHome.None));
            await session.SaveChangesAsync(cancellationToken);
        }

        IdeaPaths.EnsureWorkspace(IdeaPaths.GlobalDirectory(ideaId));
        (await RunCliAsync(cancellationToken, "plan", "start", "--idea", ideaId.ToString())).Should().BeNull();
        await using IQuerySession lookup = _postgres.Store.QuerySession();
        PlanDetails started = (await lookup.Query<PlanDetails>().ToListAsync(cancellationToken)).Single(plan => plan.IdeaId == ideaId);
        Guid discovery = DomainId.New();
        await using (IDocumentSession events = _postgres.Store.LightweightSession())
        {
            events.Events.Append(started.Id, new PlanDiscoveryDispatched(started.Id, _projectId, ideaId, discovery, Now));
            events.Events.Append(started.Id, new PlanDiscoveryParked(
                started.Id, _projectId, ideaId, PlanParkReason.Gate, discovery, RefinementFailureReason.Unknown, Now));
            await events.SaveChangesAsync(cancellationToken);
        }

        string discoveryReport = DiscoveryWorkspace.ReportPathOf(await IdeaAsync(ideaId, cancellationToken), started.Id)!;
        await File.WriteAllTextAsync(
            discoveryReport, "Recap.\n\nDISCOVERY: pursue\nTASK: Build the whole flag feature\nTASK: Document the flag\n", cancellationToken);
        (await RunCliAsync(cancellationToken, "plan", "pursue", started.Id.ToString(), "--epic-title", "Flags")).Should().BeNull();

        PlanDetails pursued = await PlanAsync(started.Id, cancellationToken);
        Guid original = pursued.Drafts[0].DraftId;
        Guid sibling = pursued.Drafts[1].DraftId;
        string reportPath = await ParkOnSplitAsync(started.Id, original, report, cancellationToken);
        return (started.Id, ideaId, original, sibling, reportPath);
    }

    private async Task<string> ParkOnSplitAsync(Guid planId, Guid draft, string report, CancellationToken cancellationToken)
    {
        Guid session = DomainId.New();
        string reportPath = RunPaths.RefinementReportFile(session);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);
        await using IDocumentSession events = _postgres.Store.LightweightSession();
        events.Events.Append(planId, new PlanRefinementDispatched(planId, _projectId, draft, session, RefinementPurpose.Refine, Now));
        events.Events.Append(planId, new PlanParked(planId, _projectId, draft, PlanParkReason.SplitProposed, session, Now));
        await events.SaveChangesAsync(cancellationToken);
        return reportPath;
    }

    [Fact]
    public async Task Accept_makes_the_original_the_first_increment_and_cuts_the_rest_as_stacked_drafts_of_the_same_plan()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, Guid ideaId, Guid original, Guid sibling, _) = await ParkedOnSplitAsync(cts.Token);
        TaskDetails before = await TaskAsync(original, cts.Token);

        string output = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "split", "accept", DomainId.Short(planId)));

        PlanDetails plan = await PlanAsync(planId, cts.Token);
        plan.Drafts.Should().HaveCount(4);
        plan.Drafts.Take(2).Select(draft => draft.DraftId).Should().Equal(original, sibling);
        plan.IsParked.Should().BeFalse();
        Guid second = plan.Drafts[2].DraftId;
        Guid third = plan.Drafts[3].DraftId;
        foreach (PlanDraftState draft in plan.Drafts.Where(draft => draft.DraftId != sibling))
        {
            (draft.Phase, draft.Round, draft.HasSplit).Should().Be((PlanPhase.Refinement, 0, true));
            PlanDecider.NextStep(draft).Should().Be(PlanStepKind.DispatchRefinement, "the daemon refines every increment with no restart");
        }

        TaskDetails first = await TaskAsync(original, cts.Token);
        first.Objective.Should().Be("Read the flag");
        first.State.Should().Be(TaskState.Draft);
        first.AgentContext.Should().StartWith(before.AgentContext!.TrimEnd())
            .And.Contain("increment 1 of 3").And.Contain($"{DomainId.Short(second)}, {DomainId.Short(third)}");

        TaskDetails secondTask = await TaskAsync(second, cts.Token);
        TaskDetails thirdTask = await TaskAsync(third, cts.Token);
        (secondTask.Objective, thirdTask.Objective).Should().Be(("Act on the flag", "Report the flag"));
        foreach (TaskDetails cut in new[] { secondTask, thirdTask })
        {
            (cut.State, cut.ProjectId, cut.Type, cut.EpicId, cut.SourceIdeaId).Should().Be(
                (TaskState.Draft, _projectId, before.Type, before.EpicId, ideaId));
            before.EpicId.Should().NotBeNull();
            cut.AgentContext.Should().StartWith(before.AgentContext!.TrimEnd()).And.Contain($"split from draft {DomainId.Short(original)}");
        }

        (secondTask.BlockedBy, secondTask.StackedOnTaskId).Should().BeEquivalentTo((new[] { original }, (Guid?)original));
        (thirdTask.BlockedBy, thirdTask.StackedOnTaskId).Should().BeEquivalentTo((new[] { second }, (Guid?)second));
        (await IdeaAsync(ideaId, cts.Token)).CutTaskIds.Should().Contain([second, third], "each new draft is recorded on the idea as cut from it");
        output.Should().Contain("Split accepted").And.Contain("Act on the flag").And.Contain("Report the flag");

        // The lineage has split: a second accept finds nothing parked on a split.
        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("nothing to accept");
        (await PlanAsync(planId, cts.Token)).Drafts.Should().HaveCount(4);
    }

    [Fact]
    public async Task The_new_drafts_keep_a_private_originals_scope_and_its_human_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, _, Guid original, _, _) = await ParkedOnSplitAsync(cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(original, token: cts.Token))!;
            session.Events.Append(original, new TaskScopeSet(original, ReplicationScope.Private, Now, _me.OwnerId));
            session.Events.Append(original, TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None, Optional<IReadOnlyList<Guid>>.None,
                Optional<TaskType>.None, Optional<AgentModel>.None, Now, _me.OwnerId, humanGate: Optional<bool>.Of(true)));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString())).Should().BeNull();

        PlanDetails plan = await PlanAsync(planId, cts.Token);
        foreach (Guid cut in plan.Drafts.Skip(2).Select(draft => draft.DraftId))
        {
            TaskDetails added = await TaskAsync(cut, cts.Token);
            (added.Scope, added.HumanGate).Should().Be((ReplicationScope.Private, true), "an increment carries the original's words and its hold");
        }
    }

    [Fact]
    public async Task Show_lists_the_increments_from_the_report_now_and_status_offers_split_accept_beside_resume()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, _, Guid original, _, string reportPath) = await ParkedOnSplitAsync(cts.Token);
        string plan = DomainId.Short(planId);

        string show = await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "show", plan));
        show.Should().Contain(PlanParkReason.SplitProposed.Description).And.Contain($"h9k plan split accept {plan}")
            .And.Contain("1. Read the flag").And.Contain("2. Act on the flag").And.Contain("3. Report the flag");

        await File.WriteAllTextAsync(reportPath, "Edited.\n\nREFINEMENT: split\nTASK: Read the flag\nTASK: Act on it all\n", cts.Token);
        (await CaptureAsync(() => RunCliAsync(cts.Token, "plan", "show", plan))).Should().Contain("2. Act on it all").And.NotContain("Report the flag");

        string status = await CaptureAsync(() => RunCliAsync(cts.Token, "status"));
        status.Should().Contain("Needs you").And.Contain($"parked on draft {DomainId.Short(original)}")
            .And.Contain($"h9k plan split accept {plan}").And.Contain($"h9k plan resume {plan}");
    }

    [Fact]
    public async Task Accept_is_refused_with_nothing_recorded_for_a_report_that_no_longer_splits_an_unnamed_choice_a_wrong_draft_or_a_spent_idea()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, Guid ideaId, Guid original, Guid sibling, string reportPath) = await ParkedOnSplitAsync(cts.Token);

        async Task NothingRecordedAsync()
        {
            PlanDetails plan = await PlanAsync(planId, cts.Token);
            plan.Drafts.Should().HaveCount(2);
            plan.Draft(original)!.ParkReason.Should().Be(PlanParkReason.SplitProposed);
            (await TaskAsync(original, cts.Token)).Objective.Should().Be("Build the whole flag feature");
            (await IdeaAsync(ideaId, cts.Token)).CutTaskIds.Should().HaveCount(2);
        }

        // A person's edit to the closing block counts.
        await File.WriteAllTextAsync(reportPath, "Edited.\n\nREFINEMENT: split\nTASK: Only one increment\n", cts.Token);
        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("no longer ends with a split").And.Contain("h9k plan resume");
        await NothingRecordedAsync();
        File.Delete(reportPath);
        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("not at").And.Contain(reportPath);
        await File.WriteAllTextAsync(reportPath, SplitReport, cts.Token);

        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString(), "--draft", DomainId.Short(sibling)))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("not parked on a proposed split").And.Contain(DomainId.Short(original));
        await NothingRecordedAsync();

        await ParkOnSplitAsync(planId, sibling, SplitReport, cts.Token);
        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("--draft");
        (await PlanAsync(planId, cts.Token)).Drafts.Should().HaveCount(2);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(ideaId, IdeaDecider.Conclude(await IdeaAsync(ideaId, cts.Token), "done another way", Now, _me.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString(), "--draft", original.ToString()))
            .Should().BeAssignableTo<DomainException>().Which.Message.Should().Contain("concluded");
        (await PlanAsync(planId, cts.Token)).Drafts.Should().HaveCount(2);
        (await TaskAsync(original, cts.Token)).Objective.Should().Be("Build the whole flag feature");
    }

    [Fact]
    public async Task Accept_vets_the_new_drafts_tracker_attestation_and_is_refused_on_a_plan_that_has_ended()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, _, _, _, _) = await ParkedOnSplitAsync(cts.Token);
        await UpdateProjectAsync(project => project.BacklogPolicy = BacklogPolicy.GitHubIssues, cts.Token);

        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()))
            .Should().BeOfType<DomainBusinessRuleException>().Which.Message.Should().Contain("--no-existing-item").And.Contain("--untracked");
        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString(), "--no-existing-item", "--untracked"))
            .Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("opposite things");
        (await PlanAsync(planId, cts.Token)).Drafts.Should().HaveCount(2);

        (await RunCliAsync(cts.Token, "plan", "cancel", planId.ToString(), "--reason", "changed course")).Should().BeNull();
        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString(), "--untracked"))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task An_attestation_given_to_the_accept_is_recorded_on_the_plan_for_its_publish()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, _, _, _, _) = await ParkedOnSplitAsync(cts.Token);
        await UpdateProjectAsync(project => project.BacklogPolicy = BacklogPolicy.GitHubIssues, cts.Token);

        (await RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString(), "--untracked")).Should().BeNull();

        PlanDetails plan = await PlanAsync(planId, cts.Token);
        (plan.Drafts.Count, plan.UntrackedAttested, plan.NoExistingItemAttested).Should().Be((4, true, false));
    }

    [Fact]
    public async Task Two_accepts_at_once_cut_one_set_of_drafts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid planId, Guid ideaId, _, _, _) = await ParkedOnSplitAsync(cts.Token);

        Exception?[] results = await Task.WhenAll(
            RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()),
            RunCliAsync(cts.Token, "plan", "split", "accept", planId.ToString()));

        results.Count(result => result is null).Should().Be(1, "exactly one of the two accepts won");
        results.Single(result => result is not null).Should().BeAssignableTo<DomainConflictException>();
        (await PlanAsync(planId, cts.Token)).Drafts.Should().HaveCount(4);
        (await IdeaAsync(ideaId, cts.Token)).CutTaskIds.Should().HaveCount(4);
    }
}
