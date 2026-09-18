namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The ledger holder lock (idea 202383dc, A3b) was cleared for this task — true completion,
/// <c>h9k task abandon</c>, or a sweep that found the holding node's own run gone. Never
/// <c>h9k task release</c>: that command remains scoped to this node's own interactive claim, which
/// never writes a ledger holder in the first place (the sentinel <c>Guid.Empty</c> node id
/// <see cref="TaskAggregate.IsInteractiveClaim"/> reads back — deliberately excluded, since the
/// ledger never carries a holder write for one at all). Carries no node id: unlike <see cref="TaskClaimed"/>, which names who is taking
/// responsibility, this only ever means "nobody is" — <see cref="TaskAggregate.Apply(TaskHolderReleased)"/>
/// clears whatever <see cref="TaskAggregate.HolderNodeId"/> already held rather than confirming it
/// against one named here, since every caller only ever appends this after deciding, from its own
/// read, that this task's holder is (or was) itself.
/// </summary>
public sealed record TaskHolderReleased(Guid Id, DateTimeOffset ReleasedAt);
