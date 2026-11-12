namespace Hall9k.Daemon.Challenge;

/// <summary>
/// The challenge sessions this node's <see cref="DraftChallengeEngine"/> is running right now, shared with the
/// <see cref="Plan.PlanEngine"/> so a plan can tell another node's ceiling expiry for a session still running here from
/// a session that really ended. A singleton read from both engines' sweeps, so every access takes the lock.
/// </summary>
public sealed class RunningChallengeSessions
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _sessions = [];

    /// <summary>Marks a session running; the engine calls it once the dispatch is saved.</summary>
    public void Add(Guid sessionId)
    {
        lock (_gate)
        {
            _sessions.Add(sessionId);
        }
    }

    /// <summary>Marks a session ended; the engine calls it only after the session's outcome is recorded.</summary>
    public void Remove(Guid sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }

    /// <summary>Whether the session is running on this node.</summary>
    public bool Contains(Guid sessionId)
    {
        lock (_gate)
        {
            return _sessions.Contains(sessionId);
        }
    }

    /// <summary>A copy of the running set, for a reader that must take it before it reads the outcomes it is checked against.</summary>
    public IReadOnlySet<Guid> Snapshot()
    {
        lock (_gate)
        {
            return _sessions.ToHashSet();
        }
    }
}
