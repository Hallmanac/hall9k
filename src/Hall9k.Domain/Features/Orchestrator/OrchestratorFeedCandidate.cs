namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// One raw event handed to <see cref="OrchestratorFeedSelection"/>, reduced to the five things
/// the selection and its caller's scope lookup actually read. Kept separate from Marten's own
/// <c>IEvent</c> so the selection — the whole of what a drain decides — stays a pure function a
/// unit test can drive without a database.
/// </summary>
/// <param name="EventType">
/// The event's own recorded type, read before <see cref="Data"/> is ever touched: an event the
/// interest table has no opinion about costs one dictionary lookup and nothing else.
/// </param>
/// <param name="StreamId">
/// The stream this event was appended to — what a caller's own scope lookup keys on, and what
/// lets it cache one answer per stream rather than one per event.
/// </param>
/// <param name="IsReplicated">
/// Whether this node received this event by replication (it carries
/// <c>Hall9k.Domain.Features.Replication.ReplicationEventHeaders.OriginEventId</c>) rather than
/// producing it itself. Read by <see cref="OrchestratorFeedInterest"/>'s own origin-filtered
/// entries — <c>RunRecordReconstructed</c> is Actionable on the node that actually rebuilt the
/// run, but a peer's own copy of that same fact rebuilt nothing and must not page that peer's
/// window, especially when a whole backlog of them lands at once.
/// </param>
public sealed record OrchestratorFeedCandidate(
    long Sequence,
    DateTimeOffset At,
    Type EventType,
    object Data,
    Guid StreamId,
    bool IsReplicated);
