using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
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
/// The platform's posting path for one in-thread reply on a task's own pull request, and the
/// only route a follow-up session has to one (task: a review-feedback follow-up never answers a
/// human reviewer in the owner's name on its own). Every reply goes through here so that exactly
/// one question can be asked before the words leave the machine: whose thread is this?
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
        [Description("Task id (full, or an unambiguous fragment) whose pull request carries the thread")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--thread <THREAD>")]
        [Description("The review thread's GraphQL node id (PRRT_…), which is where the reply lands")]
        public string Thread { get; init; } = string.Empty;

        [CommandOption("--disposition <DISPOSITION>")]
        [Description(
            "The triage disposition this reply carries: fix, decline, or route. A decline or a route "
            + "into a thread a PERSON opened is refused — draft it, park it, and let the owner send it "
            + "(PLAN.md Decisions Log #62, #159, and the review-feedback reply park)")]
        public string Disposition { get; init; } = string.Empty;

        [CommandOption("--body <BODY>")]
        [Description("The reply itself, posted verbatim under the owner's login")]
        public string Body { get; init; } = string.Empty;

        public override ValidationResult Validate() =>
            Thread.IsBlank()
                ? ValidationResult.Error("Pass --thread <node id> — the thread the reply lands in.")
                : Body.IsBlank()
                    ? ValidationResult.Error("Pass --body \"<text>\" — there is nothing to post otherwise.")
                    : ReviewThreadDisposition.Parse(Disposition) == ReviewThreadDisposition.Unknown
                        ? ValidationResult.Error(
                            "Pass --disposition fix|decline|route. It is refused unstated rather than "
                            + "assumed, because it is what decides whether a person's thread may be "
                            + "answered here at all.")
                        : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        return await ReplyAsync(session, taskId, settings, new GitHubReviewReplies(), cancellationToken);
    }

    /// <summary>
    /// The whole command over a session and a resolved task id the caller supplies — internal so
    /// the guard is testable against a real store without <see cref="CliStore.Open"/>'s ambient
    /// connection, the same seam <c>ReviewResolveCommand.ResolveAsync</c> exists for.
    /// </summary>
    internal static async Task<int> ReplyAsync(
        IDocumentSession session, Guid taskId, Settings settings, GitHubReviewReplies replies,
        CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run, so there is no lap this reply belongs to.");
        if (task.PullRequestUrl.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {taskId} has no pull request recorded, so there is no review thread of its own to "
                + "reply in.");
        }

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
}
