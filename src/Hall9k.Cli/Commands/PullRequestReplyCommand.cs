using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The platform's posting path for one reply on a task's own pull request, and the only route a
/// dispatched session has to one (task: a review-feedback follow-up never answers a human reviewer
/// in the owner's name on its own). It has two forms: <c>--thread</c> answers inside a review
/// thread, and <c>--review</c> answers a review BODY with one top-level comment. Every reply goes
/// through here so that exactly one question can be asked before the words leave the machine:
/// whose is this?
/// <para>
/// <b>What it refuses.</b> A decline or a route into a thread a PERSON opened. Telling a
/// colleague their point does not hold is the implementer's to send, so the lap drafts it, closes
/// with the <c>DISAGREEMENT:</c> block and <c>RESOLUTION: disputed</c>, and the run parks for the
/// owner to send, edit, or drop it (<c>h9k review resolve</c>). Origin incidents: arx-platform
/// PR #2021 on 2026-09-09 and PR #2042 on 2026-09-15, where accurate replies to two people went
/// out under Brian's login with no step where he saw them first.
/// </para>
/// <para>
/// <b>What it allows, unchanged.</b> Every reply into a BOT's thread, on any disposition —
/// Copilot's findings stay on the automated path they have always been on (Decisions Log #159) —
/// and a fix's reply into a person's thread, because "I changed it, here is what changed" invites
/// no argument and the commit is the evidence.
/// </para>
/// <para>
/// <b>Whose thread it is, and how much is trusted.</b> The author kind is not the session's to
/// state: it is matched against the human-authored threads closeout itself read off the provider
/// when it dispatched this lap (<c>TaskReopened.HumanReviewThreads</c>). The disposition IS the
/// session's own word, and that is the one soft spot in this guard — so every accepted reply
/// records the claim (<see cref="ReviewThreadReplyPosted"/>) and <c>RunSupervisor</c> compares it
/// against the same thread's own <c>THREAD DISPOSITION:</c> block when the session finishes, so a
/// reply that claimed fix and was really a decline lands in the run log rather than nowhere.
/// </para>
/// <para>
/// <b>The review-body form</b> (task: a dispatched session never speaks to a person at the top
/// level of a pull request on its own). A review BODY is unthreadable, so its only answer is a
/// top-level comment, which used to be the one reply route that skipped this question. The form
/// reads the review's author from GitHub at the time it runs (<see cref="GitHubPullRequestReviews"/>),
/// never from the session's word: a Bot actor type or a known Copilot login is a bot, any other
/// author is a person, and a review GitHub returns with no readable author is a person's. A fix
/// posts either way, and a decline or route on a person's review posts nothing and is recorded as
/// refused (<see cref="ReviewBodyReplyRefused"/>), with the review's url and author as GitHub
/// reported them. That record is what the park draws its reply choices from, so a review url a
/// session merely composed is never offered one. A review that is not on the task's own pull
/// request, or one GitHub will not return, is refused with nothing posted and nothing recorded.
/// </para>
/// <para>
/// <b>What a thread nobody observed does.</b> Nothing: it posts. A thread this install never read
/// as human-authored is an unobserved fact, not an observed bot (AGENTS.md), and refusing every
/// unrecognized id would break a lap answering a thread opened after its own dispatch read — the
/// ordinary case on a busy pull request. The prompt's rule still stands over the session there,
/// and the next sweep's read is what brings the thread inside this guard.
/// </para>
/// </summary>
public sealed class PullRequestReplyCommand : Hall9kAsyncCommand<PullRequestReplyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment) whose pull request carries the thread or review")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--thread <THREAD>")]
        [Description("The review thread's GraphQL node id (PRRT_…), which is where the reply lands. Pass this or --review")]
        public string Thread { get; init; } = string.Empty;

        [CommandOption("--review <REVIEW_URL>")]
        [Description(
            "A review BODY to answer instead of a thread: the review's own url "
            + "(<pull request url>#pullrequestreview-<id>). A review body is unthreadable, so this posts one "
            + "top-level comment on the task's pull request naming that review. Whose review it is comes from "
            + "GitHub, read when this runs, and a decline or a route on a PERSON's review is refused and "
            + "parked for the owner exactly as a person's thread is")]
        public string Review { get; init; } = string.Empty;

        [CommandOption("--disposition <DISPOSITION>")]
        [Description(
            "The triage disposition this reply carries: fix, decline, or route. A decline or a route "
            + "into a thread a PERSON opened, or onto a review body a PERSON wrote, is refused — draft it, park it, and let the owner send it "
            + "(PLAN.md Decisions Log #62, #159, and the review-feedback reply park)")]
        public string Disposition { get; init; } = string.Empty;

        [CommandOption("--body <BODY>")]
        [Description("The reply itself, posted verbatim under the owner's login (a review-body reply is prefixed with the review's url)")]
        public string Body { get; init; } = string.Empty;

        public override ValidationResult Validate() => this switch
        {
            { Thread: var thread, Review: var review } when thread.IsBlank() && review.IsBlank() =>
                ValidationResult.Error(
                    "Pass --thread <node id> to reply inside a review thread, or --review <review url> to "
                    + "answer a review's own body."),
            { Thread: var thread, Review: var review } when thread.IsNotBlank() && review.IsNotBlank() =>
                ValidationResult.Error("Pass --thread or --review, not both: a reply lands in one place."),
            { Body: var body } when body.IsBlank() =>
                ValidationResult.Error("Pass --body \"<text>\" — there is nothing to post otherwise."),
            _ when ReviewThreadDisposition.Parse(Disposition) == ReviewThreadDisposition.Unknown =>
                ValidationResult.Error(
                    "Pass --disposition fix|decline|route. It is refused unstated rather than "
                    + "assumed, because it is what decides whether a person's thread or review may be "
                    + "answered here at all."),
            _ => ValidationResult.Success(),
        };
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        ProcessRunner runner = new ProjectScopedGitHubRunner(store).Runner;
        return await ReplyAsync(
            session, taskId, settings, new GitHubReviewReplies(runner), new GitHubPullRequestReviews(runner),
            cancellationToken);
    }

    /// <summary>
    /// The whole command over a session and a resolved task id the caller supplies — internal so
    /// the guard is testable against a real store without <see cref="CliStore.Open"/>'s ambient
    /// connection, the same seam <c>ReviewResolveCommand.ResolveAsync</c> exists for.
    /// </summary>
    internal static async Task<int> ReplyAsync(
        IDocumentSession session, Guid taskId, Settings settings, GitHubReviewReplies replies,
        GitHubPullRequestReviews reviews, CancellationToken cancellationToken)
    {
        (TaskAggregate task, Guid runId) = await LoadReplyContextAsync(session, taskId, cancellationToken);
        return settings.Review.IsNotBlank()
            ? await ReplyToReviewBodyAsync(session, task, runId, settings, replies, reviews, cancellationToken)
            : await ReplyInThreadAsync(session, task, runId, settings, replies, cancellationToken);
    }

    /// <summary>
    /// The checks both forms share: the task has a current run to record against, a pull request of
    /// its own to reply on, and a run stream that exists.
    /// </summary>
    private static async Task<(TaskAggregate Task, Guid RunId)> LoadReplyContextAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run, so there is no lap this reply belongs to.");
        if (task.PullRequestUrl.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {taskId} has no pull request recorded, so there is no review thread or review of its "
                + "own to reply to.");
        }

        // Guards against a CurrentRunId whose stream does not exist yet, which is a real window
        // rather than a defensive one: TaskClaimed records the run id, and RunDispatched opens
        // that stream afterwards, so an append in between would have Marten silently create a run
        // stream holding a reply record with no RunDispatched under it — an invalid run history.
        // The same existence fence, for the same reason, as
        // TaskLogInteractionCommand.AppendInteractionAsync.
        //
        // No expectedVersion rides with it, by the rule that command states in full: a fenced
        // append is for one that decides something from the state it just fetched, and both
        // appends below are pure log entries. Nothing here is decided from the run stream at all
        // — the guard's one decision reads the TASK aggregate, a different stream, which no
        // expectedVersion on a run-stream append can fence. Fencing it anyway would cost
        // something real: a concurrent daemon append to the run would lose the record of a reply
        // already public on the pull request, which is the one lie this record must never tell
        // (Copilot, PR #397).
        _ = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId}'s current run {runId} has no run stream yet, so there is no lap to record "
                + "this reply against. Nothing was posted.");
        return (task, runId);
    }

    private static async Task<int> ReplyInThreadAsync(
        IDocumentSession session, TaskAggregate task, Guid runId, Settings settings, GitHubReviewReplies replies,
        CancellationToken cancellationToken)
    {
        ReviewThreadDisposition disposition = ReviewThreadDisposition.Parse(settings.Disposition);
        string threadId = settings.Thread.Trim();
        // Ordinal: a GraphQL node id is opaque and case-significant, so a near-match names a
        // different thread rather than the same one spelled differently.
        ReviewThreadReference? human = task.KnownHumanReviewThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));

        if (human is not null && disposition != ReviewThreadDisposition.Fix)
        {
            string reason =
                $"Thread {threadId} was opened by @{human.Author}, a person, and this reply is a "
                + $"{disposition.Value.ToLowerInvariant()} — which is not an agent's to post. Nothing was "
                + "sent. Draft the reply instead, close your summary with the DISAGREEMENT block naming "
                + $"thread={threadId} and disposition={disposition.Value.ToLowerInvariant()}, then "
                + "RESOLUTION: disputed. The run parks and the owner sends it, edits it, or drops it "
                + "(PLAN.md Decisions Log #62, #159).";
            session.Events.Append(
                runId, new ReviewThreadReplyRefused(runId, threadId, disposition, reason, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            throw new DomainValidationException(reason);
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId} — cannot reach its repository to post.");

        // Vetted against the project's writing conventions before the write, the same place
        // ReviewResolveCommand vets its own drafted replies and for the same reason: this posts
        // under the owner's login, so a reply that breaks the house style is stopped here rather
        // than discovered by the reviewer reading it.
        string body = PostedProse.Vet(
            settings.Body.Trim(), project.WritingConventions, $"the reply for review thread {threadId}");

        // The provider write is the LAST thing that can fail before the append, the same ordering
        // ReviewResolveCommand's own post keeps: a failed post leaves nothing on the stream saying
        // the reviewer was answered, which is the one lie this record must never tell.
        await replies.ReplyInThreadAsync(project.RepositoryPath, threadId, body, cancellationToken);
        session.Events.Append(runId, new ReviewThreadReplyPosted(
            runId, threadId, disposition, human is not null, DateTimeOffset.UtcNow));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The one window left, and the same one ReviewResolveCommand's own post spends
            // paragraphs on: the reply is already on the pull request and the record of it is
            // not. A bare failure here reads as "that did not work" and invites the session to
            // run the identical command again, which is a second reply under the owner's login
            // — GitHub offers no idempotency key on a review reply.
            throw new DomainConflictException(
                $"The reply DID reach review thread {threadId} — gh accepted it — and recording it on run "
                + $"{runId} failed afterwards: {exception.Message} Do NOT re-run this command; the reviewer "
                + "would read the same words twice. The run's own record is short one reply, which is the "
                + "smaller of the two problems.");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Replied in review thread {threadId} on {task.PullRequestUrl}.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The review-body form: one top-level comment on the task's own pull request that names the
    /// review it answers. The review is read from GitHub first, and a review that is not on this
    /// pull request or cannot be read ends the command there with nothing posted and nothing
    /// recorded, so no later park can mistake the attempt for GitHub's report of that review.
    /// </summary>
    private static async Task<int> ReplyToReviewBodyAsync(
        IDocumentSession session, TaskAggregate task, Guid runId, Settings settings, GitHubReviewReplies replies,
        GitHubPullRequestReviews reviews, CancellationToken cancellationToken)
    {
        ReviewThreadDisposition disposition = ReviewThreadDisposition.Parse(settings.Disposition);
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId} — cannot reach its repository to post.");

        PullRequestReview review = await reviews.ReadAsync(
            task.PullRequestUrl, settings.Review.Trim(), project.RepositoryPath, cancellationToken);

        if (!review.AuthoredByBot && disposition != ReviewThreadDisposition.Fix)
        {
            string author = review.AuthorLogin.IsNotBlank() ? $"@{review.AuthorLogin}" : "an author GitHub did not report";
            string reason =
                $"Review {review.Url} was written by {author}, a person, and this reply is a "
                + $"{disposition.Value.ToLowerInvariant()} — which is not an agent's to post. Nothing was "
                + "sent. Draft the reply instead, close your summary with the DISAGREEMENT block naming "
                + $"review={review.Url} and disposition={disposition.Value.ToLowerInvariant()}, then "
                + "RESOLUTION: disputed. The run parks and the owner sends it, edits it, or drops it "
                + "(PLAN.md Decisions Log #62, #159).";
            session.Events.Append(runId, new ReviewBodyReplyRefused(
                runId, review.Url, review.AuthorLogin, disposition, reason, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            throw new DomainValidationException(reason);
        }

        // Vetted after the author read and before the write, the same place the thread form vets:
        // this posts under the owner's login, so a reply that breaks the house style is stopped
        // here rather than discovered by the reviewer reading it.
        string body = PostedProse.Vet(
            settings.Body.Trim(), project.WritingConventions, $"the comment answering review {review.Url}");

        // The same naming ReviewResolveCommand gives the owner's own send of a parked body draft,
        // so the reviewer reads one shape whichever route the words took.
        await replies.CommentAsync(
            project.RepositoryPath, PullRequestUrls.ParseNumber(task.PullRequestUrl),
            $"On {review.Url}:\n\n{body}", cancellationToken);
        session.Events.Append(runId, new ReviewBodyReplyPosted(
            runId, review.Url, disposition, !review.AuthoredByBot, DateTimeOffset.UtcNow));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The same window the thread form spends its comment on: the comment is already on
            // the pull request and the record of it is not, and a bare failure invites a second
            // identical comment under the owner's login.
            throw new DomainConflictException(
                $"The comment DID reach pull request {task.PullRequestUrl}, naming review {review.Url} — gh "
                + $"accepted it — and recording it on run {runId} failed afterwards: {exception.Message} "
                + "Do NOT re-run this command; the reviewer would read the same words twice. The run's own "
                + "record is short one reply, which is the smaller of the two problems.");
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Commented on {task.PullRequestUrl} answering review {review.Url}.[/]");
        return ExitCodes.Ok;
    }
}
