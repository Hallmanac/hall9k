namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One agent session a run has in flight, as the stream last recorded it: what shape of work it
/// is, which review track it belongs to when it is one, and the identity a reader needs to ask
/// the operating system whether it is still there.
/// <para>
/// A run records a <em>list</em> of these rather than one, because a review cycle dispatches one
/// pass per active track and they read the same worktree at the same time (Decisions Log #59).
/// A single slot would hold whichever pass was dispatched last, so the first pass's process
/// would be invisible and the last pass's exit would read as the whole cycle dying — a healthy
/// run reported as a dead one, which is the incident this recording exists to prevent pointing
/// the other way.
/// </para>
/// </summary>
/// <param name="Role">Build while the agent writes the code, Review while a pass reads it, Fix while findings are applied, Synthesis while blocker context is condensed.</param>
/// <param name="Lens">The review track this session belongs to; <see cref="ReviewLens.Unknown"/> for every session that is not a review pass, and for a pass dispatched before lenses existed.</param>
/// <param name="ProcessId">The session's process id, on the node that spawned it.</param>
/// <param name="StartedAt">
/// The process start time, the other half of the identity (the PID-reuse guard, log #2). Null on
/// a resumed build session, whose event records only a pid (<see cref="Events.RunResumed"/>) — a
/// bare pid is a lie waiting to happen, so liveness stays unobserved rather than guessed.
/// </param>
/// <param name="MachineName">
/// Which machine's process table <paramref name="ProcessId"/> names, carried only for
/// <see cref="AgentRole.Interactive"/> (from <see cref="Events.InteractiveSessionStarted"/>) —
/// every other role dispatches through the daemon, whose own <c>NodeId</c> already answers this.
/// Blank when unknown, which is never treated as "this machine" (adversarial review, cycle 2).
/// </param>
public sealed record ActiveSession(
    AgentRole Role,
    ReviewLens Lens,
    int ProcessId,
    DateTimeOffset? StartedAt,
    string MachineName = "");
