using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
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
/// <item>Read the pull request's head live, so the review is pinned to one named commit rather than left to float, and a pull request that closed or merged mid-lap is refused before anything is posted.</item>
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

        // A verdict already on record refuses a SECOND one only while no lap is open to carry it
        // (task: a pr-review task stays open while the pull request's review threads are
        // unresolved). Before this feature the verdict ended the task, so a recorded verdict and
        // "no lap is running" were the same fact and one check served both. Now a scoped lap
        // (h9k pr review --since-my-review) legitimately reopens the same task to answer the
        // author, and its whole point is a second review on the same pull request — the flag this
        // guard is really about is the lap, so that is what it reads. ReviewLapOpen is false on
        // every pre-lap path this guard used to protect (an automated review's parked run, taken
        // straight to h9k pr approve), and on those the verdict is Unknown anyway.
        if (task.ReviewerVerdict != ReviewerVerdict.Unknown && !task.ReviewLapOpen)
        {
            // Both routes out name the PULL REQUEST rather than this task id, because that is what
            // h9k pr review's argument is: a task-id fragment there would be refused, or — when
            // the fragment happens to be all digits — silently read as a pull-request number
            // (self-review, round two).
            throw new DomainConflictException(
                $"Task {taskId} already delivered a {task.ReviewerVerdict.Value} verdict and no lap is open to "
                + "carry another. Open a lap on the pull request first: h9k pr review <pull request> "
                + "--since-my-review reads only what has changed since that verdict, and without the flag it "
                + $"reads the pull request whole. h9k task show {taskId} names the pull request.");
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

        // The run's own recorded session role, read for the refusal below and nothing else: a
        // lap-owned run whose PullRequestReviewLapOpened never landed is indistinguishable from
        // an automated pass by state alone, and the refusal has to say which one it is looking at
        // rather than guess (independent pre-PR review, cycle 1, conformance lens). The
        // projection can be newer than the fenced aggregate; SessionName is written once, by the
        // dispatch that started the stream, so newer cannot mean different here.
        RunDetails? runDetails = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        RefuseVerdictOnUnsettledRun(taskId, runId, task, run, runDetails?.SessionName ?? string.Empty);

        // Resolved BEFORE the post, not after, though only the append below needs it: this reads
        // the store and shells out to git and gh for a first-run owner record, so it is a real
        // failure surface — and every failure surface that can be moved ahead of the irreversible
        // half belongs there, where it costs the reviewer a re-run instead of a posted review the
        // platform never managed to record (self-review, round one).
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // Live, immediately before posting, for two things it does give and one it does not. It
        // gives the post a commit_id — the review names the tree it is an opinion about instead
        // of floating over whatever the branch becomes — and it refuses a pull request that
        // closed or merged mid-lap, before a post that GitHub would reject.
        //
        // What it does NOT do is notice that the head MOVED. Nothing here compares this sha
        // against the head the lap opened on or against the checkout's own HEAD, so an author who
        // pushes during a long lap gets the reviewer's verdict pinned to a commit the reviewer may
        // never have read. Decisions Log #149 ratifies posting on the current head, and the two
        // commands' help says CURRENT head plainly — this comment is here because the sentence
        // that stood in its place claimed the opposite guarantee, and the next maintainer would
        // have read the case as handled (independent pre-PR review, cycle 1, adversarial lens).
        // Closing it properly means carrying the opened-on sha to the verdict and refusing (or
        // warning) on a mismatch, which is a design change for a human to make, not a comment.
        PullRequestSurface pullRequest = await github.ReadAsync(
            $"{repository}#{number}", project.RepositoryPath, cancellationToken);
        if (!pullRequest.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainConflictException(
                $"{repository}#{number} is {pullRequest.State} now, not open — GitHub takes no review on it, "
                + "so nothing was posted. Close this task out with "
                + $"h9k review resolve {taskId} --merge-ready if the review no longer needs delivering.");
        }

        // The last thing before the irreversible half, and the last place this note is still
        // editable by anything but a human (task 412afe6c): what gets posted is what the project's
        // own writing conventions allow, and what has no mechanical fix stops the command here,
        // with nothing posted and nothing recorded, exactly like every other refusal above it. The
        // recorded note below is the vetted one, so the task's stream says what GitHub says.
        string vettedNote = PostedProse.Vet(note, project.WritingConventions, "the review's own note");

        // Each line comment goes through the same gate, for the same reason: GitHub posts all of
        // them in one review under the reviewer's login, and the review lap's own briefing tells
        // the drafting session the conventions govern "the note and each finding". Vetting only
        // the note would leave that promise half kept, on the half a reviewer reads in the diff.
        // Before the post, like the note, so a refusal here still leaves nothing sent.
        PullRequestReviewLineComment[] vettedFindings =
        [
            .. findings.Select(finding => finding with
            {
                Body = PostedProse.Vet(
                    finding.Body, project.WritingConventions,
                    $"the line comment on {finding.Path}:{finding.Line}"),
            }),
        ];
        PostedPullRequestReview posted = await github.PostReviewAsync(
            repository, number, pullRequest.HeadSha, verdict, vettedNote, vettedFindings, project.RepositoryPath,
            cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, new PullRequestReviewVerdictDelivered(
            taskId, verdict, vettedNote, [.. vettedFindings.Select(finding => finding.ToString())],
            posted.HeadSha, posted.ReviewUrl, now, context.OwnerId));
        session.Events.Append(runId, expectedVersion: runFence.Version + 1, new PrReviewDelivered(
            runId, DescribeForRunStream(verdict, vettedNote, vettedFindings.Length), now, context.OwnerId));

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
    /// <para>
    /// <paramref name="sessionName"/> is what keeps the Dispatched arm honest. A lap-owned run
    /// whose <c>PullRequestReviewLapOpened</c> never landed — a Ctrl-C between the dispatch and
    /// the record — leaves <c>ReviewLapOpen</c> false on a Dispatched run, and describing that as
    /// the automated review's own adversarial pass credits a session nobody launched with reading
    /// the pull request: the same never-guess-in-refusal-text class this file already fixed for
    /// Dispatched-versus-Running (independent pre-PR review, cycle 1, conformance lens). The lap
    /// side tells the two apart by the run's recorded session role
    /// (<c>PullRequestReviewCommand.IsLapRunWithNoLapRecordedAsync</c>) and so does this, with
    /// the same suffix check and for the same reason its own doc gives — a pre-field stream
    /// carries no name at all, which correctly reads as "not a lap".
    /// </para>
    /// </summary>
    private static void RefuseVerdictOnUnsettledRun(
        Guid taskId, Guid runId, TaskAggregate task, RunAggregate run, string sessionName)
    {
        bool ownOpenLap = task.ReviewLapOpen
            && task.ReviewLapRunId == runId
            && run.State == RunState.Dispatched;
        if (ownOpenLap || run.State == RunState.ReviewParked || run.State == RunState.BudgetParked)
        {
            return;
        }

        bool lapRunWithNoLapRecorded = run.State == RunState.Dispatched
            && sessionName.EndsWith("-" + SessionRoleName.ReviewLap, StringComparison.Ordinal);

        // Same reason-carries-its-own-way-out shape as
        // PullRequestReviewCommand.RefuseUnattachableRunAsync, and for the same finding: a
        // terminal run parks nothing, ever, so the shared "once it parks" suffix named a route
        // that does not exist for it (independent pre-PR review, cycle 1, adversarial lens).
        const string OnceItParks =
            "deliver the verdict once the automated review parks its findings report, or run h9k pr review "
            + "to read the pull request with the platform's help first";
        // The consequence travels per arm for the same never-guess reason the way out does: only
        // a run that is still going to park one can have a verdict overwritten by a findings
        // park, so asserting that for a terminal run — or for a lap whose opening is simply
        // missing — describes a park nobody will observe.
        const string OverwrittenByThePark =
            "would be overwritten by the review's own findings park and nothing would finalize the task";
        (string Because, string Consequence, string WayOut) refusal = run.State switch
        {
            // A lap of this node's own that never got its opening recorded is named as exactly
            // that, and its way out is the one command that records it: without the flag, the
            // daemon reads this run as a dispatch that died, so telling the reviewer an automated
            // pass occupies it names a session nobody launched (independent pre-PR review, cycle
            // 1, conformance lens). Ordered ahead of the plain Dispatched arm below, which is the
            // automated pass's own.
            var state when state == RunState.Dispatched && lapRunWithNoLapRecorded =>
                ("a review lap of this node's own was dispatched under that run and its opening was never "
                    + "recorded, so nothing on the task says a lap is open",
                    "would sit on a run the daemon still reads as a dispatch that never started — a restart "
                    + "fails that run, and takes the verdict's own record with it",
                    "h9k pr review on this pull request re-enters that same run and records the lap, and the "
                    + "verdict lands after it"),
            // Dispatched and Running are two different facts and shared one sentence: a
            // dispatched pass has been launched and has recorded nothing since, so telling its
            // reviewer it "is still reading" asserts a read nobody observed — AGENTS.md's
            // never-guess rule applied to a refusal's own text (Copilot review, pull request
            // #271). Same split, same reason, in RefuseUnattachableRunAsync's mirror of this.
            var state when state == RunState.Dispatched =>
                ("the automated review's own adversarial pass has been dispatched and has not reported "
                    + "starting yet", OverwrittenByThePark, OnceItParks),
            var state when state == RunState.Running =>
                ("the automated review's own adversarial pass is still reading the pull request",
                    OverwrittenByThePark, OnceItParks),
            var state when state == RunState.Verifying =>
                ("the automated review's adversarial pass has finished and the engine has not dispatched its "
                    + "conformance pass yet", OverwrittenByThePark, OnceItParks),
            var state when state == RunState.UnderReview =>
                ("the automated review's conformance pass is still running", OverwrittenByThePark, OnceItParks),
            var state when state.IsTerminal =>
                ($"its run is {state.Value}, which is terminal — that run will never park a findings report",
                    "would sit on a run that has already ended, and nothing finalizes a task from there",
                    $"h9k task retry {taskId} dispatches a fresh review, then h9k pr review opens the lap on it"),
            _ => ($"its run is {run.State.Value}", OverwrittenByThePark, OnceItParks),
        };
        throw new DomainConflictException(
            $"Task {taskId} cannot take a verdict right now: {refusal.Because}, so a verdict recorded against "
            + $"run {runId} {refusal.Consequence}. NOTHING was posted to the pull request by this command. "
            + $"h9k task show {taskId} to see where it stands; {refusal.WayOut}.");
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
