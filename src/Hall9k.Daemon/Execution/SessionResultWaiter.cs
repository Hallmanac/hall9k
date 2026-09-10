using System.Text;
using Hall9k.Daemon.ProcessManagement;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Waits for a spawned session's terminal result event by tailing its stream file. A result
/// line alone does not finalize the wait: a stream can hold more than one, so completion also
/// requires <paramref name="processManager"/> to report the process gone before the whole
/// file is re-read for the session's real, combined result (discovery cc9b7aec) — the exit
/// code itself is still never consulted (Decisions Log #2), only whether the process has
/// exited. Shared by every caller that spawns a session and then blocks on it — the pre-PR
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
        bool sawAnyResult = false;
        StringBuilder partialLine = new();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (long newCursor, bool sawResult) =
                await StreamTailReader.ReadNewLinesAsync(streamFile, cursor, partialLine, cancellationToken);

            if (newCursor > cursor)
            {
                cursor = newCursor;
                if (onOutput is { } notify)
                {
                    await notify(cancellationToken);
                }
            }

            // The line this poll found only signals a leg is done — a stream can hold more than
            // one result line, and in the common shape the process keeps running for seconds to
            // minutes after the first one before the leg's real terminal line lands. Acting on
            // the first line the instant it appears would read that still-running leg as
            // finished, so this only remembers a result was seen and keeps tailing; the
            // process's own death — checked below, on every poll from here on — is what confirms
            // no further result is coming (independent pre-PR review, cycle 3, adversarial lens).
            sawAnyResult |= sawResult;

            if (processManager.IsAlive(processId, processStartedAt))
            {
                deadSince = null;
            }
            else if (sawAnyResult)
            {
                // Only a re-read of the whole file finds the line that accounts for the whole
                // leg (StreamTailReader.ReadFinalResultAsync's own doc comment; discovery
                // cc9b7aec) — safe to do now that the process dying confirms this is the last
                // one there will be.
                return await StreamTailReader.ReadFinalResultAsync(streamFile, cancellationToken);
            }
            else
            {
                // The grace window keeps polling above, so buffered output that lands
                // after death still gets read before this gives up.
                deadSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                {
                    return null;
                }
            }

            await Task.Delay(TailInterval, cancellationToken);
        }
    }
}
