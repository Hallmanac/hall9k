using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The handoff through the run aggregate and read model (Decisions Log #36): what a
/// completed run hands down, the recorded absence when it hands down nothing, and the
/// historical stream that replays as Unknown rather than as a reconstruction.
/// </summary>
public sealed class RunHandoffProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_captured_handoff_lands_on_the_run_with_its_text()
    {
        Guid id = DomainId.New();
        RunAggregate run = Dispatched(id);

        run.Apply(new RunHandoffRecorded(id, HandoffOutcome.Captured, "Watch the nullable column.", Now));

        run.HandoffOutcome.Should().Be(HandoffOutcome.Captured);
        run.HandoffOutcome.HasSummary.Should().BeTrue();
        run.HandoffSummary.Should().Be("Watch the nullable column.");
    }

    [Fact]
    public void A_run_that_closed_out_without_a_handoff_records_the_absence_and_stays_valid()
    {
        Guid id = DomainId.New();
        RunAggregate run = Dispatched(id);

        run.Apply(new RunHandoffRecorded(id, HandoffOutcome.NotCaptured, null, Now));
        run.Apply(new RunCompleted(id, Now));

        run.State.Should().Be(RunState.Completed, "a run with no handoff closes out like any other");
        run.HandoffOutcome.Should().Be(HandoffOutcome.NotCaptured);
        run.HandoffOutcome.HasSummary.Should().BeFalse();
        run.HandoffSummary.Should().BeNull("the absence is recorded, never dressed up as an empty handoff");
    }

    [Fact]
    public void A_historical_stream_with_no_handoff_event_replays_as_unknown()
    {
        Guid id = DomainId.New();
        RunAggregate run = Dispatched(id);
        run.Apply(new AgentSessionCompleted(id, Now));
        run.Apply(new RunCompleted(id, Now));

        run.HandoffOutcome.Should().Be(HandoffOutcome.Unknown,
            "streams written before handoffs existed say they do not know, rather than guessing");
        run.HandoffSummary.Should().BeNull();
        run.State.Should().Be(RunState.Completed, "an older stream replays and closes out unchanged");
    }

    [Fact]
    public void Run_details_carries_the_handoff_for_the_dependent_context_query()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(NewDispatch(id)));

        projection.Apply(
            new FakeEvent<RunHandoffRecorded>(
                new RunHandoffRecorded(id, HandoffOutcome.Captured, "The gate is the fast one.", Now)),
            view);
        projection.Apply(new FakeEvent<RunCompleted>(new RunCompleted(id, Now)), view);

        view.State.Should().Be(RunState.Completed);
        view.HandoffOutcome.Should().Be(HandoffOutcome.Captured);
        view.HandoffSummary.Should().Be("The gate is the fast one.");
    }

    [Fact]
    public void Run_details_starts_unknown_so_an_unfinished_run_never_reads_as_handing_nothing_down()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(NewDispatch(id)));

        view.HandoffOutcome.Should().Be(HandoffOutcome.Unknown,
            "a run still working has not yet been asked, which is different from having nothing to say");
        view.HandoffSummary.Should().BeNull();
    }

    [Fact]
    public void Synthesis_sessions_are_counted_on_the_run_that_paid_for_them()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(NewDispatch(id)));

        projection.Apply(
            new FakeEvent<ContextSynthesisDispatched>(
                new ContextSynthesisDispatched(id, DomainId.New(), 5, 4242, Now, Now, AgentModel.Opus)),
            view);

        view.ContextSynthesisSessions.Should().Be(1, "the condensing pass is bookkeeping on the dependent's run");
        view.State.Should().Be(RunState.Dispatched, "synthesis neither moves the run state nor gates anything");
    }

    private static RunAggregate Dispatched(Guid id)
    {
        RunAggregate run = new();
        run.Apply(NewDispatch(id));
        return run;
    }

    private static RunDispatched NewDispatch(Guid id) => new(
        id, DomainId.New(), DomainId.New(), DomainId.New(), 1, DomainId.New(),
        "/tmp/worktree", "task/x", ExecutorMode.Subscription, Now);
}
