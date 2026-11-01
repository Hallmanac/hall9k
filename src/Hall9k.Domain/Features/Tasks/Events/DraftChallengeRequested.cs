namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Someone ran <c>h9k task challenge</c> on a draft: the daemon on this node is asked to start one headless
/// session that tests the draft's claims and reports a verdict.
/// <para>
/// Node-scoped, like <see cref="WorkItemPublicationSessionStarted"/>, and it names no node: it never leaves the
/// node it was appended on, so the daemon of no other node can see it, and one that somehow arrived by
/// replication would start nothing. That is what makes "the session runs on the node where the verb ran" a
/// fact of the registry rather than a rule a sweep has to keep. The dispatch that follows is project-scoped
/// and names the node (<see cref="DraftChallengeDispatched"/>).
/// </para>
/// </summary>
/// <param name="Id">The task's id (the stream key).</param>
/// <param name="RequestedAt">When the verb was run.</param>
/// <param name="RequestedByOwnerId">Who ran it.</param>
public sealed record DraftChallengeRequested(Guid Id, DateTimeOffset RequestedAt, Guid RequestedByOwnerId);
