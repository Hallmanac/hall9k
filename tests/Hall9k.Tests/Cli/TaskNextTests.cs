using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task next</c> over composed rows, one fixture per state family. Nothing here touches a
/// database: the row is what <see cref="TaskStatusComposer.Compose"/> returns, the run is the part
/// of a folded stream the command reads, and the closeout observation is read off fake events.
/// </summary>
public sealed class TaskNextTests
{
    private static readonly DateTimeOffset Now = StatusFixtures.Now;
    private const string PullRequest = "https://github.com/acme/widgets/pull/7";
    private const string ViewerRoot = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TeammateRoot = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed record Fixture(string Name, TaskNextFacts Facts);

    [Fact]
    public void A_draft_without_criteria_is_yours_and_names_the_revise_that_unblocks_publishing()
    {
        string text = Text(Pre(TaskState.Draft, criteria: 0));

        text.Should().Contain("Moves next: you.");
        text.Should().Contain("h9k task revise").And.Contain("--criteria");
    }

    [Fact]
    public void A_draft_with_criteria_names_publish_then_queue()
    {
        Fixture draft = Pre(TaskState.Draft, criteria: 2);

        Text(draft).Should().Contain($"h9k task publish {Short(draft)} then h9k task queue {Short(draft)}");
    }

    [Fact]
    public void A_published_task_is_yours_and_offers_the_interactive_claim()
    {
        Fixture published = Pre(TaskState.Published, criteria: 1);

        string text = Text(published);

        text.Should().Contain("Moves next: you.");
        text.Should().Contain($"h9k task queue {Short(published)}");
        text.Should().Contain($"(or h9k task drive {Short(published)} to claim and work it yourself)");
    }

    [Fact]
    public void An_open_dependency_changes_the_claim_hint_to_the_acknowledging_flag()
    {
        Fixture published = Pre(TaskState.Published, criteria: 1, openDependency: true);

        Text(published).Should().Contain("--acknowledge-unmet-dependencies to claim across the open dependency yourself");
    }

    [Theory]
    [InlineData("Queued", "the platform")]
    [InlineData("Blocked", "the platform")]
    public void A_queued_or_blocked_task_is_waiting_on_the_platform(string state, string who)
    {
        TaskState parsed = state;
        Fixture waiting = Pre(parsed, criteria: 1);

        string text = Text(waiting);

        text.Should().Contain($"Moves next: {who}.");
        text.Should().Contain($"h9k task dequeue {Short(waiting)}");
    }

    [Fact]
    public void The_pre_dispatch_text_is_the_text_task_show_prints()
    {
        foreach (TaskState state in new[] { TaskState.Draft, TaskState.Published, TaskState.Queued, TaskState.Blocked })
        {
            Fixture fixture = Pre(state, criteria: 1);

            string[] shown = [.. TaskNextStep.Lines(fixture.Facts.Details, false).Select(Markup.Remove)];

            Lines(fixture).Should().ContainInOrder(shown.Where(line => line.Length > 0));
        }
    }

    [Fact]
    public void A_build_under_the_owners_claim_with_no_session_is_the_owners()
    {
        Fixture building = Building(sessionProcessId: null);

        string text = Text(building);

        text.Should().Contain("Moves next: you. You hold the claim and no session is running on it.");
        text.Should().Contain("h9k task drive re-enters this claim");
    }

    [Fact]
    public void A_build_with_a_live_session_is_a_running_agent_and_the_phase_says_it_is_alive()
    {
        Fixture building = Building(sessionProcessId: 4711);

        string text = Text(building);

        text.Should().Contain("Moves next: a running agent.");
        text.Should().Contain("Now: building").And.Contain("session alive");
    }

    [Fact]
    public void The_owners_own_registered_session_building_is_not_a_running_agent_to_stay_out_of()
    {
        Fixture building = Building(sessionProcessId: 4711, ownersSession: true);

        string text = Text(building);

        text.Should().Contain("Moves next: you. Your own session is the one building here");
        text.Should().NotContain("leave its worktree alone");
    }

    [Theory]
    [InlineData("Verifying")]
    [InlineData("UnderReview")]
    public void A_delivered_run_with_no_session_is_the_pipelines_even_under_the_owners_claim(string state)
    {
        Fixture handedOver = Building(sessionProcessId: null, state: state);

        string text = Text(handedOver);

        text.Should().Contain("Moves next: the platform.");
        text.Should().NotContain("You hold the claim and no session is running on it");
    }

    [Fact]
    public void A_build_under_a_delegated_contractor_is_a_running_agent_too()
    {
        Fixture contractor = Building(sessionProcessId: 4711, delegated: true);

        Text(contractor).Should().Contain("Moves next: a running agent.")
            .And.Contain("leave its worktree alone until it reports");
    }

    [Theory]
    [InlineData(ReviewPhase.None, "the first one starts next")]
    [InlineData(ReviewPhase.AwaitingVerdict, "its verdict decides")]
    [InlineData(ReviewPhase.FixNeeded, "a fix session dispatches next")]
    [InlineData(ReviewPhase.VerdictMissing, "prompted once more")]
    [InlineData(ReviewPhase.AwaitingFix, "a fix session is working the findings")]
    [InlineData(ReviewPhase.Reverify, "the gates re-run, then a fresh review")]
    [InlineData(ReviewPhase.Settling, "the loop records how it ended")]
    [InlineData(ReviewPhase.MergeReady, "the pull request opens next")]
    [InlineData(ReviewPhase.Disputed, "disputed a finding")]
    [InlineData(ReviewPhase.Parked, "parked for a human")]
    [InlineData(ReviewPhase.AwaitingRebaseRecovery, "resolving a rebase conflict")]
    [InlineData(ReviewPhase.RebaseRecoveryDisputed, "could not be decided")]
    [InlineData(ReviewPhase.RebaseRecoveryNeeded, "fresh recovery session")]
    [InlineData(ReviewPhase.AwaitingSettlingGateRepair, "repair session is fixing")]
    [InlineData(ReviewPhase.SettlingGateRepairCapReached, "repair rounds are spent")]
    [InlineData(ReviewPhase.SettlingGateRepairNeeded, "one more repair session")]
    public void Each_review_phase_names_the_next_review_step(ReviewPhase phase, string expected)
    {
        Fixture review = UnderReview(phase);

        Text(review).Should().Contain($"Review cycle 2: ").And.Contain(expected);
    }

    [Fact]
    public void Every_review_phase_has_its_own_step_rather_than_the_fallback()
    {
        foreach (ReviewPhase phase in Enum.GetValues<ReviewPhase>())
        {
            TaskNextCommand.ReviewStep(phase).Should().NotStartWith("the loop is at", $"{phase} has no step of its own");
        }
    }

    [Theory]
    [InlineData(ReviewPhase.FixNeeded, InteractiveBoundaryLevers.ReviewVerdictToFix)]
    [InlineData(ReviewPhase.MergeReady, InteractiveBoundaryLevers.GatesToPullRequest)]
    [InlineData(ReviewPhase.None, InteractiveBoundaryLevers.ProceedOrRedirect)]
    [InlineData(ReviewPhase.Reverify, InteractiveBoundaryLevers.ProceedOrRedirect)]
    [InlineData(ReviewPhase.Settling, InteractiveBoundaryLevers.ProceedOrRedirect)]
    public void An_interactive_park_lists_the_choices_for_the_boundary_it_parked_from(
        ReviewPhase parkedFrom, InteractiveBoundaryLevers expected)
    {
        Fixture parked = Parked(parkedFrom, interactiveGate: true);

        string text = Text(parked);

        InteractiveBoundaryChoices.LeversFor(parkedFrom).Should().Be(expected);
        text.Should().Contain("Your choices at this park:");
        foreach (string choice in InteractiveBoundaryChoices.Lines(expected, parked.Facts.Details.Id))
        {
            text.Should().Contain($"  - {choice}");
        }
    }

    [Fact]
    public void The_boundary_mapping_names_no_boundary_for_a_phase_that_is_not_one()
    {
        ReviewPhase[] boundaries =
            [ReviewPhase.FixNeeded, ReviewPhase.MergeReady, ReviewPhase.None, ReviewPhase.Reverify, ReviewPhase.Settling];

        foreach (ReviewPhase phase in Enum.GetValues<ReviewPhase>().Except(boundaries))
        {
            InteractiveBoundaryChoices.LeversFor(phase).Should().BeNull($"{phase} is not an interactive boundary");
        }
    }

    [Fact]
    public void A_park_that_is_not_an_interactive_gate_offers_its_lever_and_no_boundary_choices()
    {
        Fixture disputed = Parked(ReviewPhase.Disputed, interactiveGate: false);

        string text = Text(disputed);

        text.Should().NotContain("Your choices at this park:");
        text.Should().Contain("Moves next: you.");
        text.Should().Contain($"Lever: {disputed.Facts.Row!.Attention.Lever}");
    }

    [Theory]
    [InlineData("AwaitingReview", "Closeout last recorded AwaitingReview at", "GitHub")]
    [InlineData("ChecksFailing", "failing checks build, lint", "the platform")]
    [InlineData("ReviewPending", "3 unresolved review threads", "the platform")]
    [InlineData("Conflicting", "a conflict with the base branch", "the platform")]
    [InlineData("CloseoutParked", "automatic follow-ups gave up after 3 laps", "you")]
    public void An_open_pull_request_states_what_closeout_last_recorded_and_when(
        string stateName, string carried, string mover)
    {
        RunState state = stateName;
        Fixture delivered = Closeout(state);

        string text = Text(delivered);

        text.Should().Contain($"Closeout last recorded {state.Value} at {Recorded:yyyy-MM-dd HH:mm} UTC (20m00s ago)");
        text.Should().Contain(carried);
        text.Should().Contain("Closeout records a change in that state, not every poll");
        text.Should().Contain($"Moves next: {mover}.");
        text.Should().Contain($"Pull request: {PullRequest}");
    }

    [Fact]
    public void A_pull_request_with_no_review_recorded_is_the_owners_to_read_and_merge()
    {
        Fixture delivered = Closeout(RunState.AwaitingReview, externalReview: ExternalReviewState.None);

        Text(delivered).Should().Contain("Moves next: you.").And.Contain("Needs you:");
    }

    [Fact]
    public void Closeouts_next_move_is_the_phase_and_attention_text_the_composers_give_the_state()
    {
        foreach (RunState state in new[] { RunState.ChecksFailing, RunState.ReviewPending, RunState.Conflicting })
        {
            Fixture delivered = Closeout(state);
            TaskStatusRow row = delivered.Facts.Row!;

            Text(delivered).Should().Contain($"Now: {Markup.Remove(row.Phase.Markup)}");
        }
    }

    [Fact]
    public void Closeout_reads_the_newest_event_that_set_the_state_and_ignores_older_ones()
    {
        IReadOnlyList<IEvent> stream =
        [
            Event(new PullRequestChecksFailed(Guid.Empty, ["old"], Recorded.AddHours(-5)), Recorded.AddHours(-5)),
            Event(new PullRequestOpened(Guid.Empty, PullRequest, 7, Recorded.AddHours(-6)), Recorded.AddHours(-6)),
            Event(new PullRequestChecksFailed(Guid.Empty, ["new"], Recorded), Recorded),
        ];

        CloseoutObservation? observed = CloseoutObservations.Last(stream, RunState.ChecksFailing);

        observed.Should().NotBeNull();
        observed!.Detail.Should().Be("failing checks new");
        observed.RecordedAt.Should().Be(Recorded);
    }

    [Fact]
    public void Closeout_time_is_the_one_the_event_carries_not_when_this_node_stored_it()
    {
        DateTimeOffset arrivedHere = Recorded.AddDays(3);
        IReadOnlyList<IEvent> stream = [Event(new PullRequestChecksFailed(Guid.Empty, ["lint"], Recorded), arrivedHere)];

        CloseoutObservation? observed = CloseoutObservations.Last(stream, RunState.ChecksFailing);

        observed.Should().NotBeNull();
        observed!.RecordedAt.Should().Be(Recorded);
    }

    [Fact]
    public void Closeout_says_nothing_it_did_not_observe_when_no_event_set_the_state()
    {
        IReadOnlyList<IEvent> stream = [Event(new PullRequestOpened(Guid.Empty, PullRequest, 7, Recorded), Recorded)];

        CloseoutObservations.Last(stream, RunState.Conflicting).Should().BeNull();
        Text(Closeout(RunState.Conflicting, withEvent: false)).Should().NotContain("Closeout last recorded");
    }

    [Fact]
    public void A_teammates_task_says_whose_it_is_and_names_no_command()
    {
        Fixture teammate = Teammate();

        string text = Text(teammate);

        text.Should().Contain("This is Casey Teammate's task (a teammate).");
        text.Should().Contain("Its next move is theirs");
        text.Should().NotContain("h9k ").And.NotContain("Lever:").And.NotContain("Moves next:");
    }

    [Fact]
    public void A_task_another_node_holds_is_not_described_as_this_nodes_pipeline()
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: DomainId.New());
        RunDetails run = StatusFixtures.Run(runId, RunState.Running);
        TaskStatusContext context = StatusFixtures.Context(run) with { MachineName = "another-machine" };
        TaskStatusRow row = TaskStatusComposer.Compose(task, context, Now);
        row.State.Should().Be(LifecycleState.HeldElsewhere, "the fixture must be composed as a task another node holds");
        Fixture held = new("held elsewhere", new TaskNextFacts(row, Details(task, TaskState.Claimed), false, false, Now));

        Text(held).Should().Contain("Moves next: the platform. Another node holds this task's claim")
            .And.NotContain("Nothing is asked of you");
    }

    [Fact]
    public void A_failed_task_is_yours_and_shows_the_three_exits()
    {
        Fixture failed = Terminal(TaskState.Failed);

        string text = Text(failed);

        text.Should().Contain("Moves next: you.");
        text.Should().Contain("Failed is a waypoint, not an ending — three exits:");
        text.Should().Contain($"h9k task retry {Short(failed)} --reason <why>");
        text.Should().Contain($"h9k task resolve {Short(failed)} --reason <why> [--pr <url>]");
        text.Should().Contain($"h9k task abandon {Short(failed)} [--reason <why>]");
    }

    [Fact]
    public void A_merged_task_and_an_abandoned_one_have_nobody_moving_next()
    {
        Text(Terminal(TaskState.Done, pullRequest: null)).Should().Contain("Moves next: nobody. The task is finished.");
        Text(Terminal(TaskState.Abandoned)).Should().Contain("Moves next: nobody. The task is finished.");
    }

    [Fact]
    public void A_task_without_a_composed_row_prints_the_state_it_read_and_the_next_step()
    {
        TaskDetails details = Details(StatusFixtures.Task(TaskState.Draft), TaskState.Draft, criteria: 0);

        string text = string.Join('\n', TaskNextCommand.Lines(new TaskNextFacts(null, details, false, false, Now)));

        text.Should().Contain("(no lifecycle row could be composed)");
        text.Should().Contain("h9k task revise");
    }

    [Fact]
    public void Wherever_the_board_shows_a_lever_task_next_prints_the_same_text_unchanged()
    {
        Fixture[] fixtures = All();
        int withLever = 0;

        foreach (Fixture fixture in fixtures)
        {
            TaskStatusRow row = fixture.Facts.Row!;
            if (row.Attention.Lever.IsBlank())
            {
                continue;
            }

            withLever++;
            Markup.Remove(row.Attention.Markup).Should().Contain(row.Attention.Lever, fixture.Name);
            Lines(fixture).Should().Contain($"Lever: {row.Attention.Lever}", fixture.Name);
        }

        withLever.Should().BeGreaterThan(5, "the fixtures must include enough levers for this check to mean anything");
    }

    [Fact]
    public void Every_fixture_prints_plain_text_with_no_markup_left_in_it()
    {
        foreach (Fixture fixture in All())
        {
            string text = Text(fixture);

            text.Should().NotMatchRegex(@"\[/\]|\[(dim|red|bold|yellow|green|blue)\]", fixture.Name);
            text.Should().NotContain("[[", fixture.Name);
        }
    }

    [Fact]
    public void The_command_is_classified_read_only_for_a_dispatched_session()
    {
        DispatchedSessionCommandClassification.BySettingsType[typeof(TaskNextCommand.Settings)]
            .Should().Be((DispatchedSessionAccess.ReadOnly, "task next"));
    }

    private static readonly DateTimeOffset Recorded = Now.AddMinutes(-20);

    private static string Short(Fixture fixture) => TaskListCommand.ShortId(fixture.Facts.Details.Id);

    private static IReadOnlyList<string> Lines(Fixture fixture) => TaskNextCommand.Lines(fixture.Facts);

    private static string Text(Fixture fixture) => string.Join('\n', Lines(fixture));

    private static TaskDetails Details(TaskListItem task, TaskState state, int criteria = 1, TaskType? type = null) => new()
    {
        Id = task.Id,
        ProjectId = task.ProjectId,
        Objective = task.Objective,
        State = state,
        Type = type ?? TaskType.Chore,
        AcceptanceCriteria = [.. Enumerable.Range(1, criteria).Select(index => $"criterion {index}")],
    };

    private static Fixture Pre(TaskState state, int criteria, bool openDependency = false)
    {
        TaskListItem task = StatusFixtures.Task(state, assignedAt: state == TaskState.Draft || state == TaskState.Published ? null : Now);
        TaskStatusRow row = StatusFixtures.Compose(task);
        return new Fixture($"pre-dispatch {state.Value}", new TaskNextFacts(
            row, Details(task, state, criteria), openDependency, false, Now));
    }

    private static Fixture Building(
        int? sessionProcessId, bool delegated = false, RunState? state = null, bool ownersSession = false)
    {
        Guid runId = DomainId.New();
        RunState runState = state ?? RunState.Running;
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty);
        RunDetails run = StatusFixtures.Run(
            runId, runState, sessionProcessId, ownersSession ? AgentRole.Interactive : null);
        if (delegated)
        {
            run.PhaseDelegations.Add(new PhaseDelegation(
                Now.AddMinutes(-5), "build it", DomainId.New(), "task-build", "contractor-1", AgentModel.Unknown));
        }

        TaskStatusRow row = StatusFixtures.Compose(task, run);
        return new Fixture(
            delegated ? "build under a contractor" : $"build under a claim ({sessionProcessId})",
            new TaskNextFacts(
                row, Details(task, TaskState.Claimed), false, true, Now,
                Run: new TaskNextRun(runState, ReviewPhase.None, 0, ReviewPhase.None, false, ownersSession)));
    }

    private static Fixture UnderReview(ReviewPhase phase)
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: DomainId.New());
        RunDetails run = StatusFixtures.Run(runId, RunState.UnderReview, sessionProcessId: null);
        TaskStatusRow row = StatusFixtures.Compose(task, run);
        return new Fixture($"review {phase}", new TaskNextFacts(
            row, Details(task, TaskState.Claimed), false, false, Now,
            Run: new TaskNextRun(RunState.UnderReview, phase, 2, ReviewPhase.None, false)));
    }

    private static Fixture Parked(ReviewPhase parkedFrom, bool interactiveGate)
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.NeedsHuman, runId, claimedByNodeId: DomainId.New());
        RunDetails run = StatusFixtures.Run(runId, RunState.ReviewParked, sessionProcessId: null);
        run.ParkedIsInteractiveGate = interactiveGate;
        run.ParkedReason = interactiveGate ? "an interactive boundary" : "a finding was disputed";
        TaskStatusRow row = StatusFixtures.Compose(task, run);
        return new Fixture($"park from {parkedFrom}", new TaskNextFacts(
            row, Details(task, TaskState.NeedsHuman), false, false, Now,
            Run: new TaskNextRun(RunState.ReviewParked, ReviewPhase.Parked, 1, parkedFrom, interactiveGate)));
    }

    private static Fixture Closeout(RunState state, bool withEvent = true, ExternalReviewState? externalReview = null)
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Done, runId, PullRequest);
        RunDetails run = StatusFixtures.Run(runId, state, sessionProcessId: null, pullRequestNumber: 7);
        run.FailingChecks = ["build", "lint"];
        run.UnresolvedReviewThreads = 3;
        run.ExternalReviewState = externalReview ?? ExternalReviewState.RequestedPending;
        TaskStatusRow row = StatusFixtures.Compose(task, run);

        CloseoutObservation? observation = null;
        if (withEvent)
        {
            object data = state.Value switch
            {
                "ChecksFailing" => new PullRequestChecksFailed(runId, ["build", "lint"], Recorded),
                "ReviewPending" => new ReviewFeedbackReceived(runId, 3, Recorded),
                "Conflicting" => new PullRequestConflictObserved(runId, Recorded),
                "CloseoutParked" => new CloseoutParked(runId, "automatic follow-ups gave up after 3 laps", Recorded),
                _ => new PullRequestOpened(runId, PullRequest, 7, Recorded),
            };
            observation = CloseoutObservations.Last([Event(data, Recorded)], state);
        }

        return new Fixture($"closeout {state.Value}", new TaskNextFacts(
            row, Details(task, TaskState.Done), false, false, Now,
            Run: new TaskNextRun(state, ReviewPhase.MergeReady, 1, ReviewPhase.None, false),
            Closeout: observation));
    }

    private static Fixture Teammate()
    {
        TaskListItem task = StatusFixtures.Task(TaskState.Queued, projectId: DomainId.New(), assignedAt: Now);
        task.AssignedOwnerFingerprint = TeammateRoot;
        task.AssigneeOwnerFingerprint = TeammateRoot;
        task.AssignedOwnerId = DomainId.New();
        task.AssigneeOwnerId = task.AssignedOwnerId;
        TaskStatusContext context = StatusFixtures.Context() with
        {
            ViewerRoot = ViewerRoot,
            OwnerRootsById = new Dictionary<Guid, string>(),
            CreatorFacts = new Dictionary<Guid, OwnerRootFact>(),
            ProjectMemberLabelsById = new Dictionary<Guid, ProjectMemberLabels>
            {
                [task.ProjectId] = new()
                {
                    Id = task.ProjectId,
                    Labels = [new ProjectMemberLabel(TeammateRoot, [DomainId.New()], DisplayName.Parse("Casey Teammate"), null)],
                },
            },
        };
        TaskStatusRow row = TaskStatusComposer.Compose(task, context, Now);
        row.IsTeammates.Should().BeTrue("the fixture must be composed as a teammate's task");
        return new Fixture("teammate", new TaskNextFacts(row, Details(task, TaskState.Queued), false, false, Now));
    }

    private static Fixture Terminal(TaskState state, string? pullRequest = PullRequest)
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(state, runId, pullRequest);
        task.FailureReason = state == TaskState.Failed ? "the build session exited with an error" : null;
        RunDetails run = StatusFixtures.Run(runId, state == TaskState.Done ? RunState.Completed : RunState.Failed, sessionProcessId: null);
        TaskStatusRow row = StatusFixtures.Compose(task, run);
        return new Fixture($"terminal {state.Value}", new TaskNextFacts(row, Details(task, state), false, false, Now));
    }

    private static Fixture[] All()
    {
        List<Fixture> fixtures =
        [
            Pre(TaskState.Draft, 0), Pre(TaskState.Draft, 2), Pre(TaskState.Published, 1),
            Pre(TaskState.Published, 1, openDependency: true), Pre(TaskState.Queued, 1), Pre(TaskState.Blocked, 1),
            Building(null), Building(4711), Building(4711, delegated: true), Teammate(),
            Terminal(TaskState.Failed), Terminal(TaskState.Done, pullRequest: null), Terminal(TaskState.Abandoned),
        ];
        fixtures.AddRange(Enum.GetValues<ReviewPhase>().Select(UnderReview));
        foreach (ReviewPhase parkedFrom in Enum.GetValues<ReviewPhase>())
        {
            fixtures.Add(Parked(parkedFrom, interactiveGate: true));
            fixtures.Add(Parked(parkedFrom, interactiveGate: false));
        }

        fixtures.AddRange(new[]
        {
            RunState.AwaitingReview, RunState.ChecksFailing, RunState.ReviewPending, RunState.Conflicting,
            RunState.CloseoutParked,
        }.Select(state => Closeout(state)));
        return [.. fixtures];
    }

    private static IEvent Event(object data, DateTimeOffset timestamp)
    {
        Type fake = typeof(FakeEvent<>).MakeGenericType(data.GetType());
        IEvent @event = (IEvent)Activator.CreateInstance(fake, data)!;
        @event.Timestamp = timestamp;
        return @event;
    }
}
