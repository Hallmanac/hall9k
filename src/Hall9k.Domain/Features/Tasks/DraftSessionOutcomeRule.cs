namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Whether a challenge or refinement outcome applies to a draft, one rule for both kinds so <see cref="TaskAggregate"/> and
/// <see cref="Projections.TaskDetails"/> answer it alike. Another node can end a dispatch it cannot see finish
/// (a <c>DispatchExpired</c> failure past the foreign-session ceiling) while the dispatching node records the session's own
/// outcome, and the two reach every node in either order. The dispatching node's outcome wins, and an expiry that lands after
/// the session already ended, or after a later session was dispatched, changes nothing. An outcome that arrives after such
/// an expiry ended its session applies its outcome and revision facts but leaves a challenge request filed since alone: that
/// request is for a later session, not the one the outcome names.
/// </summary>
public static class DraftSessionOutcomeRule
{
    /// <summary>
    /// Whether an outcome for <paramref name="recordedSession"/> applies, given the session running now and the session of
    /// the latest outcome on record. An outcome with no session (refused before a spawn) always applies.
    /// </summary>
    /// <param name="recordedSession">The session the outcome names, or null when none was spawned.</param>
    /// <param name="recordedIsExpiry">Whether the outcome is another node's <c>DispatchExpired</c> failure.</param>
    /// <param name="runningSession">The session the draft holds as running, or null.</param>
    /// <param name="latestSession">The session of the latest outcome on record, or null.</param>
    /// <param name="latestIsExpiry">Whether the latest outcome on record is a <c>DispatchExpired</c> failure.</param>
    public static bool Applies(
        Guid? recordedSession, bool recordedIsExpiry, Guid? runningSession, Guid? latestSession, bool latestIsExpiry) =>
        (recordedSession, runningSession) switch
        {
            (null, _) => true,
            ({ } recorded, { } running) => recorded == running,
            ({ } recorded, null) => !recordedIsExpiry && latestIsExpiry && latestSession == recorded,
        };

    /// <summary>
    /// The session whose own outcome the draft still waits for: the running one, or, when another node's
    /// <c>DispatchExpired</c> ended it, the expired session, whose outcome still applies when it arrives. A person's revision
    /// made while this session is awaited counts as made while it ran, so the expiry leaves that outcome's revision facts
    /// exactly as they would be without it.
    /// </summary>
    /// <param name="runningSession">The session the draft holds as running, or null.</param>
    /// <param name="latestSession">The session of the latest outcome on record, or null.</param>
    /// <param name="latestIsExpiry">Whether the latest outcome on record is a <c>DispatchExpired</c> failure.</param>
    public static Guid? AwaitedSession(Guid? runningSession, Guid? latestSession, bool latestIsExpiry) =>
        runningSession ?? (latestIsExpiry ? latestSession : null);
}
