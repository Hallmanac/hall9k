namespace Hall9k.Daemon.Execution;

/// <summary>
/// When a sweep first found each of another node's dispatches past the foreign-session ceiling, so it ends one only on a
/// later sweep at least <see cref="DaemonOptions.ForeignSessionSettlePeriod"/> on. A node that wakes from suspension sees
/// every dispatch made before it slept as overdue before its inbox has delivered their outcomes, and the wait gives the
/// message sweep that long to deliver them. In memory and per engine: a restart starts the count over, the safe direction.
/// Not thread-safe; each engine's sweep is serial.
/// </summary>
public sealed class ForeignDispatchSettle(TimeSpan settlePeriod)
{
    private readonly Dictionary<Guid, DateTimeOffset> _firstOverdue = [];

    /// <summary>
    /// Whether the overdue dispatch of <paramref name="sessionId"/> has been seen overdue for the whole settle period,
    /// marking it first seen at <paramref name="now"/> when it was not seen before.
    /// </summary>
    public bool HasSettled(Guid sessionId, DateTimeOffset now)
    {
        if (!_firstOverdue.TryGetValue(sessionId, out DateTimeOffset first))
        {
            _firstOverdue[sessionId] = now;
            return false;
        }

        return now - first >= settlePeriod;
    }

    /// <summary>Forgets every session not in <paramref name="standing"/>: its dispatch ended, so its count is over.</summary>
    public void KeepOnly(IReadOnlySet<Guid> standing)
    {
        foreach (Guid sessionId in _firstOverdue.Keys)
        {
            if (!standing.Contains(sessionId))
            {
                _firstOverdue.Remove(sessionId);
            }
        }
    }

    /// <summary>Forgets <paramref name="sessionId"/> once this node has ended its dispatch.</summary>
    public void Forget(Guid sessionId) => _firstOverdue.Remove(sessionId);
}
