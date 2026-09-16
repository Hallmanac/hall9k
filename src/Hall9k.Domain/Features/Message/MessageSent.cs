namespace Hall9k.Domain.Features.Message;

/// <summary>This node sent an envelope through the outbox transport (A1), landing signed at
/// <c>messages/&lt;seq&gt;.json</c> in its own outbox ref. <see cref="ProjectId"/> is this
/// install's own local project id — for a message queued before idea 202383dc's M2 shipped (its own
/// <see cref="MessageQueued.ProjectId"/> is <see cref="Guid.Empty"/>), this is the FIRST place that
/// sentinel is ever resolved into a real project: <c>MessageOutbox.FlushAsync</c> stamps it here, to
/// the project the old, single-project sweep would have picked, the one time it finally flushes.
/// </summary>
public sealed record MessageSent(Guid FromNodeId, long Seq, DateTimeOffset At, Guid ProjectId);
