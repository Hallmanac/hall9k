using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The sessions a run has in flight, as the run stream records them (Decisions Log #65). This is
/// the half of the phase line that cannot be inferred: a run sits in UnderReview while a
/// reviewer reads, while a fix session edits the worktree, and while nothing at all is running,
/// and only these records tell them apart. Everything the display then adds is one observation of
/// each recorded process.
/// <para>
/// Origin incident (2026-08-22): the board said the lane was quiet while a fix agent was editing
/// the worktree, and the orchestrator nearly rewrote history under it.
/// </para>
/// </summary>
public sealed class RunSessionProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_build_session_is_recorded_when_it_starts_and_cleared_when_it_ends()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);

        view.ActiveSessions.Should().BeEmpty("nothing has been spawned yet");

        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4484, Now)), view);
        view.ActiveSessions.Should().ContainSingle().Which
            .Should().Be(new ActiveSession(AgentRole.Build, ReviewLens.Unknown, 4484, Now),
                "pid plus start time is the identity (log #2)");

        projection.Apply(new FakeEvent<AgentSessionCompleted>(new AgentSessionCompleted(id, Now)), view);
        view.ActiveSessions.Should().BeEmpty("the gates run in the daemon, not in a session");
        view.ProcessId.Should().Be(4484, "the build session's own record is history and stays");
    }

    [Fact]
    public void A_resumed_session_records_a_pid_with_no_start_time_so_liveness_stays_unobservable()
    {
        // RunResumed carries only a pid (log #5's exit-and-resume). Half an identity is not an
        // identity, and the display says unobserved rather than checking a pid that may have
        // been reused.
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);

        projection.Apply(new FakeEvent<RunResumed>(new RunResumed(id, 5150, Now)), view);

        ActiveSession resumed = view.ActiveSessions.Should().ContainSingle().Subject;
        resumed.Role.Should().Be(AgentRole.Build);
        resumed.ProcessId.Should().Be(5150);
        resumed.StartedAt.Should().BeNull();
    }

    [Fact]
    public void Each_review_pass_keeps_its_own_process_and_clears_only_when_that_pass_answers()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5002, Now, Now, Lens: ReviewLens.Adversarial)), view);

        // Both passes read the same worktree at the same time (log #59), so both processes are
        // recorded. A single slot would hold 5002 alone, and 5001 — the pass the engine actually
        // waits on first — would be invisible to any reader asking whether it is still alive.
        view.ActiveSessions.Should().Equal(
            new ActiveSession(AgentRole.Review, ReviewLens.Conformance, 5001, Now),
            new ActiveSession(AgentRole.Review, ReviewLens.Adversarial, 5002, Now));

        projection.Apply(new FakeEvent<ReviewPassCompleted>(
            new ReviewPassCompleted(id, 1, ReviewLens.Conformance, ReviewVerdict.MergeReady, Now)), view);
        // The slower track is still reading, which is exactly what the phase line says.
        view.ActiveSessions.Should().ContainSingle().Which.Lens.Should().Be(ReviewLens.Adversarial);

        projection.Apply(new FakeEvent<ReviewPassCompleted>(
            new ReviewPassCompleted(id, 1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes, Now)), view);
        view.ActiveSessions.Should().BeEmpty("nothing is reading any more");
    }

    [Fact]
    public void The_pass_that_answers_first_takes_only_its_own_session_with_it()
    {
        // The engine waits the cycle's passes in dispatch order, but they exit in whatever order
        // their diffs allow, and a pass may be recorded as answered before the one dispatched
        // ahead of it. Whichever order it happens in, the pass still reading keeps its process on
        // the record — losing it is how a healthy cycle came to read as a dead one, red and
        // bucketed as stalled while the conformance reviewer was working normally.
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5002, Now, Now, Lens: ReviewLens.Adversarial)), view);
        projection.Apply(new FakeEvent<ReviewPassCompleted>(
            new ReviewPassCompleted(id, 1, ReviewLens.Adversarial, ReviewVerdict.NeedsFixes, Now)), view);

        view.ActiveSessions.Should().ContainSingle().Which
            .Should().Be(new ActiveSession(AgentRole.Review, ReviewLens.Conformance, 5001, Now));
    }

    [Fact]
    public void A_verdict_reprompt_replaces_its_own_tracks_pass_and_leaves_the_other_alone()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);
        Guid resumed = DomainId.New();

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, resumed, 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5002, Now, Now, Lens: ReviewLens.Adversarial)), view);
        projection.Apply(new FakeEvent<ReviewVerdictReprompted>(
            new ReviewVerdictReprompted(id, DomainId.New(), resumed, 1, 5003, Now, Now, Lens: ReviewLens.Conformance)),
            view);

        view.ActiveSessions.Should().Equal(
            new ActiveSession(AgentRole.Review, ReviewLens.Adversarial, 5002, Now),
            new ActiveSession(AgentRole.Review, ReviewLens.Conformance, 5003, Now));
    }

    [Fact]
    public void A_new_cycle_does_not_inherit_the_last_cycles_lenses()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);

        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 1, 5001, Now, Now, Lens: ReviewLens.Conformance)), view);
        // The cycle concluded without a per-lens milestone, which is what a run whose review
        // predates pass milestones looks like on replay.
        projection.Apply(new FakeEvent<ReviewCompleted>(
            new ReviewCompleted(id, 1, ReviewVerdict.NeedsFixes, Now)), view);
        projection.Apply(new FakeEvent<ReviewDispatched>(
            new ReviewDispatched(id, DomainId.New(), 2, 5003, Now, Now, Lens: ReviewLens.Adversarial)), view);

        view.ReviewCycle.Should().Be(2);
        view.ActiveSessions.Should().ContainSingle().Which.Lens.Should().Be(ReviewLens.Adversarial);
    }

    [Fact]
    public void A_fix_session_is_recorded_as_a_fix_session_and_not_as_a_reviewer()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);

        projection.Apply(new FakeEvent<ReviewFixDispatched>(
            new ReviewFixDispatched(id, DomainId.New(), 2, 6001, Now, Now)), view);
        ActiveSession fixing = view.ActiveSessions.Should().ContainSingle().Subject;
        fixing.Role.Should().Be(AgentRole.Fix);
        fixing.ProcessId.Should().Be(6001);

        projection.Apply(new FakeEvent<ReviewFixCompleted>(
            new ReviewFixCompleted(id, 2, ReviewFixOutcome.Fixed, Now)), view);
        view.ActiveSessions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("park")]
    [InlineData("pull-request")]
    [InlineData("completed")]
    [InlineData("killed")]
    public void Every_ending_clears_the_session_so_no_finished_run_reads_as_a_running_one(string ending)
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = Dispatched(projection, id);
        projection.Apply(new FakeEvent<RunProcessStarted>(new RunProcessStarted(id, 4484, Now)), view);

        switch (ending)
        {
            case "park":
                projection.Apply(new FakeEvent<ReviewParked>(new ReviewParked(id, "budget spent", Now)), view);
                break;
            case "pull-request":
                projection.Apply(new FakeEvent<PullRequestOpened>(
                    new PullRequestOpened(id, "https://github.com/x/y/pull/7", 7, Now)), view);
                break;
            case "completed":
                projection.Apply(new FakeEvent<RunCompleted>(new RunCompleted(id, Now)), view);
                break;
            default:
                projection.Apply(new FakeEvent<RunKilled>(
                    new RunKilled(id, KillReason.HumanRequested, DomainId.New(), Now)), view);
                break;
        }

        view.ActiveSessions.Should().BeEmpty();
    }

    private static RunDetails Dispatched(RunDetailsProjection projection, Guid id) =>
        projection.Create(new FakeEvent<RunDispatched>(new RunDispatched(
            id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
            "/wt/x", "task/x", ExecutorMode.Subscription, Now)));
}
