using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The spike task type's own contract on <see cref="TaskDecider"/> (task: a spike is a run, not
/// a walk): a spike's kind and exit criterion round-trip through Add, are required at Publish,
/// are the one carve-out <see cref="TaskDecider.Revise"/> lets through on an already-Published
/// task, and <see cref="TaskDecider.ConcludeSpike"/> is the only door that ever records a
/// verdict. Mirrors <see cref="TaskDeciderTests"/>'s own conventions: fast, DB-free unit tests
/// built entirely from bare <see cref="TaskAggregate"/> Apply calls.
/// </summary>
public sealed class SpikeTaskDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Owner = DomainId.New();

    // -------------------------------------------------------------------------------------
    // Add
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Add_with_a_spike_kind_and_exit_criterion_round_trips_onto_TaskAdded()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Spike whether X is feasible", ["a working demo exists"],
            TaskType.Spike, agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, spikeKind: SpikeKind.Prototype,
            exitCriterion: "  A working demo proves X is feasible.  ");

        added.SpikeKind.Should().Be(SpikeKind.Prototype);
        added.ExitCriterion.Should().Be("A working demo proves X is feasible.", "the exit criterion is trimmed like every other free-text field");
    }

    [Fact]
    public void Add_leaves_spike_kind_and_exit_criterion_null_when_unstated()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "An ordinary feature", ["it works"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, addedAt: Now, addedByOwnerId: Owner);

        added.SpikeKind.Should().BeNull();
        added.ExitCriterion.Should().BeNull();
    }

    [Fact]
    public void Add_refuses_a_spike_review_stage_composition_override()
    {
        Action act = () => TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Spike whether X is feasible", ["a demo exists"], TaskType.Spike,
            agentContext: null, constraints: null, externalReference: null, addedAt: Now, addedByOwnerId: Owner,
            spikeKind: SpikeKind.Research, exitCriterion: "one sentence",
            reviewStageComposition: "skip-final-pass", reviewStageCompositionAcknowledged: true);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*spike*")
            .WithMessage("*review pipeline is fixed*")
            .WithMessage("*--review-stage-composition*");
    }

    // -------------------------------------------------------------------------------------
    // Publish — the explicit "publish refusal without kind or exit criterion" acceptance
    // criterion.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Publish_refuses_a_spike_missing_its_kind()
    {
        TaskAggregate task = SpikeDraft(spikeKind: null, exitCriterion: "One checkable sentence.");

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*requires a kind*")
            .WithMessage($"*h9k task revise {task.Id} --kind*");
    }

    [Fact]
    public void Publish_refuses_a_spike_missing_its_exit_criterion()
    {
        TaskAggregate task = SpikeDraft(spikeKind: SpikeKind.Research, exitCriterion: null);

        Action act = () => TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*requires an exit criterion*")
            .WithMessage($"*h9k task revise {task.Id} --exit-criterion*");
    }

    [Fact]
    public void Publish_succeeds_once_both_kind_and_exit_criterion_are_set()
    {
        TaskAggregate task = SpikeDraft(spikeKind: SpikeKind.Experiment, exitCriterion: "It measurably regresses latency.");

        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner);
        task.Apply(published);

        task.State.Should().Be(TaskState.Published);
    }

    // -------------------------------------------------------------------------------------
    // Revise — the Published-spike carve-out, and every refusal around it.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Revise_can_change_kind_exit_criterion_and_budget_on_a_published_spike_alone()
    {
        TaskAggregate task = PublishedSpike();
        TaskConstraints budget = new(MaxTurns: 20, MaxTokens: 500_000, MaxWallClock: TimeSpan.FromHours(2));

        TaskRevised revised = TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            spikeKind: Optional<SpikeKind>.Of(SpikeKind.Experiment),
            exitCriterion: Optional<string>.Of("A sharper criterion."),
            constraints: Optional<TaskConstraints?>.Of(budget));
        task.Apply(revised);

        task.State.Should().Be(TaskState.Published, "the spike carve-out never moves the task off Published");
        task.SpikeKind.Should().Be(SpikeKind.Experiment);
        task.ExitCriterion.Should().Be("A sharper criterion.");
        task.Constraints.Should().Be(budget);
    }

    [Fact]
    public void Revise_of_spike_fields_on_a_claimed_spike_throws()
    {
        TaskAggregate task = ClaimedSpike(out _);

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            spikeKind: Optional<SpikeKind>.Of(SpikeKind.Prototype));

        act.Should().Throw<DomainConflictException>().WithMessage("*only a draft can be revised*");
    }

    [Fact]
    public void Revise_of_spike_fields_combined_with_an_unrelated_field_on_a_published_spike_throws()
    {
        TaskAggregate task = PublishedSpike();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.Of("A rewritten objective"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now, Owner,
            spikeKind: Optional<SpikeKind>.Of(SpikeKind.Prototype));

        act.Should().Throw<DomainConflictException>().WithMessage(
            "*only a draft can be revised*",
            "the spike carve-out is narrow: nothing else may travel in the same revision");
    }

    [Fact]
    public void Revise_refuses_a_spikes_kind_revised_to_unknown()
    {
        TaskAggregate task = PublishedSpike();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            spikeKind: Optional<SpikeKind>.Of(SpikeKind.Unknown));

        act.Should().Throw<DomainValidationException>().WithMessage("*cannot be revised to nothing*");
    }

    [Fact]
    public void Revise_refuses_a_blanked_exit_criterion()
    {
        TaskAggregate task = PublishedSpike();

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            exitCriterion: Optional<string>.Of("   "));

        act.Should().Throw<DomainValidationException>().WithMessage("*cannot be blanked*");
    }

    [Fact]
    public void Revise_refuses_spike_fields_on_a_non_spike_task()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "An ordinary feature", ["it works"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, addedAt: Now, addedByOwnerId: Owner));

        Action act = () => TaskDecider.Revise(
            task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now, Owner,
            spikeKind: Optional<SpikeKind>.Of(SpikeKind.Research));

        act.Should().Throw<DomainValidationException>().WithMessage("*is not a spike*");
    }

    // -------------------------------------------------------------------------------------
    // ConcludeSpike
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ConcludeSpike_throws_on_a_non_spike_task()
    {
        TaskAggregate task = ClaimedTask();

        Action act = () => TaskDecider.ConcludeSpike(
            task, task.CurrentRunId!.Value, SpikeVerdict.Met, "It works.", "findings.md", Now);

        act.Should().Throw<DomainConflictException>().WithMessage("*not a spike*");
    }

    [Fact]
    public void ConcludeSpike_throws_when_the_spike_is_not_claimed()
    {
        TaskAggregate task = PublishedSpike();

        Action act = () => TaskDecider.ConcludeSpike(
            task, DomainId.New(), SpikeVerdict.Met, "It works.", "findings.md", Now);

        act.Should().Throw<DomainConflictException>().WithMessage("*only a claimed spike concludes*");
    }

    [Fact]
    public void ConcludeSpike_throws_on_an_unknown_verdict()
    {
        TaskAggregate task = ClaimedSpike(out Guid runId);

        Action act = () => TaskDecider.ConcludeSpike(
            task, runId, SpikeVerdict.Unknown, "It works.", "findings.md", Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*met, not-met, or budget-exhausted*");
    }

    [Fact]
    public void ConcludeSpike_throws_on_a_blank_reason()
    {
        TaskAggregate task = ClaimedSpike(out Guid runId);

        Action act = () => TaskDecider.ConcludeSpike(
            task, runId, SpikeVerdict.Met, "   ", "findings.md", Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*needs a reason*");
    }

    [Fact]
    public void ConcludeSpike_succeeds_on_a_claimed_spike_and_records_the_right_verdict()
    {
        TaskAggregate task = ClaimedSpike(out Guid runId);

        SpikeConcluded concluded = TaskDecider.ConcludeSpike(
            task, runId, SpikeVerdict.NotMet, "  The exit criterion was not met.  ", "runs/x/findings.md", Now);
        task.Apply(concluded);

        concluded.Id.Should().Be(task.Id);
        concluded.RunId.Should().Be(runId);
        concluded.Verdict.Should().Be(SpikeVerdict.NotMet);
        concluded.Reason.Should().Be("The exit criterion was not met.");
        concluded.FindingsPath.Should().Be("runs/x/findings.md");
        task.SpikeVerdict.Should().Be(SpikeVerdict.NotMet);
        task.SpikeVerdictReason.Should().Be("The exit criterion was not met.");
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private static TaskAggregate SpikeDraft(SpikeKind? spikeKind, string? exitCriterion)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Spike whether X is feasible", ["a working demo exists"],
            TaskType.Spike, agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, spikeKind: spikeKind, exitCriterion: exitCriterion));
        return task;
    }

    private static TaskAggregate PublishedSpike()
    {
        TaskAggregate task = SpikeDraft(SpikeKind.Research, "The findings answer the stated question.");
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        return task;
    }

    private static TaskAggregate ClaimedSpike(out Guid runId)
    {
        TaskAggregate task = PublishedSpike();
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        runId = DomainId.New();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, runId, Now));
        return task;
    }

    private static TaskAggregate PublishedTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "An ordinary feature", ["it works"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        return task;
    }

    private static TaskAggregate ClaimedTask()
    {
        TaskAggregate task = PublishedTask();
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        Guid runId = DomainId.New();
        task.Apply(TaskDecider.Claim(task, DomainId.New(), Owner, runId, Now));
        return task;
    }
}
