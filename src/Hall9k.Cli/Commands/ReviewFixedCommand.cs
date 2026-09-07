using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Worktrees;
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
/// The human takes the fix role (task: a human at the wheel takes the fix role herself, Take the
/// Wheel epic): at interactive mode's review-verdict-to-fix boundary the human fixes the findings
/// in the run's own worktree, commits, and hands the branch back for re-review with this one
/// verb — the review agents then check that fix exactly the way they would check a fix session's,
/// and no headless fix agent runs unless they ask for one.
/// <para>
/// The fourth choice at that park, never a change to the other three (design ruling R9, restated
/// as this task's own third criterion): <c>h9k review proceed</c> still dispatches a fix session,
/// <c>h9k review resolve --needs-fixes "&lt;redirect&gt;"</c> still dispatches one carrying their
/// redirect, and <c>h9k review resolve --merge-ready</c> still overrules the finding outright.
/// This verb is a separate word rather than a flag on <c>proceed</c> on purpose (Brian's ruling,
/// 2026-09-07): <c>proceed</c> means "send the agent", and a flag that flips a verb's meaning is
/// what a teammate types wrong at 5 pm.
/// </para>
/// <para>
/// It records that the fix is on the branch, pushes when the branch is already published, and
/// re-enters the loop at the existing fix-to-re-review boundary
/// (<see cref="ReviewPhase.Reverify"/>) — so the gates run over those commits and the next review
/// pass reads them scoped to the parked cycle's own head, exactly as a fix session's commits would
/// have been read. It refuses over an uncommitted worktree, naming the files, and refuses when the
/// branch tip has not moved since the park unless <c>--no-change "&lt;why&gt;"</c> says why; that
/// reason is recorded and carried into every later fresh-context review pass the way an
/// <c>h9k review resolve</c> reason is. The refusal runs both ways: <c>--no-change</c> over a tip
/// the command just watched move is refused too, because the commits and the dismissal are
/// mutually exclusive answers and recording both would tell every later pass that a cycle whose
/// commits are sitting in the diff it is reading changed nothing.
/// </para>
/// <para>
/// The same lever works at a closeout-side fix park — a follow-up run reopened by a human's
/// changes-requested review or by failing checks — because that park is the identical
/// <see cref="ReviewPhase.FixNeeded"/> interactive gate inside that follow-up's own review loop,
/// and this command keys on the park rather than on how the run came to exist.
/// </para>
/// </summary>
public sealed class ReviewFixedCommand : Hall9kAsyncCommand<ReviewFixedCommand.Settings>
{
    /// <summary>
    /// How many uncommitted paths a refusal names before it stops listing and says how many more
    /// there are. The point of naming them is that the operator can act on the message without a
    /// second command; a worktree with hundreds of dirty files needs the count, not the inventory.
    /// </summary>
    private const int MaxNamedFiles = 20;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--no-change <REASON>")]
        [Description(
            "Hand the branch back for re-review even though its tip has not moved since the park, "
            + "stating why you changed nothing — e.g. the evidence that showed the finding was "
            + "already handled. Without this, an unmoved tip is refused: the next review pass "
            + "reads your commits since the parked tip as the fix, and there would be nothing "
            + "there for it to read. Your reason is recorded on the task and carried into every "
            + "later fresh-context review pass the way an h9k review resolve reason is, so a pass "
            + "is told the question was already settled rather than re-raising it. It is only for "
            + "an unmoved tip: passing it when your commits DID land is refused, because a cycle "
            + "cannot both be fixed by those commits and be a deliberate no-change dismissal.")]
        public string? NoChange { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        return await RecordAsync(session, taskId, settings, cancellationToken);
    }

    /// <summary>
    /// Everything after the id resolution, so the whole rule set is reachable in a test against a
    /// real store without going through <see cref="CliStore.Open"/>'s ambient connection (the
    /// seam <c>ReviewResolveCommand.ResolvePrReviewAsync</c> already established for this file's
    /// siblings). Every git fact below is read from the run's real worktree through
    /// <see cref="InteractiveWorktreeGit"/> — there is no stub in the middle, so a test exercises
    /// the same porcelain parsing and the same push the operator's own invocation does.
    /// </summary>
    internal static async Task<int> RecordAsync(
        IDocumentSession session, Guid taskId, Settings settings, CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {taskId} has no current run — nothing is review-parked here.");

        // Fence before aggregating, the h9k review proceed/resolve manner: the append below carries
        // expectedVersion so a fix racing the daemon (or a duplicate invocation) loses loudly
        // instead of stacking a second answer onto the same boundary.
        StreamState? fence = await session.Events.FetchStreamStateAsync(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");
        RunAggregate run = await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no run stream.");

        if (run.State != RunState.ReviewParked)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s current run is {run.State.Value}, not ReviewParked — only a " +
                "review-parked run takes a fix. (A parked pull request is resolved with h9k pr resolve instead.)");
        }

        if (task.Type == TaskType.PrReview)
        {
            throw new DomainValidationException(
                $"Task {taskId} is a pr-review task: it reviews someone else's pull request read-only, so " +
                "there is no diff of its own for you to fix or for a review pass to re-read. Direct the " +
                "findings report by hand, then resolve with h9k review resolve --merge-ready.");
        }

        if (!run.ParkedIsInteractiveGate)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s park is not one of interactive mode's own routine boundaries — h9k review " +
                "fixed only answers one of those, and this park is a dispute or a cap/budget reason. " +
                "h9k review resolve --merge-ready or --needs-fixes \"<reason>\" is the lever for this park.");
        }

        // Scoped to the review-verdict-to-fix boundary specifically, not to every interactive gate:
        // this verb means "I did the fix the reviewer asked for", and that sentence has no meaning
        // at a boundary where nothing has asked for a fix. The other three boundaries already have
        // their own correct lever, and the message names it rather than leaving the operator to
        // guess (AGENTS.md's CLI standard: an agent must be able to self-correct from the message).
        if (run.ParkedFromReviewPhase != ReviewPhase.FixNeeded)
        {
            throw new DomainConflictException(
                $"Task {taskId} is parked at interactive mode's {BoundaryLabel(run.ParkedFromReviewPhase)} " +
                "boundary, not the review-verdict-to-fix one — there is no review verdict here asking for a " +
                $"fix. h9k review proceed {taskId} continues this boundary, or h9k review resolve to redirect it.");
        }

        RunDetails runDetails = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {runId} has no recorded details to read its worktree from.");
        if (runDetails.WorktreePath.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} records no worktree path, so there is no branch here to " +
                $"confirm your fix on. h9k task show {taskId} to see where this run stands.");
        }

        await EnsureOnTheClaimBranchAsync(taskId, runDetails, cancellationToken);
        await EnsureWorktreeCommittedAsync(taskId, runDetails.WorktreePath, cancellationToken);
        (string? headSha, TipObservation tip) =
            await EnsureTipAnswersTheFixAsync(taskId, run, runDetails, settings, cancellationToken);

        // Pushed before the append, never after: a push that fails leaves the run parked exactly as
        // it was, with the lever still available, where the reverse order would advance the loop
        // over a pull request the reviewers and CI still read at the stale tip. A pre-pull-request
        // park needs no push at all — nothing is published yet, and PullRequestOpener's own
        // force-with-lease push is what publishes this branch when the loop reaches it.
        bool pushed = false;
        if (task.PullRequestUrl.IsNotBlank())
        {
            (bool succeeded, string pushError) = await InteractiveWorktreeGit.PushAsync(
                runDetails.WorktreePath, runDetails.Branch, cancellationToken);
            if (!succeeded)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s pull request is already open, so your fix has to reach origin before " +
                    $"the review agents and its checks read it — and the push failed: {pushError} Nothing was " +
                    "recorded; the run is still parked, so fix the push and run this again.");
            }

            pushed = true;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // Keyed on what the tip check actually observed, never on the flag alone: the guard above
        // has already refused --no-change over a tip it watched move, so a reason surviving to here
        // belongs to a tip that did not move or to one nothing could compare — and those are the
        // only two the record is allowed to claim (never guess at unobserved facts).
        string? noChangeReason = tip is not TipObservation.Moved && settings.NoChange.IsNotBlank()
            ? settings.NoChange.Trim()
            : null;
        session.Events.Append(runId, expectedVersion: fence.Version + 1, new ReviewHumanFixApplied(
            runId, run.ReviewCycle, headSha, noChangeReason, pushed, DateTimeOffset.UtcNow, context.OwnerId));

        // The run is no longer parked, so the sweep's parked-run shield no longer covers this
        // lease; a fresh heartbeat holds the task while the daemon wakes (mirrors h9k review
        // proceed and h9k review resolve).
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
                $"Run {runId} changed while recording your fix — the daemon (or another proceed/resolve/fixed) " +
                "got there first. Check h9k status; re-run this command only if the run is still ReviewParked.");
        }

        await Doorbell.RingAsync($"review-fixed:{taskId}", cancellationToken);
        string pushNote = pushed ? " and pushed to origin" : string.Empty;

        // Says what was observed rather than what the flag implies (independent pre-PR review,
        // cycle 1, both lenses): an unobserved tip is reported as unobserved, never narrated as
        // unmoved just because a --no-change reason is riding along with it.
        FormattableString outcome = (noChangeReason is null, tip) switch
        {
            (true, _) =>
                $"[dim]Task {taskId}'s fix is recorded on {runDetails.Branch}{pushNote} — the daemon runs the gates over your commits, then a fresh review pass reads them as this cycle's fix. No fix session was dispatched.[/]",
            (false, TipObservation.Unmoved) =>
                $"[dim]Task {taskId} handed back with its tip unmoved{pushNote} — your reason is recorded and rides into every later review pass. The daemon re-enters the loop at the fix-to-re-review boundary. No fix session was dispatched.[/]",
            _ =>
                $"[dim]Task {taskId} handed back with your no-change reason recorded{pushNote} — nothing here could compare its tip against the parked cycle's head, so whether it moved is unknown, not unmoved. The reason rides into every later review pass, and the daemon re-enters the loop at the fix-to-re-review boundary. No fix session was dispatched.[/]",
        };
        AnsiConsole.MarkupLineInterpolated(outcome);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The worktree has to actually be on the claim's own branch before anything below means what
    /// it says — the identical guard <c>h9k task deliver</c> holds, for the identical reason and
    /// with the same origin incident behind it (adversarial review, cycle 4, on that command): an
    /// operator who left this worktree checked out somewhere else, on another branch or detached
    /// at a commit that happens to build, sails past every other check here. The tree reads clean,
    /// HEAD reads moved (it is a different commit), and then the three things that follow all read
    /// something different from each other — the push publishes <c>run.Branch</c>, which may not
    /// hold the fix at all, while the reverify gate and the next review pass read the worktree's
    /// actual HEAD. Refused rather than warned, because there is no version of this that ends with
    /// the reviewers reading the fix. Git being unreadable warns and skips, the same
    /// never-guess degradation every other check here takes.
    /// </summary>
    private static async Task EnsureOnTheClaimBranchAsync(
        Guid taskId, RunDetails runDetails, CancellationToken cancellationToken)
    {
        string? currentBranch = await InteractiveWorktreeGit.GetCurrentBranchAsync(
            runDetails.WorktreePath, cancellationToken);
        if (currentBranch is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's current branch at {runDetails.WorktreePath}; skipping the branch-checkout check.[/]");
            return;
        }

        if (currentBranch == runDetails.Branch)
        {
            return;
        }

        // Empty, not null, is git's own answer for a detached HEAD (GetCurrentBranchAsync's own
        // doc): a real answer about where the worktree is, so it is named rather than treated as
        // unreadable.
        string where = currentBranch.Length == 0 ? "a detached commit" : $"'{currentBranch}'";
        throw new DomainConflictException(
            $"Task {taskId}'s worktree is checked out to {where}, not its claim branch " +
            $"'{runDetails.Branch}' — whatever you committed is not on the branch the review agents " +
            $"and the gates will read. Check out '{runDetails.Branch}' and run this again.");
    }

    /// <summary>
    /// The uncommitted-files refusal (this task's own second criterion). Split exactly the way
    /// <c>h9k task deliver</c> and <c>VerificationRunner</c> already split it, through the same
    /// shared classification: a modified or staged tracked file is strandable work, an untracked
    /// file under src/ or tests/ is too (the daemon's own pre-gate check fails the run over one),
    /// and an untracked path elsewhere can legitimately be a gate byproduct, so it warns rather
    /// than refuses. Git being unreadable is never guessed at as clean — it says the check was
    /// skipped instead (<see cref="InteractiveWorktreeGit.ListUncommittedFilesAsync"/>'s own
    /// contract).
    /// </summary>
    private static async Task EnsureWorktreeCommittedAsync(
        Guid taskId, string worktreePath, CancellationToken cancellationToken)
    {
        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(worktreePath, cancellationToken);
        if (modified is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {worktreePath}; skipping the uncommitted-files check.[/]");
            return;
        }

        (IReadOnlyList<string> strandable, IReadOnlyList<string> byproduct) =
            WorktreeGitStatus.SplitUntracked(untracked);
        IReadOnlyList<string> blocking = [.. modified, .. strandable];
        if (blocking.Count > 0)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s worktree still holds uncommitted work, so there is nothing on the branch " +
                "for the review agents to read as your fix. Commit it (or discard it) and run this again. " +
                $"Uncommitted: {Named(blocking)}");
        }

        if (byproduct.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Untracked file(s) in the worktree, left alone as build byproducts (not blocking): {string.Join(", ", byproduct)}[/]");
        }
    }

    /// <summary>
    /// What the branch tip was observed to be doing relative to the parked cycle's own head. An
    /// enum rather than a value object per TASK-MODEL.md §8: never persisted, and it leaves this
    /// file only as a rendering-and-refusal decision inside one command.
    /// </summary>
    private enum TipObservation
    {
        /// <summary>Commits landed since the park — the commits are this cycle's answer.</summary>
        Moved,

        /// <summary>The tip is exactly the parked cycle's head — only <c>--no-change</c> gets past that.</summary>
        Unmoved,

        /// <summary>
        /// Neither could be established: git could not be asked, or the cycle recorded no head of
        /// its own. Never collapsed into either of the two above — an unknown tip is said out loud
        /// (AGENTS.md's never-guess rule).
        /// </summary>
        Unobserved,
    }

    /// <summary>
    /// The two-way tip refusal and the <c>--no-change</c> override between them (this task's own
    /// second criterion). The boundary compared against is <see cref="RunAggregate.CycleHeadSha"/> —
    /// the parked cycle's own head — deliberately rather than a separately-recorded park-time tip:
    /// it is the exact same value the next review pass's diff instruction scopes to
    /// (<c>ReviewEngine</c>'s Verify reverify), and nothing commits to the worktree between a
    /// cycle's dispatch and its verdict landing, so the two can never disagree about what "since
    /// the park" means. Keying the refusal on anything else would let this command accept a fix
    /// the review pass then reads as an empty diff.
    /// <para>
    /// An unmoved tip is refused unless <c>--no-change "&lt;why&gt;"</c> says why, and a MOVED tip
    /// is refused when <c>--no-change</c> is passed anyway (independent pre-PR review, cycle 1,
    /// both lenses): the two are mutually exclusive answers to the same cycle, and accepting both
    /// recorded a dismissal — "the finding was read, nothing was deliberately changed" — over a
    /// cycle whose human-authored commits are sitting in the very diff the next pass reads, while
    /// dropping the instruction to hold those commits to a fix session's bar. Refused rather than
    /// reconciled, because the flag's own contract is the unmoved-tip case and a finding that
    /// needed no code belongs in the commit message the reviewers read beside the fix.
    /// </para>
    /// <para>
    /// Returns the observed tip for the event to record — null when git could not be asked at all —
    /// alongside what was actually observed about it, so the caller records and reports that rather
    /// than inferring it from the flag.
    /// </para>
    /// </summary>
    private static async Task<(string? HeadSha, TipObservation Tip)> EnsureTipAnswersTheFixAsync(
        Guid taskId, RunAggregate run, RunDetails runDetails, Settings settings, CancellationToken cancellationToken)
    {
        string? headSha = await InteractiveWorktreeGit.GetHeadShaAsync(runDetails.WorktreePath, cancellationToken);
        if (headSha is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's HEAD at {runDetails.WorktreePath}; skipping the moved-tip check. The recorded fix names no commit.[/]");
            return (null, TipObservation.Unobserved);
        }

        if (run.CycleHeadSha is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Review cycle {run.ReviewCycle} recorded no head commit of its own, so there is no parked tip to compare against; skipping the moved-tip check.[/]");
            return (headSha, TipObservation.Unobserved);
        }

        if (headSha != run.CycleHeadSha)
        {
            if (settings.NoChange.IsNotBlank())
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s branch '{runDetails.Branch}' has moved to {Short(headSha)} since review " +
                    $"cycle {run.ReviewCycle} was parked at {Short(run.CycleHeadSha)}, so --no-change does not " +
                    "describe what happened here — your commits do, and recording both would tell every later " +
                    "review pass that this cycle deliberately changed nothing while your commits sit in the diff " +
                    $"it is reading. Run h9k review fixed {taskId} without --no-change; if one of the cycle's " +
                    "findings needed no code, say so in the commit message the next review pass reads beside " +
                    "your fix.");
            }

            return (headSha, TipObservation.Moved);
        }

        if (settings.NoChange.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {taskId}'s branch '{runDetails.Branch}' is still at {Short(headSha)}, the same tip review " +
                $"cycle {run.ReviewCycle} was parked at — there are no commits of yours for the next review pass " +
                "to read as the fix, and it would be handed an empty diff. Commit your fix and run this again, " +
                $"or, if you deliberately changed nothing, say why: h9k review fixed {taskId} --no-change " +
                "\"<why the finding needed no change>\".");
        }

        return (headSha, TipObservation.Unmoved);
    }

    /// <summary>
    /// Which interactive-mode boundary a park caught the run at, in the same words AGENTS.md and
    /// the operations guide name the four by, so the refusal above and the docs read alike. Any
    /// other phase is named by the phase itself rather than given a guessed boundary label: only
    /// four of them are ever an interactive gate, and the guard above has already established that
    /// this park is one, so the fallback is defensive rather than expected.
    /// </summary>
    private static string BoundaryLabel(ReviewPhase parkedFrom) => parkedFrom switch
    {
        ReviewPhase.None => "build-done-to-review",
        // The mandatory final full pass is one more review dispatch, so its own gate is the
        // fix-to-re-review boundary reached from Settling rather than Reverify (ReviewEngine's own
        // comment at that call site says the same).
        ReviewPhase.Reverify or ReviewPhase.Settling => "fix-to-re-review",
        ReviewPhase.MergeReady => "gates-to-pull-request",
        _ => $"'{parkedFrom}'",
    };

    private static string Named(IReadOnlyList<string> files) =>
        files.Count <= MaxNamedFiles
            ? string.Join(", ", files)
            : $"{string.Join(", ", files.Take(MaxNamedFiles))} (and {files.Count - MaxNamedFiles} more)";

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
