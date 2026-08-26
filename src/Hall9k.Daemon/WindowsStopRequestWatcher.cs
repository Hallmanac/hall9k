using System.Diagnostics;
using System.Text.Json;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;

namespace Hall9k.Daemon;

/// <summary>
/// Windows has no SIGTERM a CLI can send to an arbitrary process, so <c>h9k daemon stop</c>
/// asks gracefully by writing <see cref="DaemonRuntime.StopRequestFile"/> instead, and this
/// is what watches for it — the same doorbell-plus-poll-backstop idiom every other daemon
/// loop here already uses (<c>DispatchLoop</c>, <c>CardPublicationLoop</c>), just with the
/// filesystem standing in for the doorbell since there is no domain event to LISTEN for
/// here. Registered only on Windows (see <c>Program.cs</c>); the request file is never
/// written anywhere on Unix, which keeps using a real SIGTERM.
/// </summary>
public sealed class WindowsStopRequestWatcher(
    IHostApplicationLifetime lifetime, ILogger<WindowsStopRequestWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Start times drift slightly between recording and reading; within this window is
    // "same process", beyond it the pid was reused — mirrors DaemonProcess.IsAlive's own
    // tolerance in Hall9k.Cli (not referenced from here: Daemon does not depend on Cli).
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    // Same JsonSerializerOptions shape DaemonPidFile writes with, so the descriptor this
    // reads back round-trips regardless of property casing.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly DateTimeOffset startedAt = CurrentProcessStartedAt();

    // The path a request is claimed onto before it is inspected — see IsOwnStopRequest's
    // own doc for why claiming (an atomic rename) replaces a plain read-then-delete. A
    // property, not a static readonly field: DaemonRuntime.StopRequestFile resolves against
    // HALL9K_HOME on every access rather than once, and tests redirect that per run.
    private static string ClaimPath => DaemonRuntime.StopRequestFile + ".claimed";

    // The stale content most recently warned about — a claim that keeps recurring for the
    // same content (a write source that keeps recreating an already-stale request) would
    // otherwise re-warn about it on every 250ms tick for the daemon's whole life, flooding
    // h9kd.log until LogRotationService rotates away the run history a human actually reads
    // that log for. Warning once per distinct stale content keeps the honest report without
    // the flood; a genuinely new stale request (different content) still gets its own
    // warning.
    private string? lastWarnedStaleContent;

    // Set once DeleteClaimFile has warned about a claimed copy it could not remove even after
    // clearing the read-only attribute — an ACL denial rather than the read-only case that
    // retry already recovers from. Without this, the same permanent failure would re-log on
    // every 250ms tick for the rest of the daemon's life, the same flood lastWarnedStaleContent
    // exists to prevent for stale content.
    private bool warnedAboutStuckClaimFile;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PollInterval);
        do
        {
            if (!IsOwnStopRequest())
            {
                continue;
            }

            // The claim already removed the request file from disk (see IsOwnStopRequest),
            // so nothing is left to delete here before signaling shutdown.
            logger.LogInformation(
                "Graceful stop requested via {StopRequestFile} — shutting down", DaemonRuntime.StopRequestFile);
            lifetime.StopApplication();
            return;
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    /// <summary>
    /// True only when a request naming THIS process's identity was just claimed — pid AND
    /// start time, never a bare pid (Decisions Log #2: the same discipline
    /// <see cref="DaemonRuntime.PidFile"/> already carries). h9k daemon stop and
    /// WindowsDaemonAutostart.DisableAsync both write the identity of the daemon they mean
    /// to stop, so a mismatch means the file is stale — left behind by a reboot mid-wait, a
    /// force-kill from Task Manager, or a claim that raced a write before this fix existed —
    /// and belongs to a daemon that is already gone, never to this one.
    /// <para>
    /// Claiming is <see cref="File.Move(string, string, bool)"/>ing the request file onto
    /// <see cref="ClaimPath"/> before reading it, rather than reading it in place and
    /// deleting it by path afterward: a rename is a single atomic filesystem operation, so
    /// whatever this reads is always a complete file exactly as some writer left it, and the
    /// original path is vacated in that same step — there is no window between "read" and
    /// "delete" for a fresh write from <c>h9k daemon stop</c> to land in and then be deleted
    /// unseen (cycle-7 pre-PR review finding: the previous read-then-delete-by-path could
    /// either throw a sharing violation into <c>h9k daemon stop</c> or silently discard the
    /// very request it just wrote). <see cref="DaemonPidFile.WriteAsync"/> writes the
    /// request the same atomic way (stage-then-rename via <c>AtomicFileWrite</c>), so the
    /// file this ever observes is always either the previous complete request or the next
    /// one, never a partial one.
    /// </para>
    /// Internal for direct unit coverage of the stale-content warning de-duplication below,
    /// the same way <c>WindowsDaemonAutostart</c>'s own pure-logic methods are exposed for
    /// direct testing rather than requiring a live poll loop.
    /// </summary>
    internal bool IsOwnStopRequest()
    {
        // The move and the read are no longer one try/finally: a read failure after a
        // successful claim (an indexer or scanner opening the just-renamed file with a
        // deny-share mode is enough on Windows) must leave the claimed copy on disk for
        // the next tick to retry, rather than deleting it unconditionally and discarding
        // the only surviving copy of the request. Skipping the move when a claimed copy
        // already sits at ClaimPath is what lets that retry find it — the original path
        // is empty by then, so a fresh move would just fail with "no file yet".
        if (!File.Exists(ClaimPath))
        {
            // Checked before the move rather than relying on the catch below for the "no
            // file yet" case: that is every tick of the daemon's entire lifetime bar the
            // rare one where a stop was actually requested, and throwing (then catching) a
            // FileNotFoundException four times a second for as long as h9kd runs floods
            // first-chance-exception tooling with nothing to show for it. The move stays
            // the only thing that ever reads the file, so the atomic-claim property this
            // method's own doc comment describes is unaffected — a fresh write landing
            // between this check and the move below still gets claimed by it.
            if (!File.Exists(DaemonRuntime.StopRequestFile))
            {
                return false;
            }

            try
            {
                File.Move(DaemonRuntime.StopRequestFile, ClaimPath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // IOException covers both "no file yet" (a write landing between the check
                // above and this move — the check narrows the common case, not the race)
                // and a rename racing a write still landing at the source path;
                // UnauthorizedAccessException covers a path that exists but is unreadable
                // (a directory in its place, a denying ACL). Either way this
                // BackgroundService never lets an escaping exception take the whole host
                // down via the default StopHost behavior — nothing to act on yet; the next
                // tick tries again.
                return false;
            }
        }

        string content;
        try
        {
            content = File.ReadAllText(ClaimPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The claim already succeeded, so ClaimPath is left in place on purpose — see
            // this method's own remarks above.
            return false;
        }

        DeleteClaimFile();

        DaemonProcessDescriptor? requested;
        try
        {
            requested = JsonSerializer.Deserialize<DaemonProcessDescriptor>(content, SerializerOptions);
        }
        catch (JsonException)
        {
            requested = null;
        }

        if (requested is not null
            && requested.ProcessId == Environment.ProcessId
            && (requested.StartedAt - startedAt).Duration() <= StartTimeTolerance)
        {
            return true;
        }

        bool isNewStaleContent = !string.Equals(content, lastWarnedStaleContent, StringComparison.Ordinal);
        if (isNewStaleContent)
        {
            logger.LogWarning(
                "Ignoring stale {StopRequestFile} ({Content}) — not this daemon (pid {ProcessId}, started {StartedAt:u})",
                DaemonRuntime.StopRequestFile, content.Trim(), Environment.ProcessId, startedAt);
            lastWarnedStaleContent = content;
        }

        return false;
    }

    /// <summary>
    /// Cleanup of the already-claimed copy — the request is off
    /// <see cref="DaemonRuntime.StopRequestFile"/> the moment the claiming move succeeds
    /// (see <see cref="IsOwnStopRequest"/>), so nothing else ever reads <see cref="ClaimPath"/>.
    /// A transient failure to delete it (a lock, an indexer holding it open) is harmless
    /// litter that the next tick's retry clears. A read-only <see cref="ClaimPath"/> is not
    /// transient, though: <see cref="File.Move(string, string, bool)"/>'s rename carries the
    /// attribute across from the source (cycle-2 pre-PR review finding), and a plain
    /// <see cref="File.Delete(string)"/> refuses a read-only file forever — which would leave
    /// <see cref="IsOwnStopRequest"/>'s <c>File.Exists(ClaimPath)</c> guard permanently true,
    /// shadowing every stop request written afterward with no daemon ever able to claim one
    /// again. Clearing the attribute and retrying once turns that permanent wedge back into
    /// the harmless case; only an ACL denial (rather than the read-only attribute) survives
    /// the retry, and that gets one warning rather than the silence this used to leave behind.
    /// </summary>
    private void DeleteClaimFile()
    {
        try
        {
            File.Delete(ClaimPath);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            File.SetAttributes(ClaimPath, FileAttributes.Normal);
            File.Delete(ClaimPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!warnedAboutStuckClaimFile)
            {
                logger.LogWarning(
                    exception,
                    "Could not delete {ClaimPath} even after clearing its read-only attribute — stop requests "
                        + "may go unclaimed until it is removed by hand",
                    ClaimPath);
                warnedAboutStuckClaimFile = true;
            }
        }
    }

    private static DateTimeOffset CurrentProcessStartedAt()
    {
        using Process current = Process.GetCurrentProcess();
        return new DateTimeOffset(current.StartTime.ToUniversalTime(), TimeSpan.Zero);
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
