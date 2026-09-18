namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The holding node left a note for whoever holds this task next (idea 202383dc, item 3): what is
/// done, what is half done, what to watch — for work still in flight, unlike
/// <see cref="Hall9k.Domain.Features.Run.Events.RunHandoffRecorded"/>, which is the closeout handoff
/// to a dependent task, appended only once a run truly finishes (Decisions Log #36). This is the
/// note's own source of truth: <see cref="TaskAggregate.HandoffNote"/> mirrors only the latest one,
/// and the ledger record's own field is composed from that mirror the same way every other
/// record field is (idea 202383dc, A3a) — never attached to the record by hand.
/// <see cref="Note"/> is bounded to <c>Handlers.TaskDecider.MaxHandoffNoteLength</c> by the decider
/// that builds this event, the same cap other event-carried free text (a run's own closeout
/// handoff) already holds itself to.
/// </summary>
public sealed record TaskHandoffNoted(
    Guid Id, string Note, Guid AuthorNodeId, string AuthorOwnerRootFingerprint, DateTimeOffset NotedAt);
