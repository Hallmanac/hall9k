using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The two heads a held fix reply is judged against: where the lap started and where the
/// platform's push left the pull request (task: a review-feedback lap's fix reply posts only
/// after the platform's push has moved the pull request's head). Either may be unknown, and
/// unknown never posts.
/// </summary>
/// <param name="StartedFrom">
/// The head the lap started from: the one closeout recorded when it dispatched the lap
/// (<c>RunDetails.OpeningReviewSinceSha</c>), or, when none was recorded, the tip this task had
/// recorded as last pushed to this branch when the push step began.
/// </param>
/// <param name="PushedTip">The tip the push step read from the branch it just pushed.</param>
public sealed record PushedHeads(string? StartedFrom, string? PushedTip)
{
    /// <summary>Both heads known and different: the push carried work the pull request did not have.</summary>
    public bool Moved => StartedFrom.IsNotBlank()
        && PushedTip.IsNotBlank()
        && !string.Equals(StartedFrom, PushedTip, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The decide-and-post step for a run's held fix replies, run by <see cref="PullRequestOpener"/>
/// right after the platform's push and before the task reads complete (task: a review-feedback
/// lap's fix reply posts only after the platform's push has moved the pull request's head).
/// <para>
/// A reply that says Fixed is true only of a head that carries the fix, and the session that
/// wrote it never pushes. So <c>h9k pr reply</c> holds a fix, and this step posts it only when
/// the push really moved the head, with one appended line naming the push: the shas before and
/// after and GitHub's compare link between them. The rule proves the push carried the lap's work,
/// not each thread's fix; the link is how a reader checks one.
/// </para>
/// <para>
/// Everything here is best-effort and per reply. A post or a resolve that fails is logged with
/// gh's error and leaves that thread open, without failing the run or stopping the other posts,
/// because by this point the branch is pushed and the delivery is not in question. Each decision
/// is recorded through <paramref name="record"/> as it lands, one event at a time, so a daemon
/// stopping mid-loop never drops the record of a reply already on GitHub, and a re-run for the
/// same run posts nothing a recorded post already covers.
/// </para>
/// <para>
/// It takes the heads, the run's records and a <see cref="GitHubReviewReplies"/> rather than
/// reading git or reaching GitHub itself, so the whole decision runs over a fake process runner.
/// </para>
/// </summary>
public sealed class HeldReplyPoster(
    ILogger logger,
    GitHubReviewReplies replies,
    Func<object, CancellationToken, Task> record)
{
    private const int ShortShaLength = 7;

    public async Task PostAsync(
        RunDetails run, PushedHeads heads, string repositoryPath, string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        // Posted ones are done; a withheld one is looked at again, so a restart's re-run retries a
        // post that failed and re-judges the rest on the same evidence.
        // Copied first: a caller recording into the same view it passes in must not mutate the list
        // this loop walks.
        List<HeldReplyRecord> unposted = [.. run.HeldReplies.Where(reply => reply.PostedAt is null)];
        foreach (HeldReplyRecord held in unposted)
        {
            string? withheld = WithholdReason(held, run, heads);
            if (withheld is not null)
            {
                await WithholdAsync(run.Id, held, withheld, cancellationToken);
                continue;
            }

            await PostOneAsync(run.Id, held, heads, repositoryPath, pullRequestUrl, cancellationToken);
        }
    }

    /// <summary>
    /// Why this held reply must not post, or null when it may. The closing-triage check comes
    /// first: a person's thread the session's own triage calls a decline or a route is one it
    /// should have drafted and parked, whichever way the head moved.
    /// </summary>
    internal static string? WithholdReason(HeldReplyRecord held, RunDetails run, PushedHeads heads)
    {
        if (held.TargetIsHumanAuthored && ContradictingTriage(held, run.ReviewThreadOutcomes) is { } triaged)
        {
            return ContradictionReason(triaged);
        }

        if (heads.StartedFrom.IsBlank())
        {
            return "the head this lap started from was not recorded, so there is no way to tell whether the "
                + "push moved the pull request's head";
        }

        if (heads.PushedTip.IsBlank())
        {
            return "the tip the push left could not be read back from the branch, so there is no way to tell "
                + "whether the push moved the pull request's head";
        }

        return heads.Moved
            ? null
            : $"the push did not move the pull request's head (it stayed at {Short(heads.PushedTip)}), so this "
                + "lap put nothing on the pull request that a reply could claim as fixed";
    }

    /// <summary>
    /// The disposition the closing triage gave a held reply's thread when it contradicts a fix
    /// claim (decline or route), else null. Shared with the supervisor's warning at the moment the
    /// triage lands, so the log line and the push step's decision read one rule.
    /// </summary>
    internal static ReviewThreadDisposition? ContradictingTriage(
        HeldReplyRecord held, IEnumerable<ReviewThreadOutcome> outcomes) =>
        held.ThreadId is null
            ? null
            : outcomes
                .Where(outcome => string.Equals(outcome.ThreadId, held.ThreadId, StringComparison.Ordinal))
                .Select(outcome => outcome.Disposition)
                .FirstOrDefault(disposition =>
                    disposition == ReviewThreadDisposition.Decline || disposition == ReviewThreadDisposition.Route);

    /// <summary>
    /// The replies still waiting that the closing triage contradicts, with the disposition it gave
    /// each: a fix reply into a person's thread that the session's own triage calls a decline or a
    /// route. The supervisor withholds these as the triage lands.
    /// </summary>
    internal static IReadOnlyList<(HeldReplyRecord Held, ReviewThreadDisposition Triaged)> ContradictedByTriage(
        RunDetails run, IReadOnlyList<ReviewThreadOutcome> outcomes)
    {
        List<(HeldReplyRecord Held, ReviewThreadDisposition Triaged)> contradicted = [];
        foreach (HeldReplyRecord held in run.HeldReplies.Where(reply => reply.IsWaiting && reply.TargetIsHumanAuthored))
        {
            if (ContradictingTriage(held, outcomes) is { } triaged)
            {
                contradicted.Add((held, triaged));
            }
        }

        return contradicted;
    }

    /// <summary>Why a held fix reply is withheld when the closing triage called its thread <paramref name="triaged"/>.</summary>
    internal static string ContradictionReason(ReviewThreadDisposition triaged) =>
        $"the session's own closing triage recorded this thread as {triaged.Value.ToLowerInvariant()}, "
        + "which contradicts a reply that says Fixed; it is drafted and parked for the owner instead";

    /// <summary>The line appended to a posted fix reply, naming the push that carried the lap's work.</summary>
    internal static string PushLine(string pullRequestUrl, PushedHeads heads)
    {
        string before = heads.StartedFrom ?? string.Empty;
        string after = heads.PushedTip ?? string.Empty;
        string line = $"Carried by the push that moved this pull request from {Short(before)} to {Short(after)}";
        return PullRequestUrls.CompareUrl(pullRequestUrl, before, after) is { } compare
            ? $"{line}: {compare}"
            : $"{line}.";
    }

    private async Task PostOneAsync(
        Guid runId, HeldReplyRecord held, PushedHeads heads, string repositoryPath, string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        string body = $"{held.Body}\n\n{PushLine(pullRequestUrl, heads)}";
        try
        {
            if (held.ThreadId is { } threadId)
            {
                await replies.ReplyInThreadAsync(repositoryPath, threadId, body, cancellationToken);
            }
            else
            {
                await replies.CommentAsync(
                    repositoryPath, PullRequestUrls.ParseNumber(pullRequestUrl),
                    GitHubReviewReplies.ReviewBodyComment(held.ReviewUrl ?? string.Empty, body), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: the platform's push moved the head, but posting the held fix reply for {Target} "
                + "failed: {Error}. The thread stays open and the other replies still post.",
                runId, held.Target, exception.Message);
            await WithholdAsync(runId, held, $"the post failed: {exception.Message}", cancellationToken);
            return;
        }

        // Recorded the moment it lands, in its own write: the reply is already on GitHub, and a
        // later failure in this loop must not take the record of it along.
        object posted = held.ThreadId is { } thread
            ? new ReviewThreadReplyPosted(
                runId, thread, held.Disposition, held.TargetIsHumanAuthored, DateTimeOffset.UtcNow, held.ReplyId)
            : new ReviewBodyReplyPosted(
                runId, held.ReviewUrl ?? string.Empty, held.Disposition, held.TargetIsHumanAuthored,
                DateTimeOffset.UtcNow, held.ReplyId);
        await RecordAsync(
            posted, runId, $"the fix reply for {held.Target} is on the pull request but unrecorded here",
            cancellationToken);

        if (held.ThreadId is { } threadToResolve)
        {
            await ResolveAsync(runId, threadToResolve, repositoryPath, cancellationToken);
        }
    }

    private async Task ResolveAsync(
        Guid runId, string threadId, string repositoryPath, CancellationToken cancellationToken)
    {
        try
        {
            await replies.ResolveThreadAsync(repositoryPath, threadId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: posted the held fix reply in thread {ThreadId} but could not resolve it: {Error}. "
                + "The thread stays open.",
                runId, threadId, exception.Message);
        }
    }

    private async Task WithholdAsync(
        Guid runId, HeldReplyRecord held, string reason, CancellationToken cancellationToken)
    {
        // A re-run reaching the same decision says nothing new.
        if (string.Equals(held.WithheldReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        logger.LogInformation(
            "Run {RunId}: the held fix reply for {Target} was withheld: {Reason}", runId, held.Target, reason);
        await RecordAsync(
            new ReviewReplyWithheld(runId, held.ReplyId, reason, DateTimeOffset.UtcNow), runId,
            $"the fix reply for {held.Target} was withheld ({reason}) but unrecorded here", cancellationToken);
    }

    private async Task RecordAsync(
        object @event, Guid runId, string consequence, CancellationToken cancellationToken)
    {
        try
        {
            await record(@event, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Run {RunId}: could not record a held-reply decision: {Consequence}",
                runId, consequence);
        }
    }

    private static string Short(string? sha) =>
        sha is { Length: > ShortShaLength } ? sha[..ShortShaLength] : sha ?? string.Empty;
}
