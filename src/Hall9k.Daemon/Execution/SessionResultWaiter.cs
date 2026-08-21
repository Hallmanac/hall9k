using System.Text;
using Hall9k.Daemon.ProcessManagement;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Waits for a spawned session's terminal result event by tailing its stream file: the
/// completion signal is the stream's final result line, never the exit code (Decisions Log
/// #2). Shared by every caller that spawns a session and then blocks on it — the pre-PR
/// review loop's legs (log #24) and the context-synthesis pass (log #36) — so the grace
/// window after process death, which exists so buffered output still gets read, behaves
/// identically wherever a session is awaited.
/// </summary>
public static class SessionResultWaiter
{
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeadProcessGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The session's result, or null when it genuinely died without one.
    /// <paramref name="onOutput"/> is invoked whenever new output lands, which is how a
    /// caller keeps the run's last-activity fresh so stall detection covers the leg.
    /// </summary>
    public static async Task<AgentResult?> WaitAsync(
        string streamFile,
        int processId,
        DateTimeOffset processStartedAt,
        IProcessManager processManager,
        Func<CancellationToken, Task>? onOutput,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? deadSince = null;
        long cursor = 0;
        StringBuilder partialLine = new();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (long newCursor, bool sawResult, AgentResult? result) =
                await StreamTailReader.ReadNewLinesAsync(streamFile, cursor, partialLine, cancellationToken);
            if (sawResult)
            {
                return result;
            }

            if (newCursor > cursor)
            {
                cursor = newCursor;
                if (onOutput is { } notify)
                {
                    await notify(cancellationToken);
                }
            }

            if (!processManager.IsAlive(processId, processStartedAt))
            {
                // The grace window keeps polling above, so buffered output that lands
                // after death still gets read before this gives up.
                deadSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                {
                    return null;
                }
            }
            else
            {
                deadSince = null;
            }

            await Task.Delay(TailInterval, cancellationToken);
        }
    }
}
