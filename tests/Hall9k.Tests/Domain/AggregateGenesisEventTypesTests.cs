using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Persistence;
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

    /// <summary>
    /// Families with no genesis named above on purpose. <see cref="ReplicationStreamFamily.Unknown"/>
    /// is the "no family resolved" sentinel, and the Project family's own genesis,
    /// <c>ProjectRegistered</c>, mints a per-install project id and never travels
    /// (<c>ProjectStreamReplicationRules.IsProjectIdentityEvent</c>), so no receiver ever starts a
    /// Project stream from a replicated event.
    /// </summary>
    private static readonly ReplicationStreamFamily[] FamiliesWithoutAReplicatedGenesis =
        [ReplicationStreamFamily.Unknown, ReplicationStreamFamily.Project];

    /// <summary>
    /// A family a stream can be resolved to, whose events travel, but which has no genesis named in
    /// <see cref="AggregateGenesisEventTypes"/> is a family every receiver holds forever: the first
    /// event of its stream is never one the inbox will start a stream from.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void Every_replication_stream_family_names_a_genesis_or_is_explicitly_exempt(ReplicationStreamFamily family)
    {
        if (FamiliesWithoutAReplicatedGenesis.Contains(family))
        {
            return;
        }

        EventScopeRegistry.KnownEventTypes
            .Where(AggregateGenesisEventTypes.IsGenesis)
            .Select(EventReplicationInbox.FamilyOf)
            .Should().Contain(
                family,
                $"a {family} stream needs a stream-starting event in AggregateGenesisEventTypes.IsGenesis, "
                + "or an entry in FamiliesWithoutAReplicatedGenesis saying why it has none");
    }

    public static TheoryData<ReplicationStreamFamily> Families() =>
        [.. Enum.GetValues<ReplicationStreamFamily>()];
}
