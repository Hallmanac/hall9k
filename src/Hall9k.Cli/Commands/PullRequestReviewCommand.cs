using System.ComponentModel;
using Hall9k.Connectors.Processes;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A human reviewer's own review lap over somebody else's pull request (Decisions Log #149),
/// built ON TOP of the pr-review task the platform already adopts rather than beside it: this
/// command finds the pr-review task this node already holds for the pull request — auto-adopted
/// from a GitHub reviewer assignment, or minted by hand with <c>h9k task add --from-pr</c> —
/// attaches to it, reuses its read-only worktree, and opens an interactive session with a
/// factual briefing that includes the platform's own findings report when one exists. Only when
/// no live task exists at all does it adopt the pull request itself, through the same adoption
/// path both of those already use.
/// <para>
/// The lap never ends on its own. It ends when the reviewer runs <c>h9k pr approve</c> or
/// <c>h9k pr request-changes</c>, which posts the GitHub review under their own login and
/// finalizes the task — replacing the <c>h9k review resolve --merge-ready</c> ceremony for
/// somebody who is actually reviewing rather than merely acknowledging a report.
/// </para>
/// <para>
/// <b>The connector is the prompt handoff</b>, the same shape <c>h9k task work</c> settled on
/// (PLAN.md §16 #126): this command prepares the lap and prints the briefing for the reviewer to
/// paste into a Claude Code session they start wherever suits them. <c>--direct-launch</c> is not
/// offered here at all — an opening briefing carries a findings report verbatim, so it is far
/// longer and more newline-dense than a work prompt, and the cmd.exe embedded-newline problem
/// that made <c>--direct-launch</c> refusable on Windows for a work prompt
/// (<see cref="TaskWorkCommand"/>) applies with more force, not less. The push guard travels with
/// the checkout instead of with an argv (<see cref="ReviewLapGuardFile"/>), which is what lets
/// the handoff path be guarded at all.
/// </para>
/// </summary>
public sealed class PullRequestReviewCommand : Hall9kAsyncCommand<PullRequestReviewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PULL-REQUEST>")]
        [Description(
            "The pull request to review: the number (42 or #42), the owner/repo#42 shorthand, or the "
            + "pull request URL on github.com. Read through the gh CLI from the project's repository, so "
            + "it uses your own GitHub login and no token of Hall9k's")]
        public string PullRequest { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Project whose repository the pull request belongs to: its name, an unambiguous fragment, or "
            + "its full id (h9k project list shows them all). Optional when exactly one project is "
            + "registered, which is the ordinary single-project install")]
        public string? Project { get; init; }

        [CommandOption("--no-worktree")]
        [Description(
            "Skip the read-only checkout entirely — for reviewing against a deployed environment rather "
            + "than reading the code locally. The lap runs the same way otherwise: the same briefing, the "
            + "same verdict commands, the same guard against posting anything yourself")]
        public bool NoWorktree { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            store, session, settings, ExternalProcess.Runner,
            new GitWorktreeManager(new ConsoleWorktreeLogger<GitWorktreeManager>()), cancellationToken);
    }

    /// <summary>
    /// The whole lap, with its two outside seams handed in: <paramref name="processRunner"/> is
    /// how it reaches <c>gh</c> (for the pull-request read AND for the adoption import, which
    /// must be the same one or the two reads could answer from different accounts), and
    /// <paramref name="worktrees"/> is how it reaches git.
    /// <para>Internal so the attach, adopt, and no-worktree rules are testable against a real store and a scripted gh without going through <see cref="CliStore.Open"/>'s ambient connection — this codebase's CLI commands have no other test seam (the same shape <c>ReviewResolveCommand.ResolvePrReviewAsync</c> already takes).</para>
    /// </summary>
    internal static async Task<int> RunAsync(
        DocumentStore store,
        IDocumentSession session,
        Settings settings,
        ProcessRunner processRunner,
        IWorktreeManager worktrees,
        CancellationToken cancellationToken)
    {
        ProjectDetails project = await ResolveProjectAsync(session, settings.Project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        GitHubPullRequestSurface github = new(processRunner);
        PullRequestSurface pullRequest = await github.ReadAsync(
            settings.PullRequest, project.RepositoryPath, cancellationToken);
        RefuseUnreviewablePullRequest(pullRequest);

        ExternalReference reference = new(WorkItemProvider.GitHubPullRequest, $"{pullRequest.Repository}#{pullRequest.Number}");
        TaskListItem? existing = await FindLiveTaskAsync(session, reference, cancellationToken);

        LapAttachment attachment;
        if (existing is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]No live task holds {reference.Reference} on this node — adopting the pull request now, the same way h9k task add --from-pr does.[/]");
            attachment = await AdoptAndClaimAsync(
                store, session, project, context, pullRequest, reference, settings.NoWorktree, processRunner, worktrees, cancellationToken);
        }
        else
        {
            attachment = await AttachAsync(
                store, session, project, context, pullRequest, existing, settings.NoWorktree, worktrees,
                cancellationToken);
        }

        // Appended after the worktree exists, not before: WorktreePath is the fact this event
        // carries, and a lap whose checkout failed must not leave a record claiming one is there.
        await RecordLapOpenedAsync(store, attachment, pullRequest, context, cancellationToken);

        Guid taskId = attachment.TaskId;
        Guid runId = attachment.RunId;
        string worktreePath = attachment.WorktreePath;
        ReviewLapBriefing briefing = await ComposeBriefingAsync(
            session, project, taskId, runId, worktreePath, pullRequest, cancellationToken);
        string prompt = ReviewLapPromptBuilder.Build(briefing);

        string settingsFile = await WriteLapSettingsAsync(session, runId, prompt, cancellationToken);
        ReviewLapGuardOutcome guard = await ReviewLapGuardFile.InstallAsync(worktreePath, cancellationToken);

        await Doorbell.RingAsync($"pr-review-lap:{taskId}", cancellationToken);
        PrintHandoff(taskId, worktreePath, settingsFile, guard, prompt);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What the lap attached to, and the two stream versions its opening record is fenced
    /// against. The versions are the fix for a check-then-act race the first cut of this command
    /// had on both sides (independent pre-PR review, cycle 1, adversarial lens): admitting a
    /// BudgetParked or ReviewParked run and then spending a possible git fetch in
    /// <see cref="ReuseOrRecutCheckoutAsync"/> left a window in which the daemon's own token-budget
    /// retry sweep (<c>TokenBudgetRetryEngine.RetryOneAsync</c>) read <c>ReviewLapOpen == false</c>
    /// — the event had not committed yet — and resumed the automated session into the very
    /// checkout the reviewer was about to be handed, or startup adoption failed the lap's own
    /// freshly-dispatched sentinel run as "dispatched but never started".
    /// <para>
    /// <see cref="RunVersion"/> is the run stream's version the admission decision was made
    /// against, re-checked immediately before the record: every one of those daemon actions
    /// appends to the run stream (<c>RunResumed</c>, <c>RunFailed</c>), so a moved version is how
    /// this side finds out it lost, while nothing of the lap has been recorded yet.
    /// <see cref="TaskVersion"/> is the task stream's, carried as the append's own expected
    /// version so a second lap, a release, or a retry racing the same task loses at the database
    /// rather than both sides believing they opened the lap.
    /// </para>
    /// <para>
    /// What this does NOT do, stated so nothing downstream reads it as more: neither daemon sweep
    /// reserves anything before it acts, so a sweep that read the flag before this record
    /// committed and spawns after it still collides. That residual window is the sweep's own
    /// read-to-spawn gap, milliseconds against the seconds-to-a-minute the fetch used to leave
    /// open, and closing it outright means a reserve-before-spawn protocol in the shared
    /// budget-retry path that every run type would pay for. The sweeps re-read the flag as late
    /// as they can instead.
    /// </para>
    /// </summary>
    private sealed record LapAttachment(
        Guid TaskId, Guid RunId, string WorktreePath, long TaskVersion, long RunVersion);

    /// <summary>
    /// <c>--project</c>, or the only registered project when there is exactly one. Never a guess
    /// between several: a lap adopts and claims a task in whichever project it picks, and picking
    /// the wrong one puts a review task in the wrong repository's board.
    /// </summary>
    private static async Task<ProjectDetails> ResolveProjectAsync(
        IQuerySession session, string? named, CancellationToken cancellationToken)
    {
        if (named.IsNotBlank())
        {
            return await ProjectResolver.ResolveAsync(session, named, cancellationToken);
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        return projects switch
        {
            [ProjectDetails single] => single,
            [] => throw new DomainNotFoundException(
                "No projects are registered yet, so there is no repository to read a pull request from. "
                + "Register one: h9k project add --name <name> --repo <path>"),
            _ => throw new DomainValidationException(
                $"{projects.Count} projects are registered, so which repository this pull request belongs to "
                + "cannot be inferred. Pass --project <name> (h9k project list shows them)."),
        };
    }

    /// <summary>
    /// A pull request that is not open takes no review: GitHub refuses an APPROVE or a
    /// REQUEST_CHANGES on a merged or closed one, so a lap opened over it could only ever end in
    /// a refusal at the verdict — after the reviewer had done the whole reading. Refused up
    /// front, naming the state gh actually reported.
    /// </summary>
    private static void RefuseUnreviewablePullRequest(PullRequestSurface pullRequest)
    {
        if (pullRequest.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new DomainConflictException(
            $"GitHub reports {pullRequest.Repository}#{pullRequest.Number} as "
            + $"{RelayedText.OneLine(pullRequest.State)}, not open — a review cannot be submitted on it, so "
            + "there is no lap to run here. Read it on GitHub directly if you want to see what landed.");
    }

    /// <summary>
    /// The live pr-review task for this pull request, using the identical dedup rule
    /// <c>TaskAddCommand.RefuseSecondAdoptionAsync</c> and <c>AutoPrReviewEngine</c> already
    /// share (PLAN.md §3.1a, one live task per item): an Abandoned task does not count, and
    /// neither does a Done pr-review — a completed review does not hold its pull request hostage,
    /// so a reviewer opening a lap after one closed gets a fresh task rather than a refusal.
    /// <para>
    /// Matched on the canonical reference the import itself returned, never on one guessed from
    /// the project's own recorded repository URL, for the reason <c>AutoPrReviewEngine</c>'s own
    /// dedup comment gives at length: the two can disagree on casing, and a lap that missed its
    /// own node's existing task would adopt a duplicate — the exact thing this whole command is
    /// built to avoid.
    /// </para>
    /// </summary>
    private static async Task<TaskListItem?> FindLiveTaskAsync(
        IQuerySession session, ExternalReference reference, CancellationToken cancellationToken)
    {
        string canonical = reference.ToString();
        return await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Attaching to the task this node already holds. Which run the lap rides on depends on how
    /// far the automated review got:
    /// <list type="bullet">
    /// <item>
    /// Its own run is parked with a findings report — the ordinary case — so the lap rides on
    /// that run and reuses the worktree it already has. The reviewer reads the machines' report
    /// alongside their own reading, and their verdict resolves that same park.
    /// </item>
    /// <item>
    /// It has no run at all (Published, Queued, or Blocked — nothing dispatched yet), so the lap
    /// claims it interactively and cuts the checkout itself. The reviewer got here first.
    /// </item>
    /// <item>
    /// The lap the reviewer is re-entering is already open on that same run — closing the
    /// terminal is an ordinary way to leave one, exactly as it is for an interactive claim — so
    /// the run's own state is not questioned at all. It is <em>the lap's</em> run, and a lap-owned
    /// run sits at Dispatched for the whole lap because nothing ever moves it: reading the state
    /// there and refusing on it would make re-entry impossible.
    /// </item>
    /// </list>
    /// Anything else is refused with the state named rather than attached to: a run mid-lens has a
    /// live agent process reading that same worktree, and a second session in it double-books the
    /// checkout the way every other command in this surface already refuses to.
    /// </summary>
    private static async Task<LapAttachment> AttachAsync(
        DocumentStore store, IDocumentSession session, ProjectDetails project, BootstrapContext context,
        PullRequestSurface pullRequest, TaskListItem existing, bool noWorktree,
        IWorktreeManager worktrees, CancellationToken cancellationToken)
    {
        StreamState fence = await session.Events.FetchStreamStateAsync(existing.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {existing.Id}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                existing.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {existing.Id}.");

        if (task.Type != TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {task.Id} already holds {pullRequest.Repository}#{pullRequest.Number}, but it is a "
                + $"{task.Type.Value} task — the task that BUILT this pull request, not one that reviews it. "
                + "A review lap needs a pr-review task; this one cannot be turned into one (h9k task revise "
                + "refuses the type change). Abandon or finish that task first if it is genuinely a review.");
        }

        if (task.CurrentRunId is { } existingRunId)
        {
            // The run stream's version FIRST, before the projection and before every state check
            // below reads it: it is the version this admission is decided against and the one
            // RecordLapOpenedAsync re-checks, so it has to be no newer than the state that decided
            // (LapAttachment's own doc has the race). A projection load after it can only be as
            // new or newer, and newer means the record refuses — which is the safe direction.
            StreamState runFence = await session.Events.FetchStreamStateAsync(existingRunId, cancellationToken)
                ?? throw new DomainConflictException(
                    $"Task {task.Id} names run {existingRunId} but that run has no stream — nothing was ever "
                    + $"dispatched under it. h9k task retry {task.Id} to dispatch a fresh review, then open "
                    + "the lap again.");
            RunDetails run = await session.LoadAsync<RunDetails>(existingRunId, cancellationToken)
                ?? throw new DomainConflictException(
                    $"Task {task.Id} names run {existingRunId} but that run has no record — the automated "
                    + $"review likely died while preparing its worktree. h9k task retry {task.Id} to dispatch "
                    + "a fresh one, then open the lap again.");

            // Re-entry into the reviewer's own still-open lap is not subject to the run-state
            // check at all: a lap-owned run sits at Dispatched for the lap's whole life, so
            // reading its state would refuse every re-entry with a message about an automated
            // session that is not running (self-review, round one — the check as first written
            // made re-entry impossible).
            bool reenteringOwnLap = task.ReviewLapOpen && task.ReviewLapRunId == existingRunId;
            if (reenteringOwnLap)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Re-entering the review lap already open on task {task.Id} (run {existingRunId}).[/]");
            }
            else if (await IsLapRunWithNoLapRecordedAsync(session, existingRunId, runFence.Version, run, cancellationToken))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Run {existingRunId} is a review lap of this node's own whose opening never finished — re-opening the lap on it rather than cutting a second one.[/]");
            }
            else
            {
                await RefuseUnattachableRunAsync(
                    session, task.Id, existingRunId, runFence.Version, run, cancellationToken);
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Attached to task {task.Id}'s existing pr-review run {existingRunId}.[/]");
            }

            string reused = noWorktree
                ? string.Empty
                : await ReuseOrRecutCheckoutAsync(
                    project, task.Id, existingRunId, run, pullRequest, worktrees, cancellationToken);
            return new LapAttachment(task.Id, existingRunId, reused, fence.Version, runFence.Version);
        }

        return await ClaimAndCutAsync(
            store, session, project, context, pullRequest, task, fence, noWorktree, worktrees, cancellationToken);
    }

    /// <summary>
    /// A lap-owned run whose <see cref="PullRequestReviewLapOpened"/> never landed: the run and
    /// the lap's own event are two commits, and a Ctrl-C between them (or a crash while composing
    /// the briefing) leaves a claimed task naming a Dispatched sentinel run with
    /// <c>ReviewLapOpen</c> still false. Re-entry is the honest recovery, so this is read as one
    /// rather than refused — the refusal that stood here credited the automated review's own
    /// adversarial pass with reading the pull request in that worktree, for a run that has no
    /// process at all, and pointed the reviewer away from every way out (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// <para>
    /// The discriminator is the run's own recorded session role — a review lap is the one thing
    /// that dispatches under <see cref="SessionRoleName.ReviewLap"/>, and it is a suffix check for
    /// the reason <c>RunDetails.SessionName</c>'s own doc gives (a pre-field stream carries no
    /// name at all, which correctly reads as "not a lap"). A delivered run is excluded, so the
    /// verdict-already-recorded refusal still reaches a reviewer whose lap has genuinely ended.
    /// </para>
    /// </summary>
    private static async Task<bool> IsLapRunWithNoLapRecordedAsync(
        IQuerySession session, Guid runId, long runVersion, RunDetails run, CancellationToken cancellationToken)
    {
        if (run.State != RunState.Dispatched
            || !run.SessionName.EndsWith("-" + SessionRoleName.ReviewLap, StringComparison.Ordinal))
        {
            return false;
        }

        return await session.Events.AggregateStreamAsync<RunAggregate>(
                runId, version: runVersion, token: cancellationToken)
            is not { PrReviewDelivered: true };
    }

    /// <summary>
    /// Which run states a lap may attach to, for a run that is NOT the reviewer's own open lap.
    /// ReviewParked is the one the whole feature is built around; BudgetParked is admitted
    /// alongside it because a token budget that ran out is the platform's problem and not a reason
    /// to refuse the human who wants to review by hand. Everything else names what is happening
    /// instead.
    /// <para>
    /// UnderReview needs the run's own stream to describe honestly, which is why this reads the
    /// aggregate rather than the projection alone. A pr-review run reaches UnderReview twice, for
    /// opposite reasons: <c>PrReviewConformanceDispatched</c> puts it there while the second lens
    /// is still running, and <c>PrReviewDelivered</c> puts it there once a verdict has landed.
    /// Only the run stream tells them apart, and a message that guessed would tell a reviewer
    /// their verdict was already recorded while the machines were still reading (self-review,
    /// round one).
    /// </para>
    /// </summary>
    private static async Task RefuseUnattachableRunAsync(
        IQuerySession session, Guid taskId, Guid runId, long runVersion, RunDetails run,
        CancellationToken cancellationToken)
    {
        if (run.State == RunState.ReviewParked || run.State == RunState.BudgetParked)
        {
            return;
        }

        bool delivered = run.State == RunState.UnderReview
            && await session.Events.AggregateStreamAsync<RunAggregate>(
                    runId, version: runVersion, token: cancellationToken)
                is { PrReviewDelivered: true };
        // The way out travels WITH the reason, never as one suffix for all of them: a run that
        // is Failed, Killed, Completed or Superseded will never park a findings report, and
        // telling its reviewer to wait for one pointed them away from every route they had
        // (independent pre-PR review, cycle 1, adversarial lens — TaskFailed leaves CurrentRunId
        // standing, so a dead automated review lands squarely in the catch-all arm).
        const string OnceItParks = "open the lap once the automated review parks its findings report";
        (string Because, string WayOut) refusal = run.State switch
        {
            // Dispatched is not Running, and one sentence for both said a dispatched pass "is
            // still reading" when all that was observed is a launch — the never-guess rule
            // applied to a refusal's own text (Copilot review, pull request #271). The checkout
            // is already cut at dispatch, so the double-booking half holds for both.
            var state when state == RunState.Dispatched =>
                ("the automated review's own adversarial pass has been dispatched into that worktree and "
                    + "has not reported starting yet, and a second session in it would double-book the "
                    + "checkout", OnceItParks),
            var state when state == RunState.Running =>
                ("the automated review's own adversarial pass is still reading the pull request in that "
                    + "worktree, and a second session in it would double-book the checkout", OnceItParks),
            var state when state == RunState.Verifying =>
                ("the automated review's adversarial pass has finished and the engine has not dispatched its "
                    + "conformance pass yet", OnceItParks),
            var state when state == RunState.UnderReview && delivered =>
                ("a verdict has already been recorded on this task — the lap it belonged to has ended",
                    "the daemon is finalizing that task now; run h9k pr review again once it closes out and "
                    + "you get a fresh lap (a Done pr-review task does not hold its pull request hostage)"),
            var state when state == RunState.UnderReview =>
                ("the automated review's conformance pass is still running in that worktree", OnceItParks),
            var state when state.IsTerminal =>
                ($"its run is {state.Value}, which is terminal — that run will never park a findings report",
                    $"h9k task retry {taskId} dispatches a fresh review, and the lap attaches to it once it "
                    + "parks its report"),
            _ => ($"its run is {run.State.Value}", OnceItParks),
        };
        throw new DomainConflictException(
            $"Task {taskId} cannot take a review lap right now: {refusal.Because}. h9k task show {taskId} to "
            + $"see where it stands; {refusal.WayOut}.");
    }

    /// <summary>
    /// The recorded worktree when it is still on disk, and a fresh cut at the identical path when
    /// it is not. The path is deterministic in the task and run ids
    /// (<c>GitWorktreeManager.WorktreePathFor</c>), so re-cutting lands exactly where the run
    /// already recorded — which is what lets this recover a pruned or hand-deleted checkout
    /// without an event to correct the recorded path with. A prune runs first, because git still
    /// holds an administrative record of a worktree whose directory is gone and refuses to add
    /// the same path over it.
    /// </summary>
    private static async Task<string> ReuseOrRecutCheckoutAsync(
        ProjectDetails project, Guid taskId, Guid runId, RunDetails run, PullRequestSurface pullRequest, IWorktreeManager worktrees,
        CancellationToken cancellationToken)
    {
        if (run.WorktreePath.IsNotBlank() && Directory.Exists(run.WorktreePath))
        {
            return run.WorktreePath;
        }

        // A run that records NO worktree is one this lap opened with --no-worktree, and it stays
        // that way for the lap's whole life. Cutting one now would leave it named nowhere on the
        // run stream, which is the one place every consumer reads a checkout from — finalize,
        // RunLauncher's own pr-review cleanup, h9k task show — so it would be a worktree nothing
        // ever removes (self-review, round one, blast-radius sweep: two of those three readers
        // would have missed it). Refused with the way out rather than fixed with a fallback in
        // each reader.
        if (run.WorktreePath.IsBlank())
        {
            throw new DomainConflictException(
                $"Task {taskId}'s lap was opened with --no-worktree, and a lap's checkout is fixed for its "
                + "life — nothing on the run records one, so a checkout cut now would be one nothing ever "
                + $"releases. Re-run with --no-worktree to continue this lap, or end it (h9k pr approve "
                + $"{taskId} / h9k pr request-changes {taskId}) and open a fresh lap with a checkout.");
        }

        await worktrees.PruneAsync(project.RepositoryPath, cancellationToken);
        Worktree cut = await worktrees.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(project.RepositoryPath, pullRequest.Number, taskId, runId), cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]The run's read-only checkout was gone, so it was fetched again at {cut.Path}.[/]");
        return cut.Path;
    }

    /// <summary>
    /// Adoption, when nothing on this node holds the pull request yet: the same import, the same
    /// linked-work-item context, and the same one-live-task-per-item rule
    /// <c>h9k task add --from-pr</c> uses — then published, assigned to this owner, and claimed
    /// interactively, all in one atomic append, so the task is never observably Queued for the
    /// dispatcher to claim out from under a reviewer who is about to start reading.
    /// <para>
    /// The acceptance criterion the adoption writes is the reviewer's, not the machines': the
    /// deliverable of a lap is a submitted GitHub review, and that is what closing this task has
    /// to mean.
    /// </para>
    /// </summary>
    private static async Task<LapAttachment> AdoptAndClaimAsync(
        DocumentStore store, IDocumentSession session, ProjectDetails project, BootstrapContext context,
        PullRequestSurface pullRequest, ExternalReference reference, bool noWorktree, ProcessRunner processRunner, IWorktreeManager worktrees,
        CancellationToken cancellationToken)
    {
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken, processRunner: processRunner);
        ImportedWorkItem imported = await importer.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHubPullRequest, reference.Reference, project.RepositoryPath),
            cancellationToken);

        // Re-checked after the import, not only before it, exactly where
        // TaskAddCommand.AdoptAsync puts its own second-adoption refusal — and for the reason
        // that placement exists: the import is a real subprocess taking real time, and an
        // auto-pr-review sweep or a second lap can mint a task for this same pull request inside
        // that window (self-review, round one). Cheap, and it narrows the duplicate window to the
        // append itself.
        if (await FindLiveTaskAsync(session, imported.Reference, cancellationToken) is { } raced)
        {
            throw new DomainConflictException(
                $"Task {raced.Id} took {imported.Reference.Reference} while this lap was reading the pull "
                + "request — auto-pr-review's own sweep, most likely. Open the lap again and it will attach "
                + "to that task instead of adopting a second one.");
        }

        string objective = RelayedText.WithoutClosingKeywords(RelayedText.OneLine(imported.Title)).Trim() is { Length: > 0 } seed
            ? seed
            : $"Review pull request {imported.Reference.Key}";
        string provenance =
            $"A human reviewer opened a review lap on this pull request with h9k pr review; no task on this "
            + "node held it yet, so it was adopted here. Nothing about a GitHub reviewer assignment was "
            + "observed — this task exists because a person asked for it.";
        string? linkedContext = await LinkedWorkItemImport.TryImportContextAsync(
            session, project, imported, cancellationToken, processRunner: processRunner);
        string additional = linkedContext.IsNotBlank() ? $"{linkedContext}\n\n{provenance}" : provenance;
        string agentContext = WorkItemContext.Compose(imported, additional);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string[] criteria =
        [
            "The reviewer's verdict is submitted to the pull request as a GitHub review (h9k pr approve or h9k pr request-changes).",
        ];

        TaskAdded added = TaskDecider.Add(
            taskId, project.Id, objective, criteria, TaskType.PrReview, agentContext, constraints: null,
            imported.Reference, now, context.OwnerId, model: null, blockedBy: null, sourceIdeaId: null, epicId: null);
        TaskAggregate task = new();
        task.Apply(added);

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, now, context.OwnerId, project.BacklogPolicy);
        task.Apply(published);

        TaskAssigned assigned = TaskDecider.Assign(task, context.OwnerId, dependencies: [], now, context.OwnerId);
        task.Apply(assigned);

        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, context.OwnerId, runId, now);
        object[] lifecycle = [added, published, assigned, claimed];
        session.Events.StartStream<TaskAggregate>(taskId, lifecycle);
        await session.SaveChangesAsync(cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Adopted {imported.Reference} as pr-review task {taskId} and claimed it for this lap.[/]");

        // Counted rather than written as a literal: the version the claim landed at is what
        // CutAndDispatchAsync's recovery fences on, and a fifth event added to this chain later
        // would silently make a hardcoded number name the wrong one.
        (string adoptedWorktree, long adoptedRunVersion) = await CutAndDispatchAsync(
            store, project, context, pullRequest, taskId, runId, claimed.LeaseGeneration, lifecycle.Length,
            noWorktree, worktrees, cancellationToken);
        return new LapAttachment(taskId, runId, adoptedWorktree, lifecycle.Length, adoptedRunVersion);
    }

    /// <summary>
    /// The claim half alone, for a task that already exists but never dispatched a run. Same
    /// atomic-append discipline as adoption: whichever events the task's own state needs travel
    /// in one <c>Append</c> under one expected version, so a dispatcher racing this loses at the
    /// database rather than half-claiming.
    /// </summary>
    private static async Task<LapAttachment> ClaimAndCutAsync(
        DocumentStore store, IDocumentSession session, ProjectDetails project, BootstrapContext context,
        PullRequestSurface pullRequest, TaskAggregate task, StreamState fence, bool noWorktree, IWorktreeManager worktrees,
        CancellationToken cancellationToken)
    {
        if (task.State.IsPreDispatch)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — a draft is not ready to review against. "
                + $"h9k task publish {task.Id} first (a pr-review task's criteria are what closing it out "
                + "means), then open the lap.");
        }

        Guid runId = DomainId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<object> events = [];
        if (task.State == TaskState.Published)
        {
            TaskAssigned assigned = TaskDecider.Assign(task, context.OwnerId, dependencies: [], now, context.OwnerId);
            task.Apply(assigned);
            events.Add(assigned);
        }

        TaskClaimed claimed = TaskDecider.ClaimInteractively(
            task, context.OwnerId, runId, now,
            // A pr-review task carries no dependency edges of its own — TaskDecider.VetStackedEdge
            // refuses a stacked one outright and adoption never writes a --blocked-by — so a
            // Blocked pr-review task is not a shape this platform can produce, and the
            // acknowledgment ClaimInteractively's Blocked branch asks for has nothing to
            // acknowledge. Passed as already-acknowledged rather than exposing a flag for a state
            // that cannot occur: if one ever does, the honest answer is a claim that proceeds,
            // not a refusal pointing at an option this command deliberately does not have.
            dependencyOverrideAcknowledged: task.State == TaskState.Blocked);
        events.Add(claimed);
        long claimedVersion = fence.Version + events.Count;
        session.Events.Append(task.Id, expectedVersion: claimedVersion, [.. events]);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {task.Id} changed while claiming it for this lap — the dispatcher likely just claimed "
                + "it for the automated review. Check h9k status and open the lap again; if the automated "
                + "review is now running, the lap attaches to its findings report once it parks.");
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]Claimed pr-review task {task.Id} for this lap.[/]");
        (string cutWorktree, long cutRunVersion) = await CutAndDispatchAsync(
            store, project, context, pullRequest, task.Id, runId, claimed.LeaseGeneration, claimedVersion,
            noWorktree, worktrees, cancellationToken);
        return new LapAttachment(task.Id, runId, cutWorktree, claimedVersion, cutRunVersion);
    }

    /// <summary>
    /// The lap's own run: a read-only detached checkout of the pull request's head and a
    /// <see cref="RunDispatched"/> recording it, so everything downstream — the run directory,
    /// the finalize that releases the worktree, <c>h9k task show</c> — reads this lap exactly as
    /// it reads an automated pr-review run.
    /// <para>
    /// <c>NodeId</c> is the ceiling-exempt <see cref="Guid.Empty"/> sentinel every human-held
    /// claim carries (Decisions Log #103): a reviewer's own lap is a deliberate human act and
    /// does not compete with headless dispatch for concurrency. <c>DispatchingNodeId</c> is this
    /// node's real id, and it is load-bearing rather than decorative — it is how
    /// <c>RunSupervisor</c>'s sentinel sweep finds this run once the verdict moves it to
    /// UnderReview, which is what finalizes the task at all.
    /// </para>
    /// <para>
    /// <c>Branch</c> is <c>pr/&lt;n&gt;</c>, the same name <c>CreatePrReviewCheckoutAsync</c>
    /// gives an automated review's checkout, because that string is what
    /// <c>PrReviewEngine.FinalizeAsync</c> reads the pull request number back out of to clean up
    /// the tracking ref. It is recorded even under <c>--no-worktree</c>, where it is the only
    /// record of which pull request this run was for.
    /// </para>
    /// </summary>
    private static async Task<(string WorktreePath, long RunVersion)> CutAndDispatchAsync(
        DocumentStore store, ProjectDetails project, BootstrapContext context, PullRequestSurface pullRequest,
        Guid taskId, Guid runId, int leaseGeneration, long claimedVersion, bool noWorktree, IWorktreeManager worktrees,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        // Outside the try, because the catch arms have to know whether a checkout exists: a cut
        // that succeeded and a commit that then failed is the one window in which a worktree
        // exists at a path no run records, and every consumer that ever removes one
        // (PrReviewEngine.FinalizeAsync, RunLauncher.CleanUpPreviousPrReviewWorktreesAsync)
        // enumerates recorded RunDetails.WorktreePath — so nothing would ever have collected it
        // (independent pre-PR review, cycle 1, adversarial lens).
        string worktreePath = string.Empty;
        try
        {
            if (!noWorktree)
            {
                Worktree cut = await worktrees.CreatePrReviewCheckoutAsync(
                    new PrReviewWorktreeRequest(project.RepositoryPath, pullRequest.Number, taskId, runId),
                    cancellationToken);
                worktreePath = cut.Path;
            }

            TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");
            string? existingTaskDirectory = project.HomeDirectory.HasValue
                ? HomeEntryLookup.FindExisting(ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), taskId)
                    ?? HomeEntryLookup.FindExisting(ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value), taskId)
                : null;
            string runDirectory = existingTaskDirectory is not null
                ? RunPaths.ResolveDirectoryUnderTaskDirectory(existingTaskDirectory, runId)
                : RunPaths.ResolveDirectory(project.HomeDirectory, TaskDocumentRenderer.DirectoryName(taskDetails), runId);

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, context.OwnerId, leaseGeneration, DomainId.New(),
                worktreePath, $"pr/{pullRequest.Number}", ExecutorMode.Subscription, DateTimeOffset.UtcNow,
                IsFollowUp: false,
                // Fable is the human-interactive tier (AgentModel's own doc, Decisions Log #33),
                // the same fixed platform choice h9k task work makes for an attended session
                // rather than the project/task role chain a headless review resolves through.
                Model: AgentModel.Fable,
                RunDirectory: runDirectory,
                PrReviewBaseRefName: pullRequest.BaseRefName,
                SessionName: SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.ReviewLap),
                // FullPipeline by omission: a pr-review run never enters the pre-PR review
                // pipeline at all (PrReviewEngine drives it end to end), so composition has
                // nothing to compose here and recording anything narrower would imply a
                // reduction that was never made.
                DispatchingNodeId: context.NodeId,
                // Both blank, and both stated rather than omitted, exactly as RunLauncher records
                // them for an automated pr-review dispatch. Blank BaseBranch means "the project's
                // own" (the field's own doc), which is the honest answer here because this run has
                // no base of its own to stack on — the base it READS against is the reviewed pull
                // request's, recorded separately as PrReviewBaseRefName above, and conflating the
                // two would make a foreign pull request's base look like this branch's fork.
                // Blank BaseCommit means nothing was observed, which is true: a detached
                // branch-less read of somebody else's head has no fork point of its own, and a
                // pr-review run is never stacked, never replayed, and never rebased.
                BaseBranch: string.Empty,
                BaseCommit: string.Empty));
            await session.SaveChangesAsync(cancellationToken);

            // Version 1: this dispatch is the stream's only event, and it is what
            // RecordLapOpenedAsync re-checks — so startup adoption failing this sentinel run as
            // "dispatched but never started" between here and the record is caught rather than
            // recorded over (LapAttachment's own doc).
            return (worktreePath, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A Ctrl-C in exactly this window leaves the claim committed with no run record, and
            // h9k task release cannot undo that — it loads the very RunDetails this failed cut
            // never wrote. CancellationToken.None because the token that just fired is the one
            // this cleanup exists for (TaskWorkCommand's own claim does both the same way).
            await ReleaseUnrecordedCheckoutAsync(
                project, pullRequest.Number, worktreePath, worktrees, CancellationToken.None);
            await TaskWorkCommand.FailInteractiveClaimAsync(
                store, taskId, claimedVersion, runId, "cancelled while preparing the read-only checkout",
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await ReleaseUnrecordedCheckoutAsync(
                project, pullRequest.Number, worktreePath, worktrees, cancellationToken);
            await TaskWorkCommand.FailInteractiveClaimAsync(
                store, taskId, claimedVersion, runId, exception.Message, cancellationToken);
            throw new DomainConflictException(
                // "the lap could not be prepared" rather than naming the checkout: this arm also
                // catches the commit that follows the cut, so blaming the checkout for a database
                // failure would name a cause nobody observed. The exception's own message says
                // which half it was.
                $"Task {taskId} was claimed for the lap but the lap could not be prepared "
                + $"({exception.Message}). It has been recorded Failed — h9k task retry {taskId} to dispatch "
                + "the automated review, or open the lap again once whatever blocked it is fixed.");
        }
    }

    /// <summary>
    /// Removes a checkout that was cut and then never recorded on any run, plus the tracking ref
    /// the cut fetched — the same two cleanups <c>PrReviewEngine.FinalizeAsync</c> does at the
    /// other end of a run's life, and in the same order, because a checked-out worktree pins the
    /// ref it detached.
    /// <para>
    /// Best-effort and loud rather than silent: this runs while another failure is already on its
    /// way out, so a removal that fails must not replace it — but a directory left behind gets
    /// named, because the alternative is a leak nothing else in the platform can ever see (the
    /// cleanup consumers all enumerate recorded run paths, and this path is on no run). No
    /// worktree means nothing to say: <c>--no-worktree</c>, or a cut that itself failed, and a
    /// warning about a cleanup that was never needed reads as a leak somebody has to chase
    /// (AGENTS.md's honest-absence rule, the same reading <c>FinalizeAsync</c> applies to a
    /// checkout-less run).
    /// </para>
    /// </summary>
    private static async Task ReleaseUnrecordedCheckoutAsync(
        ProjectDetails project, int pullRequestNumber, string worktreePath, IWorktreeManager worktrees,
        CancellationToken cancellationToken)
    {
        if (worktreePath.IsNotBlank())
        {
            try
            {
                await worktrees.RemoveAsync(project.RepositoryPath, worktreePath, cancellationToken);
                await worktrees.DeletePrReviewTrackingRefAsync(
                    project.RepositoryPath, pullRequestNumber, cancellationToken);
            }
            catch (Exception exception)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]The read-only checkout at {worktreePath} could not be removed ({exception.Message}), and no run records it — git worktree remove --force it, or git worktree prune, to reclaim the space.[/]");
            }
        }
    }

    /// <summary>
    /// The lap's own record, fenced on both streams the admission was decided against
    /// (<see cref="LapAttachment"/> has the race this closes and the residual it does not).
    /// Both refusals leave NOTHING recorded, which is what makes re-running the command the whole
    /// remedy.
    /// </summary>
    private static async Task RecordLapOpenedAsync(
        DocumentStore store, LapAttachment attachment, PullRequestSurface pullRequest,
        BootstrapContext context, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState runNow = await session.Events.FetchStreamStateAsync(attachment.RunId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Run {attachment.RunId}'s stream disappeared while this lap was being prepared, so nothing "
                + $"of the lap was recorded. h9k task show {attachment.TaskId} to see where the task stands.");
        if (runNow.Version != attachment.RunVersion)
        {
            // The daemon acted on this run while the checkout was being prepared: the
            // token-budget retry sweep resuming the automated review (RunResumed), or startup
            // adoption failing a lap-owned sentinel run (RunFailed). Refused rather than recorded
            // over, because the alternative is a lap whose briefing quotes a findings report a
            // live agent session is at that moment overwriting in the same checkout, and a
            // verdict later posted to GitHub that this platform then cannot record.
            throw new DomainConflictException(
                $"Task {attachment.TaskId}'s run {attachment.RunId} moved while this lap was being prepared "
                + $"(version {attachment.RunVersion} -> {runNow.Version}) — the daemon acted on it, most "
                + "likely a token-budget retry resuming the automated review into that same checkout, or "
                + "startup adoption failing the run. NOTHING of this lap was recorded, and nothing was "
                + $"posted anywhere. h9k task show {attachment.TaskId} to see where it stands, then open the "
                + "lap again once the automated review parks its report (a checkout re-fetched for this "
                + "attempt is released by the next pr-review run on the task, which cleans up every previous "
                + "run's checkout before it cuts its own).");
        }

        session.Events.Append(
            attachment.TaskId, expectedVersion: attachment.TaskVersion + 1, new PullRequestReviewLapOpened(
                attachment.TaskId, attachment.RunId, attachment.WorktreePath,
                pullRequest.Url?.ToString() ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {attachment.TaskId} changed while this lap was being prepared — a second lap on the "
                + "same pull request, or a release or retry of the task. NOTHING of this lap was recorded. "
                + $"h9k task show {attachment.TaskId} to see where it stands, then open the lap again.");
        }
    }

    /// <summary>
    /// Everything the briefing states, read here so the composition itself stays pure. The two
    /// halves that may be absent are absent for different reasons and are read separately: the
    /// findings report is a file the automated run writes when it parks, and the author's own run
    /// exists only when this node can see the task that built the pull request at all.
    /// </summary>
    private static async Task<ReviewLapBriefing> ComposeBriefingAsync(
        IQuerySession session, ProjectDetails project, Guid taskId, Guid runId, string worktreePath,
        PullRequestSurface pullRequest, CancellationToken cancellationToken)
    {
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        string? findingsReport = run is null ? null : await ReadFindingsReportAsync(run, cancellationToken);
        (TaskDetails? authorTask, RunDetails? authorRun) = await FindAuthorsWorkAsync(
            session, pullRequest, cancellationToken);

        return new ReviewLapBriefing(
            taskId,
            pullRequest,
            project.Name,
            project.RepositoryPath,
            worktreePath,
            authorTask?.Objective,
            authorTask?.AcceptanceCriteria ?? [],
            authorTask is null ? null : TaskListCommand.ShortId(authorTask.Id),
            findingsReport,
            authorRun is null ? null : DescribeAuthorRun(authorRun));
    }

    /// <summary>
    /// The merged findings report the automated review parked, when it did. Read off the run's
    /// own directory rather than from a state check, because the state that says "a report
    /// exists" and the file that IS the report are written in two commits and a report is only
    /// real once it is on disk (<c>PrReviewEngine.ComposeReportAndParkAsync</c>'s own ordering
    /// comment says why that order, not this one, is the safe one).
    /// </summary>
    private static async Task<string?> ReadFindingsReportAsync(RunDetails run, CancellationToken cancellationToken)
    {
        string path = RunPaths.ReviewFindingsFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory), 1);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    /// <summary>
    /// The task that BUILT this pull request, and its run, when this node can read them — a
    /// single-node install, or several nodes sharing one database. Matched on the pull request URL
    /// the task recorded when its pull request opened, which is the one field that ties a build
    /// task to a pull request at all.
    /// <para>
    /// Nothing is ever written back to it. The verdict is the GitHub review, and the author's
    /// store is theirs (the 2026-09-06 ruling behind Decisions Log #149) — this read exists so a
    /// reviewer can be told what the platform already settled, not so the lap can settle anything
    /// on the author's behalf.
    /// </para>
    /// </summary>
    private static async Task<(TaskDetails? Task, RunDetails? Run)> FindAuthorsWorkAsync(
        IQuerySession session, PullRequestSurface pullRequest, CancellationToken cancellationToken)
    {
        if (pullRequest.Url is null)
        {
            return (null, null);
        }

        string url = pullRequest.Url.ToString();
        TaskListItem? item = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("lower(d.data ->> 'pullRequestUrl') = lower(?)", url))
            .Where(task => task.MatchesSql("d.data ->> 'type' <> ?", TaskType.PrReview.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return (null, null);
        }

        TaskDetails? task = await session.LoadAsync<TaskDetails>(item.Id, cancellationToken);
        RunDetails? run = task?.CurrentRunId is { } authorRunId
            ? await session.LoadAsync<RunDetails>(authorRunId, cancellationToken)
            : null;
        return (task, run);
    }

    /// <summary>
    /// The author's run rendered into the briefing's own vocabulary. Unclaimed residuals are the
    /// two tallies the platform already names on the pull request body itself
    /// (<c>PullRequestBody</c>): findings the review raised and then did not fix here, either
    /// because the loop ran out of cycles (unfixed) or because it graded them below the bar of
    /// the cycle that found them (ride-alongs). They are listed together because the distinction
    /// is about how the platform decided, and what the reviewer is being pointed at is the same
    /// either way — a finding on this diff nobody has vouched for.
    /// </summary>
    private static ReviewLapAuthorRun DescribeAuthorRun(RunDetails run)
    {
        List<string> residuals =
        [
            .. run.ReviewUnfixedFindings.Select(finding =>
                $"{Severity(finding.Severity)} at {Location(finding.Location)} — met the fix bar, no fix session reached it"),
            .. run.ReviewRideAlongFindings.Select(finding =>
                $"{Severity(finding.Severity)} at {Location(finding.Location)} — below the fix bar of the cycle that found it"),
        ];
        List<string> rulings =
        [
            .. run.ReviewParkResolutions.Select(resolution =>
                $"{resolution.Verdict.Value}: {resolution.Reason ?? "(no reason recorded)"}"),
            .. run.ExternalInteractions
                .Where(interaction => interaction.HumanDirected)
                .Select(interaction =>
                    $"human-directed interaction with {interaction.Party}: {interaction.Summary}"),
        ];
        // The two counted tallies stay counts rather than being folded into the residual list:
        // a fixed or routed residual has already been dealt with, and naming it alongside the
        // unclaimed ones would put things the platform closed in a list whose whole point is
        // that nobody closed them.
        return new ReviewLapAuthorRun(
            run.ReviewSettlement.Value.IsNotBlank() ? run.ReviewSettlement.Value : "not recorded",
            run.ReviewResidualsFixed,
            run.ReviewResidualsRouted,
            residuals,
            rulings);
    }

    private static string Severity(ReviewSeverity severity) =>
        severity == ReviewSeverity.Unknown ? "ungraded" : severity.Value.ToLowerInvariant();

    private static string Location(string location) => location.IsBlank() ? "no location stated" : location;

    /// <summary>
    /// The lap's settings file and its prompt, in the run's own directory beside every other
    /// artifact of that run. The settings carry the push guard as well as the platform's standing
    /// conventions (<see cref="ClaudeSettingsFile.BuildForReviewLap"/>); the prompt is written
    /// alongside it because a briefing that quotes a whole findings report is not something a
    /// reviewer wants to re-scroll a terminal for.
    /// </summary>
    private static async Task<string> WriteLapSettingsAsync(
        IQuerySession session, Guid runId, string prompt, CancellationToken cancellationToken)
    {
        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException($"Run {runId} has no record — the lap cannot write its settings.");
        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        Directory.CreateDirectory(runDirectory);
        string settingsFile = RunPaths.SettingsFile(runDirectory);
        await File.WriteAllTextAsync(
            settingsFile, ClaudeSettingsFile.BuildForReviewLap(ClaudeSettingsFile.DefaultCommandTimeout), cancellationToken);
        await File.WriteAllTextAsync(RunPaths.PromptFile(runDirectory), prompt, cancellationToken);
        return settingsFile;
    }

    private static void PrintHandoff(
        Guid taskId, string worktreePath, string settingsFile, ReviewLapGuardOutcome guard, string prompt)
    {
        if (worktreePath.IsNotBlank())
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Read-only worktree: {worktreePath}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No worktree (--no-worktree) — review against your deployed environment.[/]");
        }

        // Every hole below travels through MarkupLineInterpolated, which escapes interpolated
        // values itself — so these are the RAW paths, deliberately. Escaping them first would
        // double-escape (a '[' in a user-set HALL9K_HOME would render as '[[[['), which is the
        // mistake this comment exists to stop somebody re-introducing by copying
        // TaskWorkCommand's own handoff: that one escapes explicitly because its own call is a
        // plain MarkupLine, which escapes nothing (self-review, round one).
        FormattableString guardLine = guard switch
        {
            ReviewLapGuardOutcome.Written =>
                $"[dim]Push guard installed at {ReviewLapGuardFile.PathIn(worktreePath)} — any Claude Code session started in that worktree is denied git push, every gh write verb, gh api (the endpoint they all reach), and your own two verdict commands, with no flag needed.[/]",
            ReviewLapGuardOutcome.AlreadyGuarded =>
                $"[dim]Push guard already at {ReviewLapGuardFile.PathIn(worktreePath)} from an earlier entry into this lap, unchanged — any Claude Code session started in that worktree is still covered by it, with no flag needed.[/]",
            ReviewLapGuardOutcome.AlreadyPresent =>
                $"[yellow]The worktree already had a {ReviewLapGuardFile.RelativePath}, so the push guard was NOT written there[/] [dim]— nothing of the pull request's own was overwritten. Start your session with --settings {settingsFile}, which carries the same denials.[/]",
            ReviewLapGuardOutcome.Failed =>
                $"[yellow]The push guard could not be written into the worktree.[/] [dim]Start your session with --settings {settingsFile}, which carries the same denials.[/]",
            _ =>
                $"[dim]No worktree, so the push guard travels with the settings file: start your session with --settings {settingsFile}.[/]",
        };
        AnsiConsole.MarkupLineInterpolated(guardLine);

        // Escaped explicitly here, and only here: this one is a plain MarkupLine, built with
        // ordinary concatenation across lines (which an interpolated-string literal cannot be),
        // so nothing escapes it for us.
        string escapedSettingsFile = Markup.Escape(settingsFile);
        AnsiConsole.MarkupLine(
            $"[dim]Settings file: {escapedSettingsFile} — pass --settings {escapedSettingsFile} to your own "
            + "claude invocation for this platform's required conventions and the same push guard.[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]This lap ends only when you run h9k pr approve {taskId} --note \"…\" or h9k pr request-changes {taskId} --note \"…\" — never on its own.[/]");
        AnsiConsole.MarkupLine(
            "[dim]Paste the briefing below into a Claude Code session started anywhere — the worktree above is "
            + "the natural place, but nothing requires it:[/]");
        AnsiConsole.WriteLine();
        // WriteLine, not MarkupLine: the briefing is the reviewer's to paste verbatim, and
        // Spectre would try to parse any [..] in a findings report or a pull request body as
        // markup. ExternalText.ForTerminal first, for the reason TaskWorkCommand's own handoff
        // gives: this text quotes a title, a body and a findings report written by other people,
        // and a control or bidirectional-override character in any of them would be obeyed by
        // this terminal rather than shown.
        AnsiConsole.WriteLine(ExternalText.ForTerminal(prompt));
    }
}
