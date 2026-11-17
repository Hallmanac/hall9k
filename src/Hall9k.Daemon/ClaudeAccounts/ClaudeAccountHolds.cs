using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Marten;

namespace Hall9k.Daemon.ClaudeAccounts;

/// <summary>
/// Turns a refused spawn for a run into a hold rather than a failure: <see cref="RunAccountHeld"/> clears the
/// leg that was about to start the way a launch hold does, and <see cref="ClaudeAccountMonitor"/> resumes the
/// run through the same re-entry paths once the account's check passes.
/// </summary>
public static class ClaudeAccountHolds
{
    /// <summary>
    /// Holds <paramref name="runId"/> on <paramref name="refusal"/>; a run already terminal is left alone.
    /// <paramref name="primarySession"/> is true when the refused spawn was the run's primary session, so the
    /// resume starts that session again (<see cref="RunAccountHeld.PrimarySession"/>).
    /// </summary>
    public static async Task HoldRunAsync(
        IDocumentStore store, Guid runId, ClaudeAccountRefusedException refusal, bool primarySession,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (run is null || run.State.IsTerminal)
        {
            return;
        }

        session.Events.Append(runId, new RunAccountHeld(
            runId, refusal.ConnectionId, refusal.Message, DateTimeOffset.UtcNow, primarySession));
        await session.SaveChangesAsync(cancellationToken);
    }
}
