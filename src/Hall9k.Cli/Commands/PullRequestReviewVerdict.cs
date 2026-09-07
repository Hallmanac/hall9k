using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one delivery path both reviewer verdicts share (<c>h9k pr approve</c> /
/// <c>h9k pr request-changes</c>, Decisions Log #149). Its shape is deliberate and the order of
/// its four steps is the whole design:
/// <list type="number">
/// <item>Read the pull request's head live, so the review is submitted against the tree the reviewer actually read rather than whatever the head is when GitHub gets around to it.</item>
/// <item>Post the GitHub review. This is the deliverable — everything after it is bookkeeping about something that already happened out in the world.</item>
/// <item>Record the verdict on the pr-review task, and <see cref="PrReviewDelivered"/> on its run, in one transaction.</item>
/// <item>Ring the doorbell so the daemon finalizes the task exactly as <c>h9k review resolve --merge-ready</c> already does — releasing the worktree, completing the task, dropping the lease, with no merge ever observed.</item>
/// </list>
/// <para>
/// The post comes BEFORE the record, never after, and that is the one ordering choice here worth
/// arguing about. Recording first would let a failed post leave a task closed and a verdict on
/// the stream that no reviewer on the pull request can see — the platform asserting a review it
/// never delivered, which is exactly the unobserved-fact fabrication AGENTS.md forbids. This way
/// a failed post leaves nothing recorded and the reviewer simply runs the command again. The cost
/// is the mirror-image window: a post that succeeds and a record that then fails leaves a review
/// on GitHub with the task still open. That is the survivable half — the reviewer's verdict
/// reached the pull request, which is the deliverable, and the task is closed by hand with
/// <c>h9k review resolve --merge-ready</c> or by running this command again (GitHub accepts a
/// second review from the same reviewer; it does not accept an unsubmitted one).
/// </para>
/// </summary>
internal static class PullRequestReviewVerdict
{
    /// <summary>
    /// Posts and records. <paramref name="github"/> is injected so the delivery rules are testable
    /// against a scripted <c>gh</c> rather than a live account — the same seam every other GitHub
    /// read in this codebase already takes.
    /// </summary>
    internal static async Task<int> DeliverAsync(
        IDocumentSession session,
        Guid taskId,
        ReviewerVerdict verdict,
        string note,
        IReadOnlyList<PullRequestReviewLineComment> findings,
        GitHubPullRequestSurface github,
        CancellationToken cancellationToken)
    {
        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.Type != TaskType.PrReview)
        {
            throw new DomainValidationException(
                $"Task {taskId} is a {task.Type.Value} task, not a pr-review task — there is no pull request "
                + "of somebody else's here to submit a review on. h9k review resolve is the verdict a build "
                + "task's own review park takes.");
        }

        if (task.ReviewerVerdict != ReviewerVerdict.Unknown)
        {
            throw new DomainConflictException(
                $"Task {taskId} already delivered a {task.ReviewerVerdict.Value} verdict — the lap it belonged "
                + "to has ended. Review the pull request again with h9k pr review to open a fresh lap (a Done "
                + "pr-review task does not hold its pull request hostage).");
        }

        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no run to record the verdict against — no review lap and no automated "
                + $"review has ever started here. h9k pr review <number> opens the lap first.");
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(details.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        if (!GitHubPullRequestReference.TryParseCanonical(
            ExternalReference.Parse(details.ExternalReference).Reference, out string repository, out int number))
        {
            throw new DomainConflictException(
                $"Task {taskId} carries no readable pull-request reference "
                + $"('{details.ExternalReference}'), so there is nothing to post a review to. A pr-review "
                + "task always adopts one; this needs a human look at the task's own stream.");
        }

        // Fenced against the RUN before the post, not after: the post is irreversible, so a run
        // that has already moved past the lap (a concurrent verdict, a daemon that finalized it)
        // must be caught while nothing has been sent. The same fetch supplies the expected
        // version the append below carries.
        StreamState runFence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: runFence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        if (run.PrReviewDelivered)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} has already been delivered — the daemon is finalizing it. "
                + "Nothing was posted to the pull request by this command.");
        }

        RefuseVerdictOnUnsettledRun(taskId, runId, task, run);

        // Resolved BEFORE the post, not after, though only the append below needs it: this reads
        // the store and shells out to git and gh for a first-run owner record, so it is a real
        // failure surface — and every failure surface that can be moved ahead of the irreversible
        // half belongs there, where it costs the reviewer a re-run instead of a posted review the
        // platform never managed to record (self-review, round one).
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // Live, immediately before posting: a lap can be open for hours, and a review attached to
        // a head the reviewer never read is worse than no review. The read is also what refuses a
        // pull request that closed or merged mid-lap, before a post that GitHub would reject.
        PullRequestSurface pullRequest = await github.ReadAsync(
            $"{repository}#{number}", project.RepositoryPath, cancellationToken);
        if (!pullRequest.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException(
                $"{repository}#{number} is {pullRequest.State} now, not open — GitHub takes no review on it, "
                + "so nothing was posted. Close this task out with "
                + $"h9k review resolve {taskId} --merge-ready if the review no longer needs delivering.");
        }

        PostedPullRequestReview posted = await github.PostReviewAsync(
            repository, number, pullRequest.HeadSha, verdict, note, findings, project.RepositoryPath,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, new PullRequestReviewVerdictDelivered(
            taskId, verdict, note, [.. findings.Select(finding => finding.ToString())],
            posted.HeadSha, posted.ReviewUrl, now, context.OwnerId));
        session.Events.Append(runId, expectedVersion: runFence.Version + 1, new PrReviewDelivered(
            runId, DescribeForRunStream(verdict, note, findings.Count), now, context.OwnerId));

        // The run is no longer parked, so the expiry sweep's parked-run shield no longer covers
        // this lease; a fresh heartbeat holds the task while the daemon wakes. Exactly what
        // ReviewResolveCommand's own pr-review path does, for the same reason.
        TaskLease? lease = await session.LoadAsync<TaskLease>(taskId, cancellationToken);
        if (lease is not null)
        {
            lease.HeartbeatAt = now;
            session.Store(lease);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // The review IS posted — say so plainly rather than reporting a failure that would
            // send the reviewer to post a second one. EVERY failure of the record, not only the
            // concurrency one: this is the mirror-image window the class doc names, and a reviewer
            // handed a bare Postgres error (or a Ctrl-C swallowed as cancellation) after the
            // irreversible half cannot tell whether their verdict reached the pull request, so
            // they re-run and post a duplicate review (self-review, round one — the catch as
            // first written named EventStreamUnexpectedMaxEventIdException alone, which is only
            // one of the two ways the record can fail).
            string cause = exception is EventStreamUnexpectedMaxEventIdException
                ? $"task {taskId} changed while recording it"
                : $"recording it failed ({exception.Message})";
            throw new DomainConflictException(
                $"The {verdict.Value} review WAS posted to {repository}#{number}, but {cause}, so the verdict "
                + "is not on the task's own stream. Close the task out with "
                + $"h9k review resolve {taskId} --merge-ready — the pull request already has your review.");
        }

        await Doorbell.RingAsync($"pr-review-verdict:{taskId}", cancellationToken);
        string comments = findings.Count > 0 ? $" with {findings.Count} line comment(s)" : string.Empty;
        AnsiConsole.MarkupLineInterpolated(
            $"[green]{verdict.Value}[/] [dim]posted on {repository}#{number} at {ShortSha(posted.HeadSha)}{comments}.[/]");
        if (posted.ReviewUrl.IsNotBlank())
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]  {posted.ReviewUrl}[/]");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId}: the daemon releases the worktree and completes the task — no merge is ever observed for a review.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The mirror of <c>PullRequestReviewCommand.RefuseUnattachableRunAsync</c>, on the verdict
    /// side rather than the lap-opening side: a verdict is only recordable against a run the
    /// daemon will finalize it from. Three states qualify — <see cref="RunState.ReviewParked"/>
    /// (the ordinary case, the automated review's own findings park),
    /// <see cref="RunState.BudgetParked"/> (admitted for the same reason a lap may attach to one:
    /// an exhausted token budget is the platform's problem, not the reviewer's), and the
    /// reviewer's own open lap, whose run sits at <see cref="RunState.Dispatched"/> for the lap's
    /// whole life because nothing ever moves it.
    /// <para>
    /// A run mid-lens does not, and this is the check whose absence was the defect (independent
    /// pre-PR review, cycle 1, both lenses). <c>PrReviewEngine.DriveAsync</c> checks
    /// <c>PrReviewDelivered</c> once, on entry, and then calls
    /// <c>ComposeReportAndParkAsync</c> unconditionally when its lens returns — so a verdict
    /// landing while the machines are still reading is followed by a <c>ReviewParked</c> on an
    /// already-delivered run, and <em>nothing</em> finalizes that: <c>StrandedRunStates</c> is
    /// [UnderReview, Verifying] and startup adoption's ReviewParked arm only refreshes the lease.
    /// The task would sit Claimed/NeedsHuman with a posted verdict until somebody found
    /// <c>h9k review resolve --merge-ready</c> by hand. Refused before anything is posted, which
    /// is the whole point of doing it here rather than after the irreversible half.
    /// </para>
    /// </summary>
    private static void RefuseVerdictOnUnsettledRun(
        Guid taskId, Guid runId, TaskAggregate task, RunAggregate run)
    {
        bool ownOpenLap = task.ReviewLapOpen
            && task.ReviewLapRunId == runId
            && run.State == RunState.Dispatched;
        if (ownOpenLap || run.State == RunState.ReviewParked || run.State == RunState.BudgetParked)
        {
            return;
        }

        // Same reason-carries-its-own-way-out shape as
        // PullRequestReviewCommand.RefuseUnattachableRunAsync, and for the same finding: a
        // terminal run parks nothing, ever, so the shared "once it parks" suffix named a route
        // that does not exist for it (independent pre-PR review, cycle 1, adversarial lens).
        const string OnceItParks =
            "deliver the verdict once the automated review parks its findings report, or run h9k pr review "
            + "to read the pull request with the platform's help first";
        (string Because, string WayOut) refusal = run.State switch
        {
            // Dispatched and Running are two different facts and shared one sentence: a
            // dispatched pass has been launched and has recorded nothing since, so telling its
            // reviewer it "is still reading" asserts a read nobody observed — AGENTS.md's
            // never-guess rule applied to a refusal's own text (Copilot review, pull request
            // #271). Same split, same reason, in RefuseUnattachableRunAsync's mirror of this.
            var state when state == RunState.Dispatched =>
                ("the automated review's own adversarial pass has been dispatched and has not reported "
                    + "starting yet", OnceItParks),
            var state when state == RunState.Running =>
                ("the automated review's own adversarial pass is still reading the pull request", OnceItParks),
            var state when state == RunState.Verifying =>
                ("the automated review's adversarial pass has finished and the engine has not dispatched its "
                    + "conformance pass yet", OnceItParks),
            var state when state == RunState.UnderReview =>
                ("the automated review's conformance pass is still running", OnceItParks),
            var state when state.IsTerminal =>
                ($"its run is {state.Value}, which is terminal — that run will never park a findings report",
                    $"h9k task retry {taskId} dispatches a fresh review, then h9k pr review opens the lap on it"),
            _ => ($"its run is {run.State.Value}", OnceItParks),
        };
        throw new DomainConflictException(
            $"Task {taskId} cannot take a verdict right now: {refusal.Because}, so a verdict recorded against "
            + $"run {runId} would be overwritten by the review's own findings park and nothing would finalize "
            + "the task. NOTHING was posted to the pull request by this command. h9k task show "
            + $"{taskId} to see where it stands; {refusal.WayOut}.");
    }

    /// <summary>
    /// What the run stream's own <see cref="PrReviewDelivered.Reason"/> says. The task stream
    /// carries the note and the findings verbatim; this is the one-line version for a reader of
    /// the run's history, and it names the verdict because "delivered" alone would read the same
    /// for an approval and a changes-requested review.
    /// </summary>
    private static string DescribeForRunStream(ReviewerVerdict verdict, string note, int findingCount)
    {
        string comments = findingCount == 0
            ? string.Empty
            : $" ({findingCount} line comment{(findingCount == 1 ? string.Empty : "s")})";
        return $"{verdict.Value} review submitted to GitHub{comments}: {note}";
    }

    private static string ShortSha(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
