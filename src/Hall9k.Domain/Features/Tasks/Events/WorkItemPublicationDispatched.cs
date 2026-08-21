using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The daemon spawned the session that writes the card. It follows the auxiliary-session
/// patterns (Decisions Log #33/#36): the resolved model is recorded as an observed fact, and
/// the process is identified by pid and start time so a restarted daemon can tell a live
/// session from a dead one's reused pid.
/// <para>
/// This is a milestone, never a promise: the session may create a card, create the wrong one,
/// or create none. Nothing here says a card exists — only <see cref="WorkItemLinked"/> does,
/// and only after the platform has read the card itself.
/// </para>
/// <para>
/// SessionId also names where the session's artifacts are: ~/.hall9k/runs/&lt;session-id&gt;/,
/// beside the runs, holding the prompt it was given and the stream it wrote. A publication is not
/// a run — it has no worktree, no branch, and no lease — so it has no run id to be filed under,
/// and the session's own id is the honest key.
/// </para>
/// <para>
/// NodeId is which machine spawned it, and it is what makes the pid above readable by anybody
/// else. A process identity is only meaningful on the node it belongs to, so adoption asks the
/// question a node can answer — "is the session I started still running?" — rather than judging
/// another machine's pid, which is the same rule run adoption follows (RunDetails.NodeId).
/// </para>
/// </summary>
public sealed record WorkItemPublicationDispatched(
    Guid Id,
    Guid SessionId,
    Guid NodeId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);
