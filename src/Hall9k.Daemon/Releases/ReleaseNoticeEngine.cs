using Hall9k.Connectors.Releases;
using Hall9k.Domain.Features.Node;

namespace Hall9k.Daemon.Releases;

/// <summary>What one release check came to.</summary>
public enum ReleaseCheckResult
{
    /// <summary>A first notice for a newer release was recorded.</summary>
    NoticeRecorded,

    /// <summary>A reminder for a release this node was already told about was recorded.</summary>
    ReminderRecorded,

    /// <summary>Nothing to say: the node runs the newest release, the last notice is under a day old, or the lookup found nothing new (NotYetComplete, NoRelease).</summary>
    NothingNew,

    /// <summary>The lookup failed (gh missing, signed out, failing, timed out, unparseable, or an unsupported platform); nothing was recorded.</summary>
    LookupFailed,
}

/// <summary>
/// One check for a newer hall9k release (idea 93c1d24d, card 2): ask the shared lookup, decide with
/// <see cref="ReleaseNoticeCadence"/> whether this node's person is owed a notice, and record it
/// on the node's own stream. It only informs. Nothing here installs, restarts or messages anyone
/// but the node's own event stream, which the orchestrator feed, <c>h9k status</c> and
/// <c>h9k doctor</c> read.
/// <para>
/// A failed lookup records nothing and pages nobody; it logs one warning per failure episode
/// rather than one per check, and the episode ends the next time GitHub answers. The two answers
/// that are not failures, a release still uploading its assets (NotYetComplete) and no release at
/// all (NoRelease), mean nothing new this check: no event, no warning, and they end an episode too
/// since GitHub did answer. The last notice is read from the node's own stream at every check, so a
/// restart neither repeats a notice nor resets the clock.
/// </para>
/// </summary>
public sealed class ReleaseNoticeEngine(
    ReleaseCheck check, IReleaseNoticeStore store, string runningVersion, TimeProvider clock, ILogger<ReleaseNoticeEngine> logger)
{
    private bool failureEpisodeOpen;

    public async Task<ReleaseCheckResult> CheckOnceAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        LatestReleaseResult result = await check(cancellationToken);
        switch (result.Outcome)
        {
            case LatestReleaseOutcome.GhMissing
                or LatestReleaseOutcome.GhFailed
                or LatestReleaseOutcome.TimedOut
                or LatestReleaseOutcome.Unparseable
                or LatestReleaseOutcome.UnsupportedPlatform:
                WarnOncePerEpisode(result);
                return ReleaseCheckResult.LookupFailed;
            case LatestReleaseOutcome.NotYetComplete or LatestReleaseOutcome.NoRelease:
                failureEpisodeOpen = false;
                return ReleaseCheckResult.NothingNew;
        }

        failureEpisodeOpen = false;
        string tag = result.Tag ?? throw new InvalidOperationException(
            $"A {nameof(LatestReleaseOutcome.Found)} lookup result carries a tag.");
        DateTimeOffset now = clock.GetUtcNow();
        ReleaseNoticeIssued? recorded = await store.RecordIfDueAsync(
            nodeId,
            state => ReleaseNoticeCadence.Decide(tag, runningVersion, state.LastNoticeTag, state.LastNoticeAt, now) switch
            {
                ReleaseNoticeKind.First => new ReleaseNoticeIssued(nodeId, tag, result.PublishedAt, now, runningVersion, IsReminder: false),
                ReleaseNoticeKind.Reminder => new ReleaseNoticeIssued(nodeId, tag, result.PublishedAt, now, runningVersion, IsReminder: true),
                _ => null,
            },
            cancellationToken);

        if (recorded is null)
        {
            return ReleaseCheckResult.NothingNew;
        }

        logger.LogInformation(
            "Release notice: {Tag} is available and this node runs {RunningVersion} ({Kind})",
            recorded.Tag, runningVersion, recorded.IsReminder
                ? "reminder"
                : "first notice");
        return recorded.IsReminder
            ? ReleaseCheckResult.ReminderRecorded
            : ReleaseCheckResult.NoticeRecorded;
    }

    private void WarnOncePerEpisode(LatestReleaseResult result)
    {
        if (failureEpisodeOpen)
        {
            return;
        }

        failureEpisodeOpen = true;
        logger.LogWarning(
            "Release check failed ({Outcome}, {Command}): {Detail}. Further failures stay quiet until a check succeeds; "
            + "h9k doctor runs the same check by hand",
            result.Outcome, result.Command.Length > 0 ? result.Command : "no gh call made", result.Detail);
    }
}
