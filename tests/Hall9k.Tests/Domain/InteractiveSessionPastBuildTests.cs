using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The driving session registers against a run the daemon's pipeline already owns (after
/// <c>h9k task deliver</c>), and recording it must move nothing: the run stays in the stage the
/// pipeline put it in, in the aggregate and in both projections, and the review or fix session the
/// daemon recorded stays recorded. Before the build is delivered the same event still means the run
/// is Running.
/// </summary>
public sealed class InteractiveSessionPastBuildTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Verifying", false, true)]
    [InlineData("UnderReview", false, true)]
    [InlineData("ReviewParked", false, true)]
    [InlineData("BudgetParked", true, true)]
    [InlineData("LaunchHeld", true, true)]
    [InlineData("BudgetParked", false, false)]
    [InlineData("LaunchHeld", false, false)]
    [InlineData("Dispatched", false, false)]
    [InlineData("Running", false, false)]
    public void The_pipeline_stages_are_exactly_the_ones_past_the_build(string state, bool delivered, bool pastBuild)
    {
        RunState parsed = state;

        parsed.IsPastBuild(delivered).Should().Be(pastBuild);
    }

    [Theory]
    [InlineData("BudgetParked")]
    [InlineData("LaunchHeld")]
    public void A_park_after_the_delivery_keeps_the_driving_session_and_the_run_where_it_is(string park)
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = UnderReviewWithDrivingSession(projection, id);
        view.ActiveSessions.Should().HaveCount(2);

        if (park == "BudgetParked")
        {
            projection.Apply(new FakeEvent<RunBudgetExhausted>(new RunBudgetExhausted(id, "limit", Now)), view);
        }
        else
        {
            projection.Apply(new FakeEvent<RunLaunchHeld>(new RunLaunchHeld(id, "no launch", Now)), view);
        }

        view.State.Value.Should().Be(park);
        view.IsPastBuild().Should().BeTrue();
        view.ActiveSessions.Should().Contain(session => session.Role == AgentRole.Interactive && session.ProcessId == 9001,
            "the owner's driving session is not the process that hit the limit");

        // A registration while parked records itself and leaves the park standing.
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9002, "operator-mac", "again")), view);
        view.State.Value.Should().Be(park);
        view.ActiveSessions.Should().ContainSingle(session => session.Role == AgentRole.Interactive && session.ProcessId == 9002);
    }

    [Fact]
    public void The_retry_sweeps_fix_redispatch_after_a_budget_park_keeps_the_driving_session()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = UnderReviewWithDrivingSession(projection, id);
        projection.Apply(new FakeEvent<RunBudgetExhausted>(new RunBudgetExhausted(id, "limit", Now)), view);

        projection.Apply(new FakeEvent<ReviewFixDispatched>(
            new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now)), view);

        view.State.Should().Be(RunState.UnderReview);
        view.ActiveSessions.Select(session => session.Role).Should().BeEquivalentTo([AgentRole.Fix, AgentRole.Interactive]);
    }

    [Theory]
    [InlineData("BudgetParked")]
    [InlineData("LaunchHeld")]
    public void A_park_before_the_delivery_is_still_the_build_and_a_registration_there_runs_it(string park)
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "build")), view);
        // The daemon appends AgentSessionCompleted whenever the build session exits, including on
        // the usage limit or a launch failure, so a build park always follows it.
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        if (park == "BudgetParked")
        {
            projection.Apply(new FakeEvent<RunBudgetExhausted>(new RunBudgetExhausted(id, "limit", Now)), view);
        }
        else
        {
            projection.Apply(new FakeEvent<RunLaunchHeld>(new RunLaunchHeld(id, "no launch", Now)), view);
        }

        view.IsPastBuild().Should().BeFalse();
        view.IsPipelineSession(new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, 9001, Now)).Should().BeTrue();

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9002, "operator-mac", "build")), view);

        view.State.Should().Be(RunState.Running);
    }

    [Fact]
    public void The_aggregate_keeps_the_stage_the_pipeline_put_the_run_in()
    {
        RunAggregate underReview = new();
        Guid id = DomainId.New();
        underReview.Apply(Dispatched(id));
        underReview.Apply(new AgentSessionCompleted(id, Now));
        underReview.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance));
        underReview.State.Should().Be(RunState.UnderReview);

        Guid claudeSessionId = DomainId.New();
        underReview.Apply(new InteractiveSessionStarted(id, claudeSessionId, Now, ProcessId: 4242));

        underReview.State.Should().Be(RunState.UnderReview, "registering the driving session is not a pipeline event");
        underReview.InteractiveClaudeSessionId.Should().Be(claudeSessionId, "the session itself is recorded");
        underReview.InteractiveSessionCount.Should().Be(1);

        RunAggregate verifying = new();
        verifying.Apply(Dispatched(id));
        verifying.Apply(new AgentSessionCompleted(id, Now));
        verifying.Apply(new InteractiveSessionStarted(id, claudeSessionId, Now, ProcessId: 4242));
        verifying.State.Should().Be(RunState.Verifying);
    }

    [Fact]
    public void The_aggregate_still_moves_a_dispatched_run_to_running()
    {
        RunAggregate run = new();
        Guid id = DomainId.New();
        run.Apply(Dispatched(id));

        run.Apply(new InteractiveSessionStarted(id, DomainId.New(), Now, ProcessId: 4242));

        run.State.Should().Be(RunState.Running);
    }

    [Fact]
    public void The_details_projection_records_a_registration_at_under_review_beside_the_review_session()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        view.State.Should().Be(RunState.UnderReview);

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), view);

        view.State.Should().Be(RunState.UnderReview);
        view.RegisteredInteractiveSessionName.Should().Be("brians-terminal");
        view.ActiveSessions.Should().HaveCount(2);
        view.ActiveSessions.Should().ContainSingle(session => session.Role == AgentRole.Review && session.ProcessId == 5001,
            "the review session the daemon recorded stays recorded");
        view.ActiveSessions.Should().ContainSingle(session => session.Role == AgentRole.Interactive && session.ProcessId == 9001);
    }

    [Fact]
    public void A_second_registration_past_the_build_replaces_only_the_earlier_driving_session()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "first")), view);

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9002, "operator-mac", "second")), view);

        view.ActiveSessions.Select(session => session.ProcessId).Should().BeEquivalentTo([5001, 9002]);
        view.RegisteredInteractiveSessionName.Should().Be("second");
    }

    [Fact]
    public void The_registered_conversation_survives_a_contractor_started_after_it()
    {
        // h9k task delegate records its contractor with the same event; resuming "the latest started
        // session" would put the person in the contractor's conversation.
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid registered = DomainId.New();
        Guid contractor = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, registered, Now, 9001, "operator-mac", "brians-terminal")), view);

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(
                id, contractor, Now, 9002, "operator-mac", SessionRoleName.For(DomainId.Short(view.TaskId), SessionRoleName.Build))), view);

        view.InteractiveClaudeSessionId.Should().Be(contractor, "the latest started session is the contractor's");
        view.RegisteredInteractiveClaudeSessionId.Should().Be(registered);
        view.RegisteredInteractiveSessionName.Should().Be("brians-terminal");
    }

    [Fact]
    public void No_conversation_is_registered_until_a_human_session_registers()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(
                id, DomainId.New(), Now, 9002, "operator-mac", SessionRoleName.For(DomainId.Short(view.TaskId), SessionRoleName.Build))), view);

        view.RegisteredInteractiveClaudeSessionId.Should().BeNull();
    }

    [Fact]
    public void The_details_projection_leaves_a_parked_run_parked()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        projection.Apply(new FakeEvent<ReviewParked>(new ReviewParked(id, "verdict to fix", Now, IsInteractiveGate: true)), view);
        view.State.Should().Be(RunState.ReviewParked);

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), view);

        view.State.Should().Be(RunState.ReviewParked);
    }

    [Fact]
    public void The_details_projection_still_replaces_the_build_sessions_and_runs_a_dispatched_run()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), view);

        view.State.Should().Be(RunState.Running);
        view.ActiveSessions.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interactive);
    }

    [Fact]
    public void The_list_row_keeps_the_pipelines_stage_and_still_runs_a_dispatched_run()
    {
        RunListItemProjection projection = new();
        Guid id = DomainId.New();
        RunListItem row = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), row);
        row.State.Should().Be(RunState.Running);

        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), row);
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9002, "operator-mac", "brians-terminal")), row);
        row.State.Should().Be(RunState.Verifying);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), row);
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9003, "operator-mac", "brians-terminal")), row);
        row.State.Should().Be(RunState.UnderReview);
    }

    [Fact]
    public void A_park_after_the_delivery_stays_parked_in_the_aggregate_and_the_list_row()
    {
        Guid id = DomainId.New();
        RunAggregate aggregate = new();
        aggregate.Apply(Dispatched(id));
        aggregate.Apply(new AgentSessionCompleted(id, Now));
        aggregate.Apply(new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance));
        aggregate.Apply(new RunBudgetExhausted(id, "limit", Now));
        aggregate.Apply(new InteractiveSessionStarted(id, DomainId.New(), Now, ProcessId: 4242));
        aggregate.State.Should().Be(RunState.BudgetParked);

        RunListItemProjection projection = new();
        RunListItem row = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), row);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), row);
        projection.Apply(new FakeEvent<RunLaunchHeld>(new RunLaunchHeld(id, "no launch", Now)), row);
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), row);
        row.State.Should().Be(RunState.LaunchHeld);
    }

    [Fact]
    public void A_build_that_parks_on_its_own_completion_is_not_delivered_in_the_aggregate_and_the_list_row()
    {
        // The daemon appends AgentSessionCompleted before RunBudgetExhausted or RunLaunchHeld when the
        // build session itself exits on the limit, so the completion alone cannot mark the delivery.
        Guid id = DomainId.New();
        RunAggregate aggregate = new();
        aggregate.Apply(Dispatched(id));
        aggregate.Apply(new AgentSessionCompleted(id, Now));
        aggregate.Apply(new RunBudgetExhausted(id, "limit", Now));
        aggregate.Delivered.Should().BeFalse();
        aggregate.Apply(new InteractiveSessionStarted(id, DomainId.New(), Now, ProcessId: 4242));
        aggregate.State.Should().Be(RunState.Running);

        RunListItemProjection projection = new();
        RunListItem row = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), row);
        projection.Apply(new FakeEvent<RunLaunchHeld>(new RunLaunchHeld(id, "no launch", Now)), row);
        row.Delivered.Should().BeFalse();
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), row);
        row.State.Should().Be(RunState.Running);
    }

    [Fact]
    public void The_driving_session_stays_recorded_through_the_pipelines_own_milestones()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = UnderReviewWithDrivingSession(projection, id);

        projection.Apply(new FakeEvent<ReviewCompleted>(new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now)), view);
        view.ActiveSessions.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interactive);

        projection.Apply(new FakeEvent<ReviewParked>(new ReviewParked(id, "verdict to fix", Now, IsInteractiveGate: true)), view);
        view.ActiveSessions.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interactive);

        projection.Apply(new FakeEvent<ReviewFixDispatched>(
            new ReviewFixDispatched(id, DomainId.New(), 1, 5002, Now, Now)), view);
        view.ActiveSessions.Select(session => session.Role).Should().BeEquivalentTo([AgentRole.Fix, AgentRole.Interactive]);

        projection.Apply(new FakeEvent<ReviewFixCompleted>(
            new ReviewFixCompleted(id, 1, ReviewFixOutcome.Fixed, Now)), view);
        view.ActiveSessions.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interactive);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 2, 5003, Now, Now, Lens: ReviewLens.Conformance)), view);
        view.ActiveSessions.Select(session => session.Role).Should().BeEquivalentTo([AgentRole.Review, AgentRole.Interactive]);
    }

    [Fact]
    public void The_driving_sessions_own_end_leaves_the_pipelines_sessions_recorded()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = UnderReviewWithDrivingSession(projection, id);

        projection.Apply(new FakeEvent<InteractiveSessionEnded>(
            new InteractiveSessionEnded(id, DomainId.New(), Now, Turns: null, InputTokens: null, OutputTokens: null, CostUsd: null)), view);

        view.ActiveSessions.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Review);
    }

    [Fact]
    public void Opening_the_pull_request_still_clears_every_session()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = UnderReviewWithDrivingSession(projection, id);

        projection.Apply(new FakeEvent<PullRequestOpened>(new PullRequestOpened(id, "https://example.test/pr/1", 1, Now)), view);

        view.ActiveSessions.Should().BeEmpty();
    }

    private static RunDetails UnderReviewWithDrivingSession(RunDetailsProjection projection, Guid id)
    {
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(id)));
        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(id, DomainId.New(), Now, 9001, "operator-mac", "brians-terminal")), view);
        return view;
    }

    private static RunDispatched Dispatched(Guid id) => new(
        id, DomainId.New(), Guid.Empty, DomainId.New(), LeaseGeneration: 1, SessionId: id,
        WorktreePath: "/wt/x", Branch: "task/x", ExecutorMode.Subscription, Now);
}
