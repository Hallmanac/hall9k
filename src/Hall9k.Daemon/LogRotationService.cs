using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon;

/// <summary>
/// Enforces the log's size budget from inside the process that writes it. The CLI rolls
/// an oversized log at start, but a daemon started once and left running for weeks never
/// reaches another start path — the budget has to be checked while it runs or it is not
/// a budget at all. Rotation is a copy-then-truncate (see
/// <see cref="DaemonLogRotation"/>): this process holds the log's descriptor open for
/// its whole lifetime, so a rename would silently redirect every subsequent line into
/// the rolled-aside generation.
/// <para>
/// On Windows this check cannot currently succeed, and the claim above that the budget is
/// checked while the daemon runs holds on Unix only. Both Windows launch paths give h9kd its
/// log through a cmd.exe <c>&gt;&gt;</c> redirect, which holds the file with
/// <c>FILE_SHARE_READ</c> for the whole run, so <see cref="DaemonLogRotation"/>'s
/// <c>FileAccess.ReadWrite</c> open is refused with a sharing violation on every tick — caught
/// and logged below, then retried five minutes later. Nothing is corrupted by that (no
/// truncation lands, so no line is lost and the log is not NUL-padded), but an oversized log
/// on Windows stays oversized until the CLI's own start path next rolls it aside with nothing
/// holding it (<c>h9k daemon start</c>, or an <c>h9k install</c> or <c>h9k update</c> that
/// restarts the daemon) — and that path is the only Windows one that rotates, so a node that
/// comes up solely through the logon autostart task (<c>wscript.exe</c> straight to cmd.exe,
/// never through the CLI) never enforces the budget at all rather than merely deferring it. The
/// fix is a launcher-supplied append handle in place of the redirect: see
/// <c>WindowsAppendOnlyLog</c> and PLAN.md §16 #PLACEHOLDER-3abf032d.
/// </para>
/// </summary>
public sealed class LogRotationService(ILogger<LogRotationService> logger) : BackgroundService
{
    /// <summary>
    /// How often the budget is checked. Frequent enough that even a daemon logging
    /// hard stays within a couple of megabytes of the threshold, cheap enough to be
    /// nothing: the check is one stat call whenever the log is under budget.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(CheckInterval);
        do
        {
            Rotate();
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    private void Rotate()
    {
        try
        {
            if (DaemonLogRotation.RotateIfOversized(DaemonRuntime.LogFile))
            {
                // Logged after the truncation on purpose, so the line lands at the top
                // of the fresh log rather than at the bottom of the rolled-aside one.
                logger.LogInformation(
                    "Log passed its {Budget} MB budget — copied aside to {Previous} and truncated in place",
                    DaemonLogRotation.ThresholdBytes / (1024 * 1024),
                    DaemonLogRotation.PreviousLogFile(DaemonRuntime.LogFile));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be rolled is not worth taking the daemon down for; the
            // next tick tries again.
            logger.LogWarning(exception, "Log rotation failed; will retry next tick");
        }
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
