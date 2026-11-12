namespace Hall9k.Daemon.Execution;

/// <summary>
/// When a sweep first found each of another node's dispatches past the foreign-session ceiling, so it ends one only on a
/// later sweep at least <see cref="DaemonOptions.ForeignSessionSettlePeriod"/> on. A node that wakes from suspension sees
/// every dispatch made before it slept as overdue before its inbox has delivered their outcomes, and the wait gives the
/// message sweep that long to deliver them. The period is measured on <see cref="TimeProvider.GetTimestamp"/>, not the wall
/// clock, so time the node spends suspended after a sweep noted a dispatch does not count toward it: on macOS and Linux
/// that timestamp stops while the machine sleeps (whether it does on Windows was not observed). In memory and per engine:
/// a restart starts the count over, the safe direction. Not thread-safe; each engine's sweep is serial.
/// </summary>
public sealed class ForeignDispatchSettle(TimeSpan settlePeriod, TimeProvider clock)
{
    private readonly Dictionary<Guid, long> _firstOverdue = [];

    /// <summary>
    /// Whether the overdue dispatch of <paramref name="sessionId"/> has been seen overdue for the whole settle period,
    /// marking it first seen now when it was not seen before.
    /// </summary>
    public bool HasSettled(Guid sessionId)
    {
        if (!_firstOverdue.TryGetValue(sessionId, out long first))
        {
            _firstOverdue[sessionId] = clock.GetTimestamp();
            return false;
        }

        return clock.GetElapsedTime(first) >= settlePeriod;
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
