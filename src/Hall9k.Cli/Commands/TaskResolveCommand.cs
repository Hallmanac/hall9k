using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The attestation exit from Failed (Decisions Log #27): the run failed, but the objective
/// was met anyway — the task ends Done, with the failure still on the stream. The reason is
/// required (an attestation without a why is a guess, the AGENTS.md never-guess rule) and
/// the exit is human-only: no monitor resolves a failure (never loop on judgment, log #11).
/// The other two exits from Failed are h9k task retry and h9k task abandon.
/// </summary>
public sealed class TaskResolveCommand : Hall9kAsyncCommand<TaskResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Required: why the objective counts as met despite the run failure — the attestation recorded on the stream and shown by h9k task show")]
        public string? Reason { get; init; }

        [CommandOption("--pr <URL>")]
        [Description("Where the work landed, when known (e.g. the merged pull request) — recorded on the task and shown by h9k status. When it names a real pull request on the project's own repository, it also enrolls that pull request in closeout's orphan sweep, so its merge later completes this task's closeout (unblocking dependents, removing the retained worktree) same as any watched run — except on a pr-review task, whose --pr names the pull request it reviewed and is never enrolled")]
        public string? PullRequestUrl { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);

        // Fence before aggregating: a resolve racing h9k task retry (or the dispatch loop
        // after one) must not land on a task that already left Failed.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;

        // Resolved once and threaded into both guards below (independent pre-PR review, cycle 1,
        // adversarial, medium): a caller loading ProjectDetails and falling back to gh repo view
        // separately for each guard would risk the two guards observing different answers to the
        // same question, were the ambient gh fallback (a --repo-only project with no --repo-url)
        // to hit a transient failure on only one of the two calls — a foreign URL the run-stream
        // guard refused could then still reach the task stream. One observation up front rules
        // that out and avoids paying for the subprocess twice. Gated on the URL being present at
        // all, so a resolve with no --pr still never shells out to gh.
        Uri? projectRepositoryUrl = await ResolveProjectRepositoryUrlAsync(
            session, task, settings.PullRequestUrl, cancellationToken);

        RunStreamPullRequestOutcome runStreamOutcome = await RecordPullRequestOnRunStreamAsync(
            session, task, settings.PullRequestUrl, resolvedAt, projectRepositoryUrl, cancellationToken);

        // The task stream's own copy of --pr is guarded independently of whatever the run-stream
        // append above decided: a URL naming a repository other than the project's own known
        // repository never reaches the task stream, whether or not a run stream existed to append
        // onto (routed defect fix, independent pre-PR review, cycle 1, medium — see
        // SafeTaskStreamPullRequestUrl's own doc comment for the full reasoning, including why a
        // pr-review task's own URL is still recorded here whenever a run stream exists). The
        // repository-mismatch check is a courtesy that only fires once projectRepositoryUrl is
        // actually resolved (independent pre-PR review, cycle 2, low): a project whose repository
        // cannot be observed at all still lets a foreign URL through, same as
        // PullRequestUrls.NamesForeignRepository's own doc comment says.
        string? taskStreamPullRequestUrl = SafeTaskStreamPullRequestUrl(
            task, settings.PullRequestUrl, runStreamOutcome, projectRepositoryUrl);

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.Resolve(
            task, settings.Reason ?? string.Empty, taskStreamPullRequestUrl, resolvedAt, context.OwnerId));

        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while resolving — check h9k status; re-run this command " +
                "only if the task is still Failed.");
        }

        AnsiConsole.MarkupLineInterpolated(taskStreamPullRequestUrl.IsBlank()
            ? (FormattableString)$"[dim]Task {taskId} resolved to Done — the failure stays on the stream.[/]"
            : $"[dim]Task {taskId} resolved to Done — the failure stays on the stream. PR: {taskStreamPullRequestUrl}[/]");

        // Told on stderr rather than left to the "nothing is watching this pull request any
        // more" wording on h9k status to teach: a --pr given while the run stream itself is
        // missing but was still safe to show on the task (see SafeTaskStreamPullRequestUrl)
        // is the normal missing-run-sweep path (silent) and not this warning's concern — this fires
        // when a run stream existed to record onto and the URL still did not end up recorded there,
        // or when no run stream existed and the URL was not even safe to show on the task. The
        // reasons that can happen name different advice, and none of them names h9k pr resolve as a
        // fix (independent pre-PR review, cycle 1, medium): that command reopens the task onto its
        // own already-recorded PullRequestUrl and branch (TaskDecider.Reopen, PullRequestResolveCommand)
        // rather than accepting a new URL, so it cannot correct a mistyped or foreign one — it can
        // only dispatch a follow-up run onto a dead branch, or, worse, watch whatever pull request
        // the wrong number happens to name in the project's own repository. This task is Done, and
        // a fresh h9k task resolve --pr is never a real lever either way, since TaskDecider.Resolve
        // accepts only a Failed one.
        bool pullRequestUrlNotRecordedAnywhere = taskStreamPullRequestUrl.IsBlank();
        if (settings.PullRequestUrl.IsNotBlank()
            && (runStreamOutcome == RunStreamPullRequestOutcome.NotRecorded || pullRequestUrlNotRecordedAnywhere))
        {
            string visibility = pullRequestUrlNotRecordedAnywhere
                ? "it was not recorded at all — h9k task show and h9k status will not display it"
                : "h9k status will keep showing this run as unwatched, which is expected here";
            if (task.Type == TaskType.PrReview)
            {
                await Console.Error.WriteLineAsync(
                    $"Note: task {taskId} is a pull-request review — {settings.PullRequestUrl} names the "
                    + "pull request it reviewed, not one of this task's own, so closeout will never watch "
                    + "it for a merge. There is no lever that changes that: h9k pr resolve refuses a "
                    + $"pull-request review task outright. {visibility}.");
            }
            else
            {
                await Console.Error.WriteLineAsync(
                    $"Note: {settings.PullRequestUrl} does not look like a pull request on this task's own "
                    + "project repository, so closeout will not watch it for a merge. There is no lever "
                    + "that fixes this: h9k pr resolve reopens the task onto its already-recorded pull "
                    + $"request rather than accepting a corrected one, so it cannot record a different URL. {visibility}.");
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>What appending the run-side pull request came to, for the caller's stderr note and for tests.</summary>
    internal enum RunStreamPullRequestOutcome
    {
        /// <summary>
        /// <see cref="TaskAggregate.CurrentRunId"/> is null, or names a run whose stream was
        /// never started — an interactive claim's worktree cut failing before <c>h9k task work</c>
        /// starts the run stream, the same shape <c>RunLauncher.RecordLaunchFailureAsync</c>
        /// guards against. Appending here regardless would implicitly create that stream — Marten
        /// starts a stream on its first append — which would materialize a stub
        /// <c>RunDetails</c> row and drop the task out of
        /// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own candidate set, the one
        /// sweep that is actually built to complete closeout for exactly this shape (independent
        /// pre-PR review, cycle 1). Silent by design: the missing-run sweep watches this task's
        /// <c>PullRequestUrl</c> without needing a run stream at all — recorded on the task stream
        /// by <c>TaskDecider.Resolve</c> only when <see cref="SafeTaskStreamPullRequestUrl"/>
        /// judges it safe to (independent pre-PR review, cycle 1, medium). Belt-and-suspenders
        /// rather than the only guard: <c>CloseoutEngine.InspectMissingRunAsync</c> now applies the
        /// same pr-review guard and repository-match guard this command enforces everywhere else
        /// (routed defect fix, independent pre-PR review, cycle 1 adversarial), but an unsafe URL
        /// is still refused here first rather than relying on the sweep alone to catch it.
        /// </summary>
        NoRunStream,

        /// <summary>
        /// A run stream existed, but <see cref="BuildFailedRunPullRequestEvent"/> built nothing to
        /// append onto it — no <c>--pr</c> was given, the URL did not parse to a pull request
        /// number, or it named a different repository than the project's own.
        /// </summary>
        NotRecorded,

        /// <summary><see cref="PullRequestRecordedOnFailedRun"/> was appended to the run stream.</summary>
        Recorded,
    }

    /// <summary>
    /// The run-side counterpart to the pull request <see cref="TaskDecider.Resolve"/> also
    /// carries onto the task stream: without this, <c>RunDetails.PullRequestNumber</c> stays null
    /// forever and the run — still Failed — never matches <c>CloseoutEngine</c>'s orphan sweep
    /// (Decisions Log #72), so the row sits needs-you even after the pull request actually
    /// merges. Never <c>PullRequestOpened</c> or <c>PullRequestUpdated</c> here: either one moves
    /// <c>RunState</c> to <c>AwaitingReview</c>, which would pull this Failed run into the
    /// WATCHED sweep instead — the watched path dispatches follow-up runs onto the branch, which
    /// is not what a dead run's recovery record wants. A <see cref="TaskType.PrReview"/> task's
    /// <c>--pr</c> names the pull request it reviewed, not one of its own, so it records nothing
    /// here regardless of the URL: recording it would enroll a foreign pull request as this
    /// run's merge signal, and that pull request's own merge would complete this task's closeout
    /// and run the remote branch-delete cleanup <c>TaskDecider.Reopen</c> already refuses the
    /// type to prevent (adversarial review, cycle 3, high).
    /// </summary>
    internal static async Task<RunStreamPullRequestOutcome> RecordPullRequestOnRunStreamAsync(
        IDocumentSession session,
        TaskAggregate task,
        string? pullRequestUrl,
        DateTimeOffset resolvedAt,
        Uri? projectRepositoryUrl,
        CancellationToken cancellationToken)
    {
        if (task.CurrentRunId is not { } runId)
        {
            return RunStreamPullRequestOutcome.NoRunStream;
        }

        // Fenced the same way h9k pr resolve and h9k review resolve fence their own run-stream
        // appends: expectedVersion makes a concurrent writer (the daemon, or a duplicate
        // invocation of this command) fail loudly through TaskResolveCommand's own
        // EventStreamUnexpectedMaxEventIdException catch, rather than silently stacking a second
        // PullRequestRecordedOnFailedRun event on the same run.
        StreamState? runStreamState = await session.Events.FetchStreamStateAsync(runId, cancellationToken);
        if (runStreamState is null)
        {
            return RunStreamPullRequestOutcome.NoRunStream;
        }

        if (pullRequestUrl.IsBlank() || task.Type == TaskType.PrReview)
        {
            return RunStreamPullRequestOutcome.NotRecorded;
        }

        if (BuildFailedRunPullRequestEvent(runId, pullRequestUrl, resolvedAt, projectRepositoryUrl)
            is not { } pullRequestRecorded)
        {
            return RunStreamPullRequestOutcome.NotRecorded;
        }

        session.Events.Append(runId, expectedVersion: runStreamState.Version + 1, pullRequestRecorded);
        return RunStreamPullRequestOutcome.Recorded;
    }

    /// <summary>
    /// What <c>--pr</c> should be recorded as on the task stream, independent of whatever
    /// <see cref="RecordPullRequestOnRunStreamAsync"/> decided for the run stream: a URL naming a
    /// repository other than the project's own is refused whenever that project repository is
    /// actually known — resolved from <c>ProjectDetails</c> or observed through <c>gh</c>, never
    /// guessed — whether or not a run stream existed to append onto (routed defect fix, independent
    /// pre-PR review, cycle 1, medium: a run stream existing was previously read as "this URL is
    /// already safe to show on the task", but a run stream can exist and still come back
    /// <see cref="RunStreamPullRequestOutcome.NotRecorded"/> for a foreign repository, and the
    /// caller used to fall through to recording <paramref name="pullRequestUrl"/> verbatim in that
    /// case). The check is <see cref="PullRequestUrls.NamesForeignRepository"/> alone, not the
    /// fuller <see cref="PullRequestUrls.IsSafePullRequestUrl"/> the run-stream side uses: the
    /// option's own help text and AGENTS.md promise <c>--pr</c> is recorded on the task
    /// unconditionally, with only <em>enrollment</em> in closeout's orphan sweep conditioned on it
    /// naming a real pull request on the project's own repository, so a URL that is merely not
    /// pull-request-shaped (a commit link, an issue link) must still display here even though it can
    /// never enroll (independent pre-PR review, cycle 1, conformance and adversarial, medium: an
    /// earlier version of this guard called <see cref="PullRequestUrls.IsSafePullRequestUrl"/>
    /// directly, which also rejects on <see cref="PullRequestUrls.ParseNumber"/> failing, and so
    /// silently dropped display for that shape too — a narrowing neither document describes). When
    /// the project's repository cannot be resolved at all, the mismatch check is a no-op by design —
    /// see <see cref="PullRequestUrls.NamesForeignRepository"/>'s own doc comment (independent
    /// pre-PR review, cycle 2, low: this doc previously overclaimed "unconditionally", when the
    /// check has always been best-effort against a known repository). A pr-review task's own URL is
    /// excluded only when <paramref name="runStreamOutcome"/> is
    /// <see cref="RunStreamPullRequestOutcome.NoRunStream"/> — with no run stream at all, no
    /// <c>RunDetails</c> row will ever materialize to drop this task back out of
    /// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own candidate shape
    /// (<c>PullRequestUrl != null &amp;&amp; CurrentRunId != null</c>), so the task stream is the
    /// only lever left to keep it out of that candidate set at all. When a run stream does exist,
    /// that candidate shape is already reachable regardless (the run itself, terminal with no
    /// pull-request number recorded, already matches it), and <c>CloseoutEngine.InspectMissingRunAsync</c>
    /// applies the identical pr-review guard at inspection time before ever treating it as
    /// watchable — so recording it here is safe, and is exactly what the option's own help text
    /// and AGENTS.md promise: only *enrollment* is excepted for a pr-review task, never display
    /// (routed defect fix, independent pre-PR review, cycle 1, medium and adversarial: an earlier
    /// version of this guard excluded a pr-review task's URL unconditionally, silently dropping it
    /// from the task stream — and from h9k task show / h9k status — whenever a run stream existed,
    /// which neither document says happens).
    /// </summary>
    internal static string? SafeTaskStreamPullRequestUrl(
        TaskAggregate task,
        string? pullRequestUrl,
        RunStreamPullRequestOutcome runStreamOutcome,
        Uri? projectRepositoryUrl)
    {
        if (pullRequestUrl.IsBlank()
            || (task.Type == TaskType.PrReview && runStreamOutcome == RunStreamPullRequestOutcome.NoRunStream))
        {
            return null;
        }

        return PullRequestUrls.NamesForeignRepository(pullRequestUrl, projectRepositoryUrl) ? null : pullRequestUrl;
    }

    /// <summary>
    /// The project's own repository, falling back to the same ambient <c>gh repo view</c>
    /// observation <c>RunLauncher.LaunchAsync</c> and <c>TaskPublishCommand</c> already use for a
    /// project registered with <c>--repo</c> and no <c>--repo-url</c> — the shape that leaves
    /// <see cref="ProjectDetails.RepositoryUrl"/> null forever, since nothing backfills it and
    /// there is no <c>h9k project set --repo-url</c> (independent pre-PR review, cycle 3, medium:
    /// an earlier version of this guard read only <c>RepositoryUrl</c> and treated that null as
    /// "unknown, proceed" — permanently inert for exactly that project shape). Best-effort, like
    /// both precedents: a <c>gh</c> that cannot resolve a remote at all leaves nothing observed, so
    /// the caller proceeds exactly as it would when no repository is recorded at all. Resolved
    /// exactly once by <see cref="ExecuteAsync"/> and threaded into both
    /// <see cref="RecordPullRequestOnRunStreamAsync"/> and <see cref="SafeTaskStreamPullRequestUrl"/>
    /// (routed defect fix, independent pre-PR review, cycle 1, adversarial, medium: resolving it
    /// once per guard could let a transient gh failure on only one of the two calls make them
    /// disagree about the same URL's safety), and gated on <paramref name="pullRequestUrl"/> being
    /// present at all, so a resolve with no <c>--pr</c> never shells out to <c>gh</c> or even loads
    /// <c>ProjectDetails</c>. Unlike <see cref="RecordPullRequestOnRunStreamAsync"/>'s own guard,
    /// this one is <em>not</em> also gated on <see cref="PullRequestUrls.ParseNumber"/> succeeding:
    /// <see cref="SafeTaskStreamPullRequestUrl"/> checks <see cref="PullRequestUrls.NamesForeignRepository"/>
    /// against this method's return value for every <c>--pr</c> shape, pull-request-shaped or not (a
    /// commit link, an issue link), so a URL that fails to parse as a pull request still needs the
    /// project's repository resolved in order to be checked for a repository mismatch (routed defect
    /// fix, independent pre-PR review, cycle 2, medium: an earlier version of this guard also
    /// short-circuited on <see cref="PullRequestUrls.ParseNumber"/> failing, which starved
    /// <see cref="SafeTaskStreamPullRequestUrl"/>'s own <c>NamesForeignRepository</c> check of the
    /// repository it needed — <c>NamesForeignRepository</c> treats a null project repository as
    /// "never a mismatch", so every non-pull-request-shaped foreign URL looked same-repo by
    /// default). A pr-review task with no
    /// <see cref="TaskAggregate.CurrentRunId"/> at all is the identical waste for the same reason:
    /// <see cref="RecordPullRequestOnRunStreamAsync"/> returns
    /// <see cref="RunStreamPullRequestOutcome.NoRunStream"/> before ever touching this method's
    /// return value, and <see cref="SafeTaskStreamPullRequestUrl"/> excludes a pr-review task's URL
    /// outright in exactly that shape — so this method skips the load and the <c>gh</c> fallback for
    /// it too (independent pre-PR review, cycle 1, adversarial, low). A pr-review task whose
    /// <c>CurrentRunId</c> names a run whose stream itself never started is not caught by this
    /// shortcut — that shape can only be told apart from an ordinary live run by the same
    /// <c>FetchStreamStateAsync</c> call <see cref="RecordPullRequestOnRunStreamAsync"/> already
    /// makes, and repeating that call here to decide whether to resolve the repository would
    /// reintroduce the exact two-guard divergence risk resolving this once was meant to remove.
    /// </summary>
    internal static async Task<Uri?> ResolveProjectRepositoryUrlAsync(
        IDocumentSession session, TaskAggregate task, string? pullRequestUrl, CancellationToken cancellationToken,
        ProcessRunner? processRunner = null)
    {
        if (pullRequestUrl.IsBlank() || (task.Type == TaskType.PrReview && task.CurrentRunId is null))
        {
            return null;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        return project is null
            ? null
            : project.RepositoryUrl
                ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(
                    project.RepositoryPath, cancellationToken);
    }

    /// <summary>
    /// The run-stream event to append alongside <see cref="TaskDecider.Resolve"/>'s pull request,
    /// or null when there is nothing to record: no <c>--pr</c> was given, the URL does not parse
    /// to a real pull request number (never guess a number, AGENTS.md's never-guess rule), or the
    /// URL names a repository other than <paramref name="projectRepositoryUrl"/>'s own — a
    /// mistyped or copy-pasted URL from an unrelated repository must not become this run's merge
    /// signal, since CloseoutEngine inspects strictly by number against the project's own
    /// repository and a false match would let an unrelated pull request's merge complete this
    /// task's closeout and delete this run's own branch out from under it (adversarial review,
    /// cycle 1). The repository check is a courtesy, the same shape
    /// <c>RunLauncher.LaunchAsync</c>'s pr-review repository check is: a project with no
    /// <paramref name="projectRepositoryUrl"/> recorded proceeds rather than blocking on
    /// information this command does not have.
    /// In every null case the caller appends nothing, leaving the run exactly as invisible to
    /// <c>CloseoutEngine</c>'s orphan sweep as a resolve with no <c>--pr</c> already left it.
    /// </summary>
    public static PullRequestRecordedOnFailedRun? BuildFailedRunPullRequestEvent(
        Guid runId, string? pullRequestUrl, DateTimeOffset recordedAt, Uri? projectRepositoryUrl = null) =>
        PullRequestUrls.IsSafePullRequestUrl(pullRequestUrl, projectRepositoryUrl)
            ? new PullRequestRecordedOnFailedRun(
                runId, pullRequestUrl, PullRequestUrls.ParseNumber(pullRequestUrl), recordedAt)
            : null;
}
