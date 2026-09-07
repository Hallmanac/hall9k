using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The human's unpark lever for the pre-PR review loop (Decisions Log #24 deferred it;
/// its absence left a parked run with no path forward except abandonment): record a
/// human verdict on a review-parked run. --merge-ready runs one mandatory full-scope
/// verification gate over the fix unless this tip was already gated at full scope
/// (log #98: nothing merges on scoped green alone) and sends the run on to its pull
/// request if that passes; --needs-fixes dispatches a fix
/// session with the stated reason as its findings and, like h9k pr resolve, re-bases the
/// review's per-track cycle caps on the cycle it resolved (log #63's ReviewBudgetBaseCycle)
/// — the human asking is a fresh grant, not one cycle before an immediate re-park. It does
/// not re-open the severity gate, which is a statement about how converged the diff is
/// rather than a budget.
/// <para>
/// One park takes merge-ready differently. The thread-dispute park (Decisions Log #62) is
/// raised before the gates run, so there is no reviewed diff to sign off: the verdict
/// settles the disputed thread, and the run re-enters the pipeline at the gates and the
/// review loop rather than proceeding straight to the pull request. The message says so.
/// </para>
/// <para>
/// A rebase-conflict dispute (backlog 44) refuses merge-ready outright rather than taking it
/// the same way: nothing has been rebased, so there is no sense in which the branch is
/// "ready" — every path forward needs the human's actual resolution, which only
/// --needs-fixes carries. The refusal is scoped to that specific park (the task's follow-up
/// is a rebase AND no review pass has ever run, <see cref="RunAggregate.ReviewCycle"/> still 0)
/// rather than to the task's FollowUpKind alone, which stays Rebase for the rest of the run: an
/// ordinary review park later in that same rebase follow-up — the branch already rebased, the
/// gates already green, at least one review cycle behind it — is exactly the park --merge-ready
/// exists for. ReviewCycle, not <see cref="RunAggregate.ParkedFromState"/>, is what the refusal
/// keys on: ParkedFromState is captured from the run's State at park time, and a resumed dispute
/// that disputes again parks from UnderReview rather than Verifying (the fix session that resumed
/// it already moved State on), so it stops reading as "before the gates" the moment the dispute
/// resumes even though nothing has actually been rebased yet. ReviewCycle carries no such state
/// to go stale — it is untouched by the whole dispute-and-resolve round trip. The outcome message
/// printed below the append, by contrast, still reads <see cref="RunAggregate.ParkedFromState"/>
/// rather than ReviewCycle: it describes what <see cref="RunAggregate.Apply(Hall9k.Domain.Features.Run.Events.ReviewParkResolved)"/>
/// itself will do, and that method's own branch is still keyed on ParkedFromState, stale reads and
/// all. Keying the message on ReviewCycle would make it lie about a second pre-gate dispute's
/// outcome instead of the refusal preventing one; that mismatch inside RunAggregate is a real,
/// recorded gap (backlog 64), not this command's to fix.
/// </para>
/// <para>
/// A changes-requested disagreement park (task: a changes-requested pull-request review from a
/// human becomes a fix lap) takes a verdict like any other park, but not a verdict alone. The fix
/// lap disagreed with a person's finding and deliberately said nothing about it, so this command
/// is where that silence ends or is made permanent: exactly one of
/// <c>--post-reply-as-written</c>, <c>--post-reply "&lt;text&gt;"</c>, or <c>--post-nothing</c> is
/// required there and refused everywhere else. The posting happens here, in the CLI, before
/// anything is appended — the human is standing in front of it, so a refused post is something
/// they can answer for rather than a queued write that silently fails later. No agent and no
/// orchestrator ever posts a disagreement to a human reviewer (Brian's ruling, 2026-09-06 12:15).
/// </para>
/// </summary>
public sealed class ReviewResolveCommand : Hall9kAsyncCommand<ReviewResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--merge-ready")]
        [Description(
            "Your verdict: the diff is sound — one mandatory full-scope verification gate runs "
            + "over the fix unless this tip was already gated at full scope, then the pull "
            + "request opens if it passes "
            + "(on a thread-dispute park, which happens before the gates, it re-enters at the "
            + "gates and the review loop instead: your call settled the thread, not the diff. "
            + "Refused on a disputed rebase conflict — nothing has been rebased yet, so use "
            + "--needs-fixes with your resolution instead). On a pr-review task's park it means "
            + "something different: the findings report has been walked and directed, and the "
            + "task completes with no diff, pull request, or merge of its own — the only verdict "
            + "a pr-review task takes.")]
        public bool MergeReady { get; init; }

        [CommandOption("--needs-fixes <REASON>")]
        [Description(
            "Your verdict: the stated defects are real — a fix session is dispatched with this "
            + "reason as its findings. Refused on a pr-review task's park: it reviews someone "
            + "else's pull request read-only, so there is no diff of its own for a fix session "
            + "to apply — direct the findings report by hand instead, then resolve with "
            + "--merge-ready.")]
        public string? NeedsFixes { get; init; }

        [CommandOption("--reason <TEXT>")]
        [Description(
            "Why the diff is sound despite the finding — e.g. the evidence that dismissed it. Only valid "
            + "with --merge-ready (--needs-fixes already takes its reason as its own argument). Recorded on "
            + "the task so a later fresh-context review pass is told this was already settled, rather than "
            + "re-raising the same question — except on a thread-dispute park, which settles a disputed "
            + "thread rather than a review finding and is not carried forward this way "
            + "(PLAN.md log #24, task: review prompts carry prior rulings).")]
        public string? Reason { get; init; }

        [CommandOption("--post-reply-as-written")]
        [Description(
            "Only on a changes-requested disagreement park: post the reply the fix session drafted, "
            + "verbatim, under your own login — in the disputed finding's own review thread, or as a "
            + "top-level pull-request comment when what was disputed was the review's own body "
            + "(GitHub makes a review body unthreadable). Read it first: nothing has reached the "
            + "reviewer yet, and this is what they will see. Refused when the target the fix "
            + "session named is not one closeout read on this pull request — reply by hand there "
            + "and resolve with --post-nothing instead.")]
        public bool PostReplyAsWritten { get; init; }

        [CommandOption("--post-reply <TEXT>")]
        [Description(
            "Only on a changes-requested disagreement park: post THIS text instead of the drafted "
            + "reply, same target. Refused when the park holds more than one disagreement — there "
            + "would be no way to say which one your text answers; use --post-nothing and reply by "
            + "hand in that case.")]
        public string? PostReply { get; init; }

        [CommandOption("--post-nothing")]
        [Description(
            "Only on a changes-requested disagreement park: say nothing to the reviewer. The run "
            + "continues on your verdict and the drafted reply is never sent. The honest choice "
            + "when the disagreement is not worth their time, or when you would rather say it "
            + "yourself, elsewhere, in your own words.")]
        public bool PostNothing { get; init; }

        /// <summary>
        /// How many of the three reply choices were passed. Exactly one is required on a
        /// changes-requested disagreement park and none is accepted on any other park — but which
        /// park this is cannot be known until the run is read, so this counts them here and
        /// <see cref="ExecuteAsync"/> enforces the rest.
        /// </summary>
        internal int ReplyChoiceCount =>
            (PostReplyAsWritten ? 1 : 0) + (PostReply.IsNotBlank() ? 1 : 0) + (PostNothing ? 1 : 0);

        public override ValidationResult Validate()
        {
            if (ReplyChoiceCount > 1)
            {
                return ValidationResult.Error(
                    "Pass at most one reply choice: --post-reply-as-written, --post-reply <text>, or " +
                    "--post-nothing. They are three answers to one question — what the reviewer hears.");
            }

            if (MergeReady == NeedsFixes.IsNotBlank())
            {
                return ValidationResult.Error(
                    "Pass exactly one verdict: --merge-ready, or --needs-fixes <reason>. " +
                    "The park exists because the platform refused to guess; this command records " +
                    "YOUR judgment (PLAN.md log #24).");
            }

            return Reason.IsNotBlank() && NeedsFixes.IsNotBlank()
                ? ValidationResult.Error(
                    "Pass --reason only with --merge-ready; --needs-fixes already takes its reason as its " +
                    "own argument.")
                : ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        return await ResolveAsync(session, taskId, settings, new GitHubReviewReplies(), cancellationToken);
    }

    /// <summary>
    /// The whole verdict, over a session and a resolved task id the caller supplies — internal so
    /// the resolve rules are testable against a real store without going through
    /// <see cref="CliStore.Open"/>'s ambient connection, the same seam
    /// <see cref="ResolvePrReviewAsync"/> already exists for (test: changes-requested resolve
    /// coverage). <paramref name="replies"/> is the provider write the three reply choices go
    /// through, injectable for the identical reason.
    /// </summary>
    internal static async Task<int> ResolveAsync(
        IDocumentSession session, Guid taskId, Settings settings, GitHubReviewReplies replies,
        CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run — nothing is review-parked here.");

        // Fence before aggregating (the h9k pr resolve manner): the append below carries
        // expectedVersion so a resolve racing the daemon (or a duplicate invocation)
        // loses loudly instead of stacking a second verdict.
        StreamState? fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");

        if (run.State != RunState.ReviewParked)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s current run is {run.State.Value}, not ReviewParked — only a " +
                "review-parked run takes a human verdict. (A parked pull request is resolved " +
                "with h9k pr resolve instead.)");
        }

        // The three reply choices belong to exactly one park, and refusing them elsewhere is not
        // pedantry: they name a reviewer's thread on a pull request, and on any other park there
        // is no drafted reply and no disputed finding for them to point at. Checked ahead of the
        // pr-review branch below rather than after it, so a choice passed against a pr-review
        // task's park is refused rather than silently ignored — that park can never be a
        // disagreement (a pr-review task writes to no pull request at all), which makes silence
        // there the same lie as silence anywhere else.
        if (settings.ReplyChoiceCount > 0 && !run.ParkedOnReviewDisagreement)
        {
            throw new DomainValidationException(
                $"Task {taskId}'s park is not a changes-requested disagreement, so there is no drafted "
                + "reply to post, edit, or withhold. The reply choices (--post-reply-as-written, "
                + "--post-reply, --post-nothing) apply only to a park where a fix lap disagreed with a "
                + "human reviewer's finding and deliberately said nothing about it.");
        }

        if (task.Type == TaskType.PrReview)
        {
            return await ResolvePrReviewAsync(session, runId, taskId, fence, settings, cancellationToken);
        }

        if (run.ParkedOnReviewDisagreement && settings.ReplyChoiceCount == 0)
        {
            throw new DomainValidationException(
                $"Task {taskId} is parked because a fix lap disagreed with a reviewer's finding and posted "
                + "nothing — that reply is yours to send, so say what the reviewer hears before the run "
                + "continues: --post-reply-as-written (send the drafted reply verbatim), --post-reply "
                + "\"<your text>\" (send yours instead), or --post-nothing. Read the draft first: "
                + "h9k task show names the file it was saved to.");
        }

        // !run.ParkedIsInteractiveGate excludes interactive mode's own routine boundary park
        // (task: interactive mode becomes a recorded property of the task): a rebase follow-up
        // that rebased cleanly, passed its gates, and parked at the "build done to review"
        // boundary also carries FollowUpKind.Rebase with ReviewCycle == 0, but nothing there is
        // disputed — without the exclusion this refused a --merge-ready that should instead
        // re-enter the pipeline at the gates and dispatch cycle 1's review (RunAggregate.Apply
        // (ReviewParkResolved), as of this task's own cycle 1 fix), with a message describing a
        // conflict that does not exist (independent pre-PR review, cycle 1, both lenses).
        if (settings.MergeReady && task.FollowUpKind == FollowUpKind.Rebase
            && run.ReviewCycle == 0 && !run.ParkedIsInteractiveGate)
        {
            throw new DomainConflictException(
                $"Task {taskId} is parked on a disputed rebase conflict — merge-ready has no meaning " +
                "here, because nothing has been rebased yet and the branch still conflicts with its " +
                "base. Resolve with --needs-fixes \"<how to resolve the conflict>\" instead; the " +
                "follow-up applies your decision and retries the rebase.");
        }

        // The mandatory final pass's own pre-flight rebase (task: a run rebases its branch onto
        // the current base branch) refuses merge-ready for the identical reason the post-PR
        // rebase dispute above does — nothing has been rebased, so there is no diff to sign off —
        // but keyed on ParkedFromReviewPhase rather than FollowUpKind/ReviewCycle: this dispute
        // can land mid-run, at any review cycle, with no follow-up and no pull request open yet.
        if (settings.MergeReady && run.ParkedFromReviewPhase == ReviewPhase.RebaseRecoveryDisputed)
        {
            throw new DomainConflictException(
                $"Task {taskId} is parked on a disputed pre-final-pass rebase conflict — merge-ready " +
                "has no meaning here, because nothing has been rebased yet and the branch still " +
                "conflicts with its base. Resolve with --needs-fixes \"<how to resolve the conflict>\" " +
                "instead; a fresh recovery session applies your decision and retries the rebase.");
        }

        // Read before the append below, because SaveChangesAsync drives the inline
        // RunDetailsProjection (ProjectionLifecycle.Inline, MartenConfiguration.cs), whose own
        // Apply(ReviewParkResolved) clears ParkedReason to null the moment this resolve commits.
        // The outcome message's no-progress arm below quotes this text directly rather than
        // pointing the operator at h9k task show, which by the time they run it has nothing left
        // to name (independent pre-PR review, cycle 1) — the park reason itself already names the
        // exact cap or budget and the one correct lever for whichever level supplied it
        // (ReviewEngine.TakeoverCapParkReason), so repeating that judgment here would only risk
        // getting it wrong a second time (independent pre-PR review, cycle 1, conformance lens).
        RunDetails? runDetailsBeforeResolve = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        string parkedReasonBeforeResolve = runDetailsBeforeResolve?.ParkedReason.IsNotBlank() == true
            ? runDetailsBeforeResolve.ParkedReason!
            : "no reason was recorded for this park";

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // The run is no longer parked, so the sweep's parked-run shield no longer covers
        // this lease; a fresh heartbeat holds the task while the daemon wakes. Read HERE, ahead of
        // the provider write below, rather than after the append where it once sat: it is a live
        // database round-trip, so a dropped connection there threw past the post with nothing on
        // the stream — and past the catch that exists to name an already-posted reply, leaving the
        // operator's natural retry to post the identical reply a second time under their own login
        // (self-review, this task). The store itself is in-memory until SaveChangesAsync, so
        // moving the pair up changes nothing about what commits.
        TaskLease? lease = await session.LoadAsync<TaskLease>(taskId, cancellationToken);
        if (lease is not null)
        {
            lease.HeartbeatAt = DateTimeOffset.UtcNow;
            session.Store(lease);
        }

        // The provider write goes LAST among everything that can fail before the commit, and that
        // ordering is the point: every refusal above, the bootstrap read, and the lease read that
        // precede it once sat between the post and the append, so any of them throwing left a reply
        // the reviewer had already read with nothing on the stream saying so (independent pre-PR
        // review, cycle 1, both lenses; the lease read, self-review, this task). Nothing but
        // SaveChangesAsync itself can fail after this line now, and that one remaining window is
        // reported with the posted targets named (below).
        IReadOnlyList<ReplyOutcome> repliesDirected = run.ParkedOnReviewDisagreement
            ? await DirectDisagreementRepliesAsync(session, task, run, settings, replies, cancellationToken)
            : [];

        ReviewVerdict verdict = settings.MergeReady ? ReviewVerdict.MergeReady : ReviewVerdict.NeedsFixes;
        // Normalized so the stored event agrees with how the rest of the pipeline reads it:
        // prompt rendering and echo-stripping both treat a blank reason as "none recorded".
        string? reason = settings.MergeReady ? settings.Reason : settings.NeedsFixes;
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        // One transaction, the directions first: the record of what the reviewer was told must
        // never land without the resolve that follows it, and a reader of this stream should see
        // the replies directed before the run moved on. expectedVersion counts every event, so a
        // resolve racing the daemon still loses loudly rather than half-landing.
        DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;
        object[] appended =
        [
            .. repliesDirected.Select(directed => (object)new ReviewDisagreementReplyDirected(
                runId, directed.Choice, directed.PostedBody, directed.PostedTarget, resolvedAt, context.OwnerId)),
            new ReviewParkResolved(runId, verdict, reason, resolvedAt, context.OwnerId),
        ];
        session.Events.Append(runId, expectedVersion: fence.Version + appended.Length, appended);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        // A cancellation is caught too once a reply has posted, and deliberately: Ctrl+C in this
        // window leaves the reviewer already read and the run still parked, so an operator handed a
        // bare cancellation re-runs the same reply choice and posts the identical reply a second
        // time — the very double-post this arm exists to prevent (independent pre-PR review, cycle
        // 1, adversarial finding). With nothing posted it still propagates untouched, so an
        // ordinary Ctrl+C on an ordinary park closes the way Program.cs's own handler closes it.
        catch (Exception exception) when (exception is EventStreamUnexpectedMaxEventIdException
            || repliesDirected.Any(r => r.PostedTarget is not null))
        {
            // The append is one transaction, so a failure here records nothing — but any reply has
            // already reached the reviewer, and the retry advice cannot be given without saying so:
            // an operator who re-runs the same reply choice against a still-parked run posts the
            // identical reply a second time under their own login, and GitHub offers no idempotency
            // key on a review reply to catch it (independent pre-PR review, cycle 1, both lenses).
            // Named the same way DirectDisagreementRepliesAsync names its own partial failure, and
            // steered to the same place: --post-nothing, once the run is confirmed still parked.
            string[] posted = [.. repliesDirected.Select(reply => reply.PostedTarget).OfType<string>()];
            // Silent on posting when no reply was ever in play (an ordinary park), rather than
            // reassuring an operator about a question they did not ask.
            string already = (posted.Length, repliesDirected.Count) switch
            {
                ( > 0, _) => $" Already posted, and NOT recorded on the run: {string.Join(", ", posted)}. "
                    + "Do NOT re-run with a reply choice — the reviewer would hear the same thing twice; "
                    + "re-run with --post-nothing instead.",
                (0, > 0) => " Nothing was posted, so the reviewer has heard nothing.",
                _ => "",
            };
            string cause = exception is EventStreamUnexpectedMaxEventIdException
                ? $"Run {runId} changed while resolving — the daemon (or another resolve) got there first."
                : $"Recording the resolve of run {runId} failed: {exception.Message}";
            throw new DomainConflictException(
                $"{cause} The verdict was not recorded.{already} Check h9k status; re-run this "
                + "command only if the run is still ReviewParked.");
        }
        await Doorbell.RingAsync($"review-resolve:{taskId}", cancellationToken);

        // Printed above the outcome, because it is the part that reached another person: what the
        // reviewer now sees, or that they deliberately see nothing.
        foreach (ReplyOutcome directed in repliesDirected)
        {
            if (directed.PostedTarget is not { } target)
            {
                AnsiConsole.MarkupLine(
                    "[dim]Nothing was posted — the reviewer has heard nothing about the disagreement.[/]");
                continue;
            }

            string which = directed.Choice == ReviewDisagreementReplyChoice.Edited
                ? "your own reply"
                : "the drafted reply";
            AnsiConsole.MarkupLineInterpolated($"[dim]Posted {which} to {target}, under your login.[/]");
        }

        // What happens next differs by where the park caught the run, so say which — and this
        // has to read RunAggregate.ParkedFromState, not ReviewCycle, even though the refusal
        // guard above reads ReviewCycle: Apply(ReviewParkResolved) itself still branches on
        // ParkedFromState == RunState.Verifying (RunAggregate.cs), so a message keyed on
        // ReviewCycle would describe a re-enter-at-the-gates outcome on a second pre-gate
        // dispute that the aggregate actually settles straight to the pull request. That
        // mismatch between the aggregate's two conditions is a real, recorded gap (backlog 64);
        // until it's fixed, the honest message is the one that matches what
        // Apply(ReviewParkResolved) will actually do — which, as of this task's own cycle 1 fix,
        // no longer carves the interactive gate out of the dispute path (independent pre-PR
        // review, cycle 1, adversarial lens): an interactive-gate park also carries
        // ParkedFromState == Verifying, with cycle 1's review not yet dispatched either, so the
        // aggregate re-enters at the gates there too rather than settling unreviewed — this
        // message now agrees, without needing run.ParkedIsInteractiveGate as a second condition.
        bool rebaseRecoveryDispute = run.ParkedFromReviewPhase == ReviewPhase.RebaseRecoveryDisputed;
        FormattableString outcome = (settings.MergeReady, run.ParkedFromState == RunState.Verifying) switch
        {
            (true, true) =>
                $"[dim]Run {runId} resolved merge-ready — the daemon re-enters the pipeline at the gates, then review; the pull request opens if both pass.[/]",
            (true, false) =>
                $"[dim]Run {runId} resolved merge-ready — the daemon runs one mandatory full-scope verification gate over the fix, unless this tip was already gated at full scope, then opens the pull request if it passes.[/]",
            (false, _) when run.ParkedNeedsFixesOffersNoProgress =>
                $"[dim]Run {runId} resolved needs-fixes — but this park's review-cycle cap or lifetime budget won't clear from a plain grant. The park itself already named the cap, its level, and the one lever that actually raises it: {parkedReasonBeforeResolve} Unless you raised it before running this command, the run re-parks behind this grant rather than settling — sometimes after one more fix session lands real work, sometimes before one ever dispatches.[/]",
            (false, _) when rebaseRecoveryDispute =>
                $"[dim]Run {runId} resolved needs-fixes — the daemon dispatches a fresh rebase-recovery session carrying your resolution.[/]",
            _ =>
                $"[dim]Run {runId} resolved needs-fixes — the daemon dispatches a fix session with your reason as its findings.[/]",
        };
        AnsiConsole.MarkupLineInterpolated(outcome);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What one reply actually said back to the reviewer — the choice, the text a provider write
    /// accepted, and the one place it landed. Both null on
    /// <see cref="ReviewDisagreementReplyChoice.Nothing"/>, which is the point of that choice.
    /// </summary>
    private readonly record struct ReplyOutcome(
        ReviewDisagreementReplyChoice Choice, string? PostedBody, string? PostedTarget);

    /// <summary>
    /// Sends what the implementer directed, before the resolve appends anything (task: a
    /// changes-requested pull-request review from a human becomes a fix lap). Posting comes first
    /// deliberately: a resolve that recorded the verdict and then failed to post would leave a
    /// stream saying the reviewer was answered when they were not, and this is the one place in
    /// the platform where the whole point is that nothing reaches a person without a human
    /// choosing it.
    /// <para>
    /// Each disagreement's reply goes where that disagreement points: inside its own review thread
    /// when it has one, and as a top-level pull-request comment when the disputed finding was the
    /// review's own body, which GitHub makes unthreadable. Nothing here ever opens a review thread
    /// (AGENTS.md).
    /// </para>
    /// <para>
    /// Where it points is the session's claim, not an observed fact — the thread id and review url
    /// are parsed out of its free-text summary — so both are checked against the reviews carried on
    /// this task's own reopen before anything is sent, and a target closeout never read is refused
    /// rather than posted to. See the check itself for why a wrong one is otherwise invisible.
    /// </para>
    /// <para>
    /// Every refusal this method can raise is raised BEFORE the first provider write, so a park
    /// holding two disagreements where only one carries a draft posts neither rather than posting
    /// one and then failing the resolve — a reply the reviewer has read with nothing on the stream
    /// saying so is the worst outcome available here, and it is not one an operator can undo.
    /// A write that the provider itself refuses can still land partway through several replies;
    /// that throws with the accepted targets named and records nothing, leaving the park
    /// unresolved so the operator can finish by hand and then resolve with <c>--post-nothing</c>.
    /// </para>
    /// <para>
    /// The caller holds the other half of that contract: it calls this only after its own last
    /// refusal and its last read, so the sole failure left between the post and the commit is
    /// <c>SaveChangesAsync</c> — which reports the posted targets in the same words this method
    /// does, because the advice to re-run is a double-post without them.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<ReplyOutcome>> DirectDisagreementRepliesAsync(
        IDocumentSession session, TaskAggregate task, RunAggregate run, Settings settings,
        GitHubReviewReplies replies, CancellationToken cancellationToken)
    {
        if (settings.PostNothing)
        {
            return [new ReplyOutcome(ReviewDisagreementReplyChoice.Nothing, null, null)];
        }

        IReadOnlyList<ReviewDisagreement> disagreements = run.ParkedDisagreements;
        if (disagreements.Count == 0)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s park recorded no readable disagreement, so there is no drafted reply to "
                + "post or edit — the fix session reported a disagreement without a parseable block. Read "
                + "its summary (h9k task show names the file), then resolve with --post-nothing and reply "
                + "by hand if the reviewer is owed one.");
        }

        if (settings.PostReply.IsNotBlank() && disagreements.Count > 1)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s park holds {disagreements.Count} disagreements, so a single --post-reply "
                + "text has no one place to go. Post each drafted reply as written "
                + "(--post-reply-as-written), or resolve with --post-nothing and reply by hand where you "
                + "want your own words.");
        }

        if (task.PullRequestUrl.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {task.Id} has no pull request recorded, so there is nowhere to post a reply. "
                + "Resolve with --post-nothing.");
        }

        // What the provider itself read off this pull request, which is the only trustworthy
        // account of where a reply may land. Every thread id and review url on a parked
        // disagreement came out of a fix session's free-text summary
        // (ReviewResultParser.ParseDisagreements' `thread=` and `review=` tags), so they are the
        // session's claim about the reviewer's own review rather than an observed fact — and the
        // thread id is what SELECTS where the implementer's reply lands, under their own login.
        // The reply mutation accepts any thread node id the token can reach, including one on a
        // different pull request, and answers success; the only target the implementer ever sees
        // is an opaque PRRT_… printed after the post has already landed. So a copied-from-the-
        // adjacent-finding or invented-but-valid id is checked against the reviews carried on this
        // task's own reopen here or never (independent pre-PR review, cycle 1, adversarial lens).
        // Same skepticism the parser already applies to the sibling `at=` tag, which it drops
        // outright when it echoes the contract's example placeholder.
        IReadOnlyList<ChangesRequestedFinding> reviewed =
            [.. task.ChangesRequestedReviews.SelectMany(review => review.Findings)];

        // Every body resolved and vetted before the first write, per this method's own contract.
        ReviewDisagreementReplyChoice choice = settings.PostReply.IsNotBlank()
            ? ReviewDisagreementReplyChoice.Edited
            : ReviewDisagreementReplyChoice.AsWritten;
        List<(ReviewDisagreement Disagreement, string Body)> planned = [];
        foreach (ReviewDisagreement disagreement in disagreements)
        {
            string body = choice == ReviewDisagreementReplyChoice.Edited
                ? settings.PostReply!.Trim()
                : disagreement.ProposedReply;
            if (body.IsBlank())
            {
                throw new DomainConflictException(
                    $"Task {task.Id}'s parked disagreement at "
                    + $"{(disagreement.Location.IsNotBlank() ? disagreement.Location : "the review's own body")} "
                    + "carries no drafted reply, so there is nothing to post as written. Nothing has been "
                    + "posted. Pass your own text with --post-reply \"<text>\", or resolve with "
                    + "--post-nothing.");
            }

            // Ordinal: a GraphQL node id is opaque and case-significant, so a near-match is a
            // different thread rather than the same one spelled differently.
            if (disagreement.ThreadId.IsNotBlank()
                && !reviewed.Any(finding =>
                    string.Equals(finding.ThreadId, disagreement.ThreadId, StringComparison.Ordinal)))
            {
                string[] known = [.. reviewed.Select(finding => finding.ThreadId).OfType<string>()];
                throw new DomainConflictException(
                    $"Task {task.Id}'s parked disagreement names review thread {disagreement.ThreadId}, "
                    + "which is not one of the threads the reviewer opened on this pull request — the fix "
                    + "session stated it, and closeout never read it, so posting there would send your "
                    + "reply somewhere the disputed finding is not. Nothing has been posted. The threads "
                    + $"this task's review actually left: {(known.Length > 0 ? string.Join(", ", known) : "none")}. "
                    + "Reply by hand in the right thread, then resolve with --post-nothing — which is also "
                    + "the answer when you can see the thread is genuinely the reviewer's: closeout's own "
                    + "thread read is capped at the pull request's first 100 threads, so a real thread past "
                    + "that cap is unverifiable here rather than wrong.");
            }

            if (disagreement.ThreadId.IsBlank() && disagreement.ReviewUrl.IsNotBlank()
                // Checked only on this branch, which is the only one that uses the url: a review
                // BODY is unthreadable, so its answer is a top-level comment that names the review
                // it answers in its own text. That text goes to a person under the implementer's
                // login, so an unverifiable url in it tells a real reviewer they are being answered
                // about a review that may not be theirs. On the thread branch above the url is
                // display-only (h9k task show groups by it), and refusing a post over a field the
                // post does not use would be refusing for nothing.
                // OrdinalIgnoreCase, the comparison this codebase already uses for provider-supplied
                // urls and logins, so casing alone never rejects a url closeout did read.
                && !task.ChangesRequestedReviews.Any(review =>
                    string.Equals(review.ReviewUrl, disagreement.ReviewUrl, StringComparison.OrdinalIgnoreCase)))
            {
                string[] known = [.. task.ChangesRequestedReviews.Select(review => review.ReviewUrl)];
                throw new DomainConflictException(
                    $"Task {task.Id}'s parked disagreement answers review {disagreement.ReviewUrl}, which is "
                    + "not one of the changes-requested reviews this lap was dispatched to answer — the fix "
                    + "session stated it, and closeout never read it, so a top-level comment naming it would "
                    + "tell the reviewer they are being answered about a review that may not be theirs. "
                    + "Nothing has been posted. The reviews this lap is answering: "
                    + $"{(known.Length > 0 ? string.Join(", ", known) : "none")}. Comment by hand on the right "
                    + "review, then resolve with --post-nothing.");
            }

            planned.Add((disagreement, body));
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"No project {task.ProjectId} — cannot reach its repository to post.");
        int pullRequestNumber = PullRequestUrls.ParseNumber(task.PullRequestUrl);

        List<ReplyOutcome> directed = [];
        foreach ((ReviewDisagreement disagreement, string body) in planned)
        {
            try
            {
                if (disagreement.ThreadId.IsNotBlank())
                {
                    await replies.ReplyInThreadAsync(
                        project.RepositoryPath, disagreement.ThreadId, body, cancellationToken);
                    directed.Add(new ReplyOutcome(choice, body, disagreement.ThreadId));
                }
                else
                {
                    // The review's own body was disputed: unthreadable, so a top-level comment is
                    // the only answer that exists. The review it answers is named in the text so
                    // the reviewer does not have to connect it back themselves.
                    string named = disagreement.ReviewUrl.IsNotBlank()
                        ? $"On {disagreement.ReviewUrl}:\n\n{body}"
                        : body;
                    await replies.CommentAsync(project.RepositoryPath, pullRequestNumber, named, cancellationToken);
                    directed.Add(new ReplyOutcome(choice, named, task.PullRequestUrl));
                }
            }
            // Cancellation propagates untouched while nothing has posted, and is reported like any
            // other failure once something has: a multi-disagreement park cancelled part-way
            // through leaves the earlier replies already read, and an operator not told so re-runs
            // the same choice and posts them again — the sibling of the SaveChangesAsync window's
            // own arm above (self-review, this task).
            catch (Exception exception) when (exception is not OperationCanceledException || directed.Count > 0)
            {
                string already = directed.Count == 0
                    ? "Nothing was posted."
                    : "Already posted, and NOT recorded on the run: "
                        + $"{string.Join(", ", directed.Select(reply => reply.PostedTarget))}.";
                throw new DomainConflictException(
                    $"Posting the reply for task {task.Id} failed, so the park is left unresolved rather than "
                    + $"recorded as answered. {already} {exception.Message} Finish by hand if the reviewer is "
                    + "owed a reply, then resolve with --post-nothing.");
            }
        }

        return directed;
    }

    /// <summary>
    /// A pr-review task's own verdict shape: there is no diff of this task's own to fix or
    /// re-review, so --needs-fixes has nothing to dispatch a fix session over and is refused
    /// outright. --merge-ready instead means "the findings report has been walked and
    /// directed" — records <see cref="PrReviewDelivered"/>, which moves the run to
    /// UnderReview exactly as ReviewParkResolved does so the daemon's own resume sweep picks
    /// it up, but PrReviewEngine finalizes it directly (task Done, no merge ever observed)
    /// rather than re-entering any review loop.
    /// <para>Internal so the pr-review verdict rules are testable against a real store without going through <see cref="CliStore.Open"/>'s ambient connection (test: pr-review resolve coverage).</para>
    /// </summary>
    internal static async Task<int> ResolvePrReviewAsync(
        IDocumentSession session, Guid runId, Guid taskId, StreamState fence, Settings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NeedsFixes.IsNotBlank())
        {
            throw new DomainValidationException(
                $"Task {taskId} is a pr-review task: it reviews someone else's pull request read-only, "
                + "so there is nothing here for a fix session to apply. Direct the findings yourself "
                + "(dismiss, comment, or have a session post on your behalf), then resolve with "
                + "--merge-ready when you are done.");
        }

        if (!settings.MergeReady)
        {
            throw new DomainValidationException(
                "Pass --merge-ready once the findings report has been walked and directed — the only "
                + "verdict a pr-review task takes.");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        string? reason = string.IsNullOrWhiteSpace(settings.Reason) ? null : settings.Reason.Trim();
        session.Events.Append(runId, expectedVersion: fence.Version + 1, new PrReviewDelivered(
            runId, reason, DateTimeOffset.UtcNow, context.OwnerId));

        TaskLease? lease = await session.LoadAsync<TaskLease>(taskId, cancellationToken);
        if (lease is not null)
        {
            lease.HeartbeatAt = DateTimeOffset.UtcNow;
            session.Store(lease);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Run {runId} changed while resolving — the daemon (or another resolve) got there " +
                "first. Check h9k status; re-run this command only if the run is still ReviewParked.");
        }

        await Doorbell.RingAsync($"review-resolve:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Run {runId} resolved — the daemon completes the task. Nothing was posted to the pull request.[/]");
        return ExitCodes.Ok;
    }
}
