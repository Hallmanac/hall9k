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
/// This check could not succeed on Windows at all until PLAN.md §16 PLACEHOLDER-d4e64dfa. Both Windows launch
/// paths used to give h9kd its log through a cmd.exe <c>&gt;&gt;</c> redirect, which holds the
/// file with <c>FILE_SHARE_READ</c> for the whole run, so <see cref="DaemonLogRotation"/>'s
/// <c>FileAccess.ReadWrite</c> open was refused with a sharing violation on every tick — caught
/// and logged below, then refused again five minutes later, forever. Nothing was corrupted by
/// that (no truncation landed, so no line was lost and the log was not NUL-padded), but the
/// budget itself went unenforced. Both paths now hand the daemon an append handle their launcher
/// opened with a share mode that refuses nobody, so the tick below rotates, including on a node
/// that comes up solely through the logon autostart task and never touches the CLI's own start
/// path. A node whose autostart registration predates that change still carries the old launch
/// script, and only <c>h9k daemon autostart enable</c> rewrites it — see
/// <c>WindowsAppendOnlyLog</c>, which says so in the warning it logs at start.
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
