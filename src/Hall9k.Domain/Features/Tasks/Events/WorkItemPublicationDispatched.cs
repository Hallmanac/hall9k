using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The daemon committed to spawning the session that writes the card. It follows the
/// auxiliary-session pattern (Decisions Log #33): the resolved model is recorded as an observed
/// fact rather than re-derived later.
/// <para>
/// This is a milestone, never a promise: the session may create a card, create the wrong one,
/// or create none. Nothing here says a card exists — only <see cref="WorkItemLinked"/> does,
/// and only after the platform has read the card itself.
/// </para>
/// <para>
/// It is written <em>before</em> the process is spawned, which is why the process identity is on
/// <see cref="WorkItemPublicationSessionStarted"/> instead of here. The order is RunLauncher's
/// (RunDispatched, then spawn, then RunProcessStarted) and it is the order that makes "one
/// session per request" survive a crash: the marker that stops a second dispatch is on the stream
/// before anything exists that could create a card. Spawning first would leave a window — a lost
/// commit, a kill -9 — where a live session is writing a card and the stream says nothing was
/// ever dispatched, so the next sweep starts a second one. Origin incident (2026-08-21): the
/// pre-PR review of this branch traced both paths to two cards for one task.
/// </para>
/// <para>
/// SessionId also names where the session's artifacts are: ~/.hall9k/runs/&lt;session-id&gt;/,
/// beside the runs, holding the prompt it was given and the stream it wrote. A publication is not
/// a run — it has no worktree, no branch, and no lease — so it has no run id to be filed under,
/// and the session's own id is the honest key.
/// </para>
/// <para>
/// NodeId is which machine spawned it, and it is what makes the pid on the follow-on event
/// readable by anybody else. A process identity is only meaningful on the node it belongs to, so
/// adoption asks the question a node can answer — "is the session I started still running?" —
/// rather than judging another machine's pid, which is the same rule run adoption follows
/// (RunDetails.NodeId).
/// </para>
/// </summary>
public sealed record WorkItemPublicationDispatched(
    Guid Id,
    Guid SessionId,
    Guid NodeId,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null);
