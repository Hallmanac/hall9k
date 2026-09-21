using FluentAssertions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Persistence;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What it takes for a decision or a lesson to actually reach another node (idea d805fd8b,
/// piece 1). Classifying the four event types project-scoped is only half of it: the receiving
/// node's inbox refuses to start a local stream from anything but that aggregate's own genesis,
/// so a genesis nobody named is held forever waiting on itself. Both halves are asserted here
/// because the first one alone looks complete and silently carries nothing.
/// </summary>
public sealed class DecisionAndLearningReplicationTests
{
    public static TheoryData<Type> EveryEventType() =>
    [
        typeof(DecisionRecorded),
        typeof(DecisionSuperseded),
        typeof(LearningRecorded),
        typeof(LearningRetired),
    ];

    [Theory]
    [MemberData(nameof(EveryEventType))]
    public void Every_decision_and_learning_event_travels_with_its_project(Type eventType)
    {
        EventScopeRegistry.ClassificationOf(eventType).Should().Be(EventScope.ProjectScoped);
    }

    [Fact]
    public void Each_streams_own_genesis_is_named_so_a_receiving_node_can_start_it()
    {
        AggregateGenesisEventTypes.IsGenesis(typeof(DecisionRecorded)).Should().BeTrue();
        AggregateGenesisEventTypes.IsGenesis(typeof(LearningRecorded)).Should().BeTrue();
    }

    /// <summary>
    /// The other side of the same rule: a tail event must NOT read as a genesis, or the inbox
    /// would start a local stream from it and leave a headless document behind.
    /// </summary>
    [Fact]
    public void A_terminal_event_is_never_a_genesis()
    {
        AggregateGenesisEventTypes.IsGenesis(typeof(DecisionSuperseded)).Should().BeFalse();
        AggregateGenesisEventTypes.IsGenesis(typeof(LearningRetired)).Should().BeFalse();
    }
}
