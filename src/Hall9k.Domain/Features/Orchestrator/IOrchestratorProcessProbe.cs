namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// One look at this machine's own process table, seamed so the register refusal, the daemon's
/// presence sweep, and <c>h9k orchestrator status</c> all ask the same question the same way —
/// and so a unit test can answer it without a real process (idea 89471598, piece 1).
/// <para>
/// Liveness is by process id alone, deliberately: it is the one identity every vendor's agent
/// CLI has, where a session registry is Claude Code's own and may not exist at all for the next
/// runtime an operator launches a window from.
/// </para>
/// </summary>
public interface IOrchestratorProcessProbe
{
    /// <summary>
    /// <see langword="null"/> when no process with this id is on the table — the only answer that
    /// means "gone". A sighting means the id is in use right now; see
    /// <see cref="OrchestratorProcessSighting.StartedAt"/> for the pid-reuse half of the question.
    /// </summary>
    OrchestratorProcessSighting? Probe(int processId);
}
