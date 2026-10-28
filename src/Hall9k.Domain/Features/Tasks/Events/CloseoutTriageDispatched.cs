namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Closeout dispatched the one read-only triage session for a set of unresolved review threads on a
/// pull request it leaves alone while the task's interactive flag is on, before the session was
/// spawned, so a poll that observes the same threads again, or a daemon that restarts mid-triage,
/// finds the dispatch already on the stream and starts nothing. Counted against the task's lifetime
/// triage allowance (<see cref="TaskAggregate.CloseoutTriagesDispatched"/>).
/// </summary>
/// <param name="ThreadSetKey">The canonical key of the thread ids this triage covers, one per distinct set.</param>
/// <param name="SessionId">The triage session's id, which ties its outcome to this dispatch.</param>
/// <param name="ReportPath">The one file in the run directory the session is permitted to write.</param>
/// <param name="ThreadCount">How many threads the set holds.</param>
public sealed record CloseoutTriageDispatched(
    Guid Id,
    string ThreadSetKey,
    Guid SessionId,
    string ReportPath,
    int ThreadCount,
    DateTimeOffset DispatchedAt);
