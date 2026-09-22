using FluentAssertions;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="AggregateGenesisEventTypes"/> is the one place the inbox decides whether a stream
/// this node has never started may legally start from a candidate event at all. Task: a run
/// stream whose first event is a reconstruction rather than a dispatch — <see cref="RunRecordReconstructed"/>
/// is the Run aggregate's own second genesis, alongside <see cref="RunDispatched"/>.
/// </summary>
public sealed class AggregateGenesisEventTypesTests
{
    [Theory]
    [InlineData(typeof(TaskAdded))]
    [InlineData(typeof(IdeaCaptured))]
    [InlineData(typeof(EpicAdded))]
    [InlineData(typeof(RunDispatched))]
    [InlineData(typeof(RunRecordReconstructed))]
    public void Names_every_genesis_event_type(Type eventType) =>
        AggregateGenesisEventTypes.IsGenesis(eventType).Should().BeTrue();

    [Fact]
    public void An_ordinary_run_event_is_not_a_genesis() =>
        AggregateGenesisEventTypes.IsGenesis(typeof(RunCompleted)).Should().BeFalse();
}
