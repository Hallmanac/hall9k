using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>
/// One sweep's tally, the <see cref="Hall9k.Daemon.Closeout.CloseoutSweepResult"/> shape applied
/// to this feature: <see cref="ProjectsFailed"/> feeds the monitor's own backoff decision exactly
/// as that sibling's <c>Failures</c> does, keyed the identical way — a project this sweep could
/// not reach at all (no repository resolved, gh itself unreachable) counts, one project's own
/// unrelated trouble (a single unreadable pull request) does not widen the interval for every
/// other project's own healthy reads. <see cref="ProjectsInspected"/> counts every registered
/// project since Decisions Log #161, not only the opted-in ones — a project recorded as off is
/// still read, because its requests are still recorded.
/// </summary>
public sealed record AutoPrReviewSweepResult(
    int ProjectsInspected, int ProjectsFailed, int TasksCreated, int AssignmentsRecalled);

/// <summary>
/// What one candidate's own decision came to: the outcome recorded against the pull request, the
/// task it produced or was covered by, whatever the outcome needs said in words to be actionable,
/// and the request actor read for it (null when nothing read one — an already-covered candidate
/// pays no timeline subprocess at all).
/// </summary>
internal sealed record MintAttempt(
    ReviewRequestOutcome Outcome, Guid? TaskId, string? Detail, ReviewRequestActor? Actor = null);

/// <summary>
/// The words the once-per-pull-request Info line says about an outcome (Decisions Log #161), and
/// the decision of whether a line is owed at all — pure, so both are testable without a sweep.
/// </summary>
internal static class AutoPrReviewObservation
{
    /// <summary>
    /// A line is owed for a request nothing has recorded yet, and for one whose recorded answer
    /// has actually changed since — never for a standing request whose answer is the same as last
    /// tick's, which at a three-minute interval would bury every other line in the log.
    /// <para>
    /// The answer is the outcome <em>and</em> the task it names (independent pre-PR review, cycle
    /// 1, adversarial lens, low): a genuine re-review mints a second task under the same
    /// <see cref="ReviewRequestOutcome.TaskCreated"/> outcome, and comparing outcomes alone would
    /// suppress the one line an operator watching the log has to see for it — the transition this
    /// record exists to make visible. <paramref name="taskId"/> is the id the row is about to
    /// carry, so a sweep that merely rediscovers the same task still says nothing.
    /// </para>
    /// </summary>
    public static bool IsReportable(
        ObservedReviewRequest? recorded, ReviewRequestOutcome outcome, Guid? taskId) =>
        recorded is null || recorded.Outcome != outcome || recorded.TaskId != taskId;

    /// <summary>
    /// The outcome to record now, given whatever was recorded before. A request this install
    /// minted a task for keeps <see cref="ReviewRequestOutcome.TaskCreated"/> when a later sweep
    /// merely rediscovers that same task through the already-covered fast path: it is the same
    /// fact restated, and letting it overwrite the record would both rewrite what this install
    /// actually did about the request and spend a second Info line on one pull request on the
    /// very next tick — the exact noise the one-line-per-pull-request rule exists to prevent.
    /// A different task covering it (a human's own <c>--from-pr</c> adoption after this one was
    /// abandoned) is a genuinely different answer and is recorded as such.
    /// <para>
    /// A row that already carries GitHub's own requested-at time keeps whatever it recorded when
    /// this tick's timeline read came back without one (independent pre-PR review, cycle 1,
    /// adversarial lens, low). <c>FindMostRecentRequestActorAsync</c> answers a flaky
    /// <c>gh api graphql</c> the same way it answers a pull request GraphQL cannot resolve — an
    /// actor with null fields — so a single hiccup against a standing request would otherwise
    /// overwrite an <em>observed</em> timestamp's verdict with "GitHub's own requested-at time
    /// could not be read", flip back on the next successful tick, and spend two Info lines and a
    /// misleading <c>h9k status</c> row on a failure that changed nothing. A failed read is not
    /// new information about the request, and the row's own <c>RequestedAt</c> is still there:
    /// the honest record is the one an earlier sweep actually observed (AGENTS.md: never guess at
    /// unobserved facts — including guessing that what was observed has stopped being true). The
    /// hold itself is unaffected — nothing mints on a tick that could not read the time — and a
    /// request first seen during the hiccup has no recorded time at all, so it is recorded as
    /// unknown exactly as before.
    /// </para>
    /// </summary>
    public static ReviewRequestOutcome Settle(
        ObservedReviewRequest? recorded, ReviewRequestOutcome outcome, Guid? taskId) =>
        recorded switch
        {
            not null when outcome == ReviewRequestOutcome.HeldRequestTimeUnknown
                && recorded.RequestedAt is not null => recorded.Outcome,
            not null when recorded.Outcome == ReviewRequestOutcome.TaskCreated
                && outcome == ReviewRequestOutcome.AlreadyCovered
                && taskId is not null
                && recorded.TaskId == taskId => ReviewRequestOutcome.TaskCreated,
            _ => outcome,
        };

    public static string Describe(ReviewRequestOutcome outcome, string? detail, Guid? taskId)
    {
        string task = taskId is { } id ? DomainId.Short(id) : "none";
        string sentence = ReviewRequestOutcome.FromInput(outcome.Value) switch
        {
            { } known when known == ReviewRequestOutcome.TaskCreated =>
                $"task {task} is created and reviewing",
            { } known when known == ReviewRequestOutcome.AlreadyCovered =>
                $"task {task} already covers it; nothing new was created",
            { } known when known == ReviewRequestOutcome.HeldSettingOff =>
                "nothing was created: auto pr-review is off here, so this one is yours to take by hand",
            { } known when known == ReviewRequestOutcome.HeldBeforeCutoff =>
                "nothing was created: the request predates this project's own auto-pr-review cutoff, so it "
                + "never starts on its own (the no-backfill guard) and is yours to take by hand",
            { } known when known == ReviewRequestOutcome.HeldRequestTimeUnknown =>
                "nothing was created: GitHub's own requested-at time could not be read, so nothing proves "
                + "the request postdates this project's own cutoff — yours to take by hand",
            { } known when known == ReviewRequestOutcome.MintFailed =>
                "nothing was created: the pull request could not be adopted",
            _ => $"an outcome this build does not recognise ({RelayedText.OneLine(outcome.Value)})",
        };

        return detail.IsBlank() ? sentence : $"{sentence} ({detail})";
    }
}

/// <summary>
/// The auto-pr-review core (idea e5e98a33, PLAN.md §16 decision #34's amendment, amended again
/// by #161): for every registered project, asks GitHub which open pull requests in that
/// project's repo currently request this install's own login — read back from GitHub every
/// sweep, never a configured or cached name — records each one once, and for each one with no
/// live task already watching it, mints, publishes, and starts a pr-review task exactly as
/// <c>h9k task add --from-pr</c> would, at the project's effective speed. The reviewer assignment
/// on GitHub is the go signal; there is no scheduling code here beyond the three general dispatch
/// levers this feature deliberately builds nothing new on top of: the ordinary claim rotation, the
/// queue-first marker (Decisions Log #127), and the ceiling-exempt claim <c>h9k task start</c>
/// already uses (Decisions Log #103, #125).
/// <para>
/// Every project, not only the opted-in ones (Decisions Log #161): the effective speed is
/// <see cref="AutoPrReviewSetting"/>'s resolution — <c>Normal</c> unless this project recorded
/// something else — and a project that recorded <c>off</c> still has its requests observed and
/// recorded, because the row an operator has to act on is only possible if the request was seen
/// at all. Two guards bound what that on-by-default reading may start: the no-backfill cutoff
/// (<see cref="AutoPrReviewCutoff"/>) and the same one-live-task-per-pull-request dedup this
/// engine has always enforced.
/// </para>
/// <para>
/// The same sweep also watches every non-terminal task it previously auto-created
/// (<see cref="TaskListItem.AutoPrReviewAssigneeLogin"/> non-null) for the mirror image: a
/// reviewer request GitHub no longer reports. Before the run ever dispatches (Published or
/// Queued), that concludes the task honestly — the go signal recalled by the same authority that
/// gave it. Once the run is Claimed or parked, it is recorded as an observation only; the work,
/// and any findings already produced, are never discarded for a reviewer reshuffle.
/// </para>
/// </summary>
public sealed class AutoPrReviewEngine(
    IDocumentStore store,
    NodeContext node,
    RunLauncher launcher,
    ProcessRunner processRunner,
    LaunchHoldEngine launchHold,
    ILogger<AutoPrReviewEngine> logger)
{
    private static readonly string[] TerminalStates =
        [TaskState.Done.Value, TaskState.Abandoned.Value];

    /// <summary>
    /// At most one ceiling-exempt launch per sweep (Decisions Log #64's own origin OOM,
    /// independent pre-PR review cycle 1, conformance and adversarial lenses both): the consent
    /// text a human agrees to at <c>h9k project set --auto-pr-review now</c>
    /// (<c>ProjectSetCommand.AutoPrReviewConsequence</c>) promises "an extra concurrent agent
    /// session", singular, and a single sweep iterating every currently-requested pull request
    /// across every opted-in project with no cap at all could otherwise start as many ceiling-exempt
    /// sessions as there are open requests in one tick. A candidate beyond the cap is not dropped —
    /// it is minted, published and assigned exactly as a <c>First</c>-speed task is, so it still
    /// takes the next free ordinary dispatch slot rather than waiting a full poll interval for
    /// nothing to happen.
    /// </summary>
    private const int MaxImmediateLaunchesPerSweep = 1;

    private readonly GitHubReviewAssignments reviewAssignments = new(processRunner);

    private int _immediateLaunchesThisSweep;

    /// <summary>
    /// Read fresh at the point <see cref="CreateOneAsync"/> decides whether to launch immediately,
    /// not sampled once at the top of the sweep and reused (Copilot review, PR #317; this replaced
    /// an earlier per-sweep field after independent pre-PR review, cycle 1, conformance lens, first
    /// flagged the shape below): a node-wide launch hold means this node cannot launch a working
    /// session at all right now (task: a session that exits at once with no work done is treated as
    /// the node failing to launch sessions), so a Now-speed candidate must take the ordinary
    /// queue-first slot below rather than the ceiling-exempt immediate launch — claiming and
    /// spawning straight into a broken node strands one more task the same way the dispatcher's own
    /// claim gate already refuses to, since this direct claim-and-launch bypasses that gate
    /// entirely (it never goes through <c>DispatchEngine</c>). A sweep walks every project and
    /// candidate in turn, with real GitHub fetches and <c>LinkedWorkItemImport</c> calls between
    /// them, so a hold raised mid-sweep by an earlier candidate's own failure must still be seen by
    /// a later one's decision — a value sampled once at the sweep's own start cannot see that.
    /// </summary>
    private async Task<bool> LaunchHoldActiveAsync(CancellationToken cancellationToken) =>
        await launchHold.CurrentHoldAsync(node.NodeId, cancellationToken) is { LaunchHoldActive: true };

    /// <summary>
    /// One Info line per project, at daemon start, naming whether auto pr-review is on or off
    /// there and whether that is the project's own recorded choice or the platform default
    /// (Decisions Log #161) — printed always, at the default too, because the origin incident was
    /// not a wrong setting but an invisible one: the feature sat installed and silent on both
    /// nodes for three days with nothing on any surface saying so.
    /// <para>
    /// It is also where this install's own no-backfill cutoff is first recorded, since the first
    /// daemon start after the flip is exactly the moment the on-by-default behaviour arrives
    /// here. A failure to reach the database is logged and swallowed by the monitor rather than
    /// taking the poll loop down with it: the sweep re-records the cutoff on its own first tick.
    /// </para>
    /// </summary>
    public async Task AnnounceSettingsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset adoptedAt = await EnsureDefaultAdoptionAsync(cancellationToken);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<ProjectDetails> projects = await query.Query<ProjectDetails>().ToListAsync(cancellationToken);
        IReadOnlyDictionary<Guid, AutoPrReviewSetting> settings = await AutoPrReviewSetting.ResolveAllAsync(
            query, projects.Select(project => project.Id), cancellationToken);

        logger.LogInformation(
            "Auto-pr-review is on by default (Decisions Log #161), watching both a review request and a "
            + "GitHub mention of the install's own login (idea 2f079bcd); this install adopted that on "
            + "{AdoptedAt:u}, and nothing GitHub recorded before a project's own cutoff starts a task on its own",
            adoptedAt);

        foreach (ProjectDetails project in projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            AutoPrReviewSetting setting = settings[project.Id];
            logger.LogInformation(
                "Auto pr-review is {State} for project {Project} — {Speed} ({Origin}) — covering both a "
                + "review request and a GitHub mention of the install's own login; change it with "
                + "h9k project set {Project} --auto-pr-review off|normal|first|now",
                setting.OnOff, project.Name, setting.Speed.Value.ToLowerInvariant(), setting.Origin, project.Name);
        }
    }

    public async Task<AutoPrReviewSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        // Sequential ticks only (AutoPrReviewMonitor awaits one PollOnceAsync before starting the
        // next), so a plain field is safe here without any locking.
        _immediateLaunchesThisSweep = 0;

        DateTimeOffset adoptedAt = await EnsureDefaultAdoptionAsync(cancellationToken);

        IReadOnlyList<ProjectDetails> projects;
        IReadOnlyDictionary<Guid, AutoPrReviewSetting> settings;
        await using (IQuerySession query = store.QuerySession())
        {
            // Every project, not only the ones this feature will act for (Decisions Log #161):
            // a review request GitHub makes of this install's own login is recorded once per pull
            // request whatever the project's setting says, because the off-row an operator has to
            // act on is only possible if the request was observed at all. The cost is one
            // gh pr list per project per tick rather than per opted-in project per tick — a
            // human-timescale poll against a handful of projects, which is what the interval is
            // sized for.
            projects = await query.Query<ProjectDetails>().ToListAsync(cancellationToken);
            settings = await AutoPrReviewSetting.ResolveAllAsync(
                query, projects.Select(project => project.Id), cancellationToken);
        }

        int inspected = 0;
        int failed = 0;
        int created = 0;
        int recalled = 0;
        foreach (ProjectDetails project in projects)
        {
            inspected++;
            try
            {
                (int projectCreated, int projectRecalled) = await SweepProjectAsync(
                    project, settings[project.Id], adoptedAt, cancellationToken);
                created += projectCreated;
                recalled += projectRecalled;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogWarning(
                    exception, "Auto-pr-review sweep failed for project {Project}; will retry next tick", project.Name);
            }
        }

        return new AutoPrReviewSweepResult(inspected, failed, created, recalled);
    }

    /// <summary>
    /// This install's own adoption moment, read if it exists and written once if it does not —
    /// never recomputed (Decisions Log #161), because a cutoff that moved every start would let
    /// a request that was too old yesterday be too old again tomorrow while a fresh one in
    /// between was never actionable at all. A racing second writer is answered by re-reading
    /// rather than overwriting: whichever moment landed first is this install's honest one.
    /// </summary>
    private async Task<DateTimeOffset> EnsureDefaultAdoptionAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        AutoPrReviewDefaultAdoption? recorded =
            await session.LoadAsync<AutoPrReviewDefaultAdoption>(node.NodeId, cancellationToken);
        if (recorded is not null)
        {
            return recorded.AdoptedAt;
        }

        AutoPrReviewDefaultAdoption adoption = new() { Id = node.NodeId, AdoptedAt = DateTimeOffset.UtcNow };
        session.Insert(adoption);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Auto-pr-review recorded this install's on-by-default cutoff at {AdoptedAt:u}: a review request "
                + "GitHub recorded before it never starts a task on its own (Decisions Log #161)",
                adoption.AdoptedAt);
            return adoption.AdoptedAt;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Insert, not Store, precisely so a second writer's own moment cannot overwrite the
            // one already recorded: the duplicate-key failure is the guarantee, and the answer to
            // it is to read back whoever won rather than to re-record.
            await using IQuerySession retry = store.QuerySession();
            if (await retry.LoadAsync<AutoPrReviewDefaultAdoption>(node.NodeId, cancellationToken) is { } theirs)
            {
                return theirs.AdoptedAt;
            }

            logger.LogWarning(exception, "Auto-pr-review could not record this install's on-by-default cutoff");
            throw;
        }
    }

    private async Task<(int Created, int Recalled)> SweepProjectAsync(
        ProjectDetails project, AutoPrReviewSetting setting, DateTimeOffset adoptedAt,
        CancellationToken cancellationToken)
    {
        Uri? repositoryUrl = project.RepositoryUrl
            ?? await new GitHubWorkItemProvider(processRunner).TryObserveRepositoryHostAsync(project.RepositoryPath, cancellationToken);
        string repository = RunLauncher.OwnerRepoFrom(repositoryUrl)
            ?? throw new InvalidOperationException(
                $"Could not resolve which GitHub repository {project.Name} is ({project.RepositoryPath}) — "
                + "neither the project's own recorded URL nor gh repo view named one.");

        string login = await reviewAssignments.CurrentLoginAsync(project.RepositoryPath, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Could not read gh's authenticated login from {project.RepositoryPath} — gh may not be "
                + "installed or signed in on this node.");

        IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested =
            await reviewAssignments.ListReviewRequestedAsync(repository, login, project.RepositoryPath, cancellationToken);

        DateTimeOffset cutoff = AutoPrReviewCutoff.For(project.RegisteredAt, adoptedAt);
        int created = await ObserveRequestsAsync(
            project, setting, repository, login, cutoff, currentlyRequested, cancellationToken);
        int recalled = await ConcludeWithdrawnAsync(project, repository, login, currentlyRequested, cancellationToken);
        await ForgetWithdrawnObservationsAsync(repository, login, currentlyRequested, cancellationToken);
        // The second search (idea 2f079bcd, decision 1): every open pull request in this
        // repository currently mentioning the install's own login, alongside the review-requested
        // search above — the same "read fresh every sweep" discipline, and the same "every
        // registered project, whatever its setting says" scope, for the identical reason: the
        // needs-you row an operator has to act on is only possible if the mention was observed at
        // all, even when the project's own setting refuses to act on it.
        created += await ObserveMentionsAsync(project, setting, repository, login, cutoff, cancellationToken);
        return (created, recalled);
    }

    /// <summary>
    /// Every currently review-requested pull request, recorded once and acted on according to
    /// this project's own effective setting and this install's no-backfill cutoff (Decisions Log
    /// #161). The order the four questions are asked in is itself the decision:
    /// <list type="number">
    /// <item>Is a live task already covering it? Then nothing is asked of anyone, and no
    /// <c>gh</c> call beyond the search is paid — the fast path this engine has always had.</item>
    /// <item>Can GitHub's own requested-at time be read? Without it nothing proves the request
    /// postdates the cutoff, so nothing starts on its own.</item>
    /// <item>Does it postdate the cutoff? The no-backfill guard outranks the setting, because a
    /// stale request stays stale after an operator turns the setting on — telling them
    /// otherwise would be the one row that lies about its own lever.</item>
    /// <item>Is the setting off here? Then the row is theirs to act on.</item>
    /// </list>
    /// </summary>
    private async Task<int> ObserveRequestsAsync(
        ProjectDetails project, AutoPrReviewSetting setting, string repository, string login,
        DateTimeOffset cutoff, IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested,
        CancellationToken cancellationToken)
    {
        int created = 0;
        foreach (ReviewRequestedPullRequest candidate in currentlyRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using IDocumentSession session = store.LightweightSession();
            // This node and this project, because they are the decider: the row this sweep reads
            // back and grades against is the one its own setting, registration and adoption
            // moment wrote, never another project's or another install's answer to the same
            // question (ObservedReviewRequest.ComputeId).
            string id = ObservedReviewRequest.ComputeId(
                node.NodeId, project.Id, repository, candidate.Number, login);
            ObservedReviewRequest? recorded = await session.LoadAsync<ObservedReviewRequest>(id, cancellationToken);

            MintAttempt attempt = await DecideAsync(
                session, project, setting, repository, login, cutoff, candidate, cancellationToken);
            if (attempt.Outcome == ReviewRequestOutcome.TaskCreated)
            {
                created++;
            }

            await RecordObservationAsync(
                session, project, setting, repository, login, candidate, recorded, attempt, cancellationToken);
        }

        return created;
    }

    private async Task<MintAttempt> DecideAsync(
        IDocumentSession session, ProjectDetails project, AutoPrReviewSetting setting, string repository,
        string login, DateTimeOffset cutoff, ReviewRequestedPullRequest candidate,
        CancellationToken cancellationToken)
    {
        // A cheap fast path in front of the gh pr view subprocess the import below always pays
        // (independent pre-PR review, cycle 1, conformance lens, low): the overwhelmingly common
        // case on every sweep after the first is "a live task already covers this pull request",
        // and a case-insensitive match against the reference guessed from repository — never
        // gh's own canonical casing, per the discipline the canonical dedup check inside
        // CreateOneAsync still enforces — catches it without ever shelling out. A guess that
        // finds nothing here is not trusted as "nothing exists": it is only a fast path in front
        // of the canonical check, never a replacement for it, so the import and the exact-match
        // dedup still run regardless of what this finds.
        string guessedReference = $"{WorkItemProvider.GitHubPullRequest.Value}:{repository}#{candidate.Number}";
        TaskListItem? likelyCovering = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("lower(d.data ->> 'externalReference') = lower(?)", guessedReference))
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            // Newest first, the same task h9k status' own pane names as the covering one: which
            // task this returns is now part of whether an Info line is owed at all
            // (AutoPrReviewObservation.IsReportable), and an unordered FirstOrDefault over two
            // rows could name a different one per tick — a line per tick for a request whose
            // answer never changed.
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (likelyCovering is not null)
        {
            return new MintAttempt(ReviewRequestOutcome.AlreadyCovered, likelyCovering.Id, null);
        }

        // GitHub's own timestamp, read fresh every sweep and deliberately never reused from the
        // row this candidate already has: the timeline read returns the MOST RECENT request for
        // this login, so a re-request is a new answer to the same question. Serving a remembered
        // one instead would hold a request the requester has since re-made — permanently, for a
        // request older than the cutoff, since nothing else would ever move that comparison —
        // and would feed a stale timestamp to IsGenuineReRequestAsync below, which is exactly the
        // comparison that tells a genuine re-review apart from the same standing request. The
        // cost is one graphql call per standing request per tick, which is what this path paid
        // before the record existed; a candidate a live task already covers still returns above
        // without paying it at all.
        ReviewRequestActor actor = await reviewAssignments.FindMostRecentRequestActorAsync(
            OwnerFrom(repository), NameFrom(repository), candidate.Number, login,
            ReviewTimelineEventKind.Requested, project.RepositoryPath, cancellationToken);

        if (actor.RequestedAt is null)
        {
            // Debug, not Info: a flaky graphql call and a pull request GraphQL genuinely cannot
            // resolve are indistinguishable from here, and the recorded row is the surface that
            // says which requests are held (AutoPrReviewObservation.Settle keeps an already-read
            // time's own verdict rather than letting one bad tick rewrite it). This line is what
            // makes a repeating read failure findable in the log without an Info line per tick.
            logger.LogDebug(
                "Auto-pr-review could not read GitHub's own requested-at time for {Repository}#{Number} on "
                + "this tick; nothing starts on its own from a request whose time is unproven",
                repository, candidate.Number);
            return new MintAttempt(ReviewRequestOutcome.HeldRequestTimeUnknown, null, null, actor);
        }

        if (!AutoPrReviewCutoff.StartsOnItsOwn(actor.RequestedAt, cutoff))
        {
            return new MintAttempt(ReviewRequestOutcome.HeldBeforeCutoff, null, null, actor);
        }

        if (!setting.IsOn)
        {
            return new MintAttempt(ReviewRequestOutcome.HeldSettingOff, null, null, actor);
        }

        try
        {
            return await CreateOneAsync(session, project, setting, repository, candidate, login, actor, cancellationToken);
        }
        catch (DomainException exception)
        {
            // A race with GitHub itself (closed or merged between the search and the import) or a
            // genuinely unreadable pull request: recorded as a refused mint and skipped rather
            // than failing the whole project's sweep, since every other candidate this project
            // offered is unrelated to this one's own trouble — and recorded rather than only
            // logged, so the operator gets a row naming what to do by hand.
            logger.LogWarning(
                exception, "Auto-pr-review could not adopt {Repository}#{Number}; skipping this poll",
                repository, candidate.Number);
            return new MintAttempt(
                ReviewRequestOutcome.MintFailed, null, RelayedText.OneLine(exception.Message), actor);
        }
    }

    /// <summary>
    /// The record itself, and the one Info line per pull request an orchestrator window's log
    /// tail picks up (Decisions Log #161). The line is written when the row is new or when its
    /// recorded answer — the outcome, or the task that answer names — actually changed; never on
    /// every tick for a standing request whose answer is the same as last tick's, which would
    /// bury the log, and never suppressed for a genuinely different one (an off project turned
    /// on, a held request that finally minted, a re-review minting a second task under the same
    /// outcome), which is exactly the transition the operator is watching for.
    /// <para>
    /// "Its recorded answer" means this decider's own: the row is keyed on the observing node and
    /// project as well as the request (<see cref="ObservedReviewRequest.ComputeId"/>), so what a
    /// second project pointing at the same repository — or a second install sharing this database
    /// under the same <c>gh</c> login — graded the same request cannot flip this row's answer and
    /// spend a line per tick saying so. One line per pull request per decider, and in the
    /// ordinary single-project-single-node case that is one line per pull request.
    /// </para>
    /// </summary>
    private async Task RecordObservationAsync(
        IDocumentSession session, ProjectDetails project, AutoPrReviewSetting setting, string repository,
        string login, ReviewRequestedPullRequest candidate, ObservedReviewRequest? recorded,
        MintAttempt attempt, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ObservedReviewRequest observed = recorded ?? new ObservedReviewRequest
        {
            Id = ObservedReviewRequest.ComputeId(
                node.NodeId, project.Id, repository, candidate.Number, login),
            FirstObservedAt = now,
        };

        ReviewRequestOutcome outcome = AutoPrReviewObservation.Settle(recorded, attempt.Outcome, attempt.TaskId);
        // Read off recorded, not off observed: the two are the same object whenever a row already
        // existed, so anything read after the assignments below would be this sweep's own answer.
        Guid? taskId = attempt.TaskId ?? recorded?.TaskId;
        bool reportable = AutoPrReviewObservation.IsReportable(recorded, outcome, taskId);
        // Both are part of the row's own key, so they are restated rather than changed: a row
        // this decider loaded by that key already carries them.
        observed.ObservingNodeId = node.NodeId;
        observed.ProjectId = project.Id;
        observed.Repository = repository;
        observed.Number = candidate.Number;
        observed.PullRequestUrl = candidate.Url;
        observed.ReviewerLogin = login;
        observed.RequesterLogin = attempt.Actor?.Login ?? observed.RequesterLogin;
        observed.RequestedAt = attempt.Actor?.RequestedAt ?? observed.RequestedAt;
        observed.LastObservedAt = now;
        observed.SettingWhenObserved = setting.Speed;
        observed.SettingWasRecorded = setting.Recorded;
        observed.Outcome = outcome;
        // The detail belongs to the outcome it was recorded with: a settled outcome that kept an
        // earlier sweep's answer keeps that sweep's own words too, rather than pairing last
        // tick's sentence with this tick's rediscovery.
        observed.OutcomeDetail = outcome == attempt.Outcome ? attempt.Detail : observed.OutcomeDetail;
        observed.TaskId = taskId;

        session.Store(observed);
        await session.SaveChangesAsync(cancellationToken);

        if (reportable)
        {
            logger.LogInformation(
                "Auto-pr-review observed a review request: {Repository}#{Number} in project {Project} — "
                + "auto pr-review is {State} here ({Speed}, {Origin}) — {Outcome}",
                repository, candidate.Number, project.Name, setting.OnOff,
                setting.Speed.Value.ToLowerInvariant(), setting.Origin,
                AutoPrReviewObservation.Describe(outcome, observed.OutcomeDetail, observed.TaskId));
        }
    }

    /// <summary>
    /// Drops the rows for pull requests GitHub no longer reports as requesting this login — a
    /// withdrawal, a merge, a close, or a submitted review that cleared the request all end it.
    /// Absence from the search is enough here and deliberately not enough in
    /// <c>ConcludeOneAsync</c>: dropping a row costs a re-record and one more log line if the
    /// request returns, while abandoning a task on the same evidence would throw work away. It is
    /// only ever reached after a successful search, since a <c>gh</c> failure throws out of
    /// <c>SweepProjectAsync</c> before this runs.
    /// <para>
    /// Scoped to <paramref name="repository"/> and <paramref name="login"/> together — exactly
    /// the scope of the search that produced <paramref name="currentlyRequested"/> — and
    /// deliberately not to the sweeping project's or this node's own id, even though both are
    /// part of a row's key (<see cref="ObservedReviewRequest.ComputeId"/>). What a row is keyed
    /// on is who graded the request; what this search proves is whether GitHub still makes it,
    /// which is one fact about the pull request and the login and no decider's own. So every
    /// decider's row for a request GitHub has stopped reporting goes, including a second
    /// project's pointing at the same repository and an install's that has since been
    /// decommissioned — neither of which would otherwise ever be cleared, leaving a needs-you row
    /// in <c>h9k status</c> for a request nobody is making. Narrower on the number alone, a row
    /// recorded when the project pointed at a different repository would be deleted against a
    /// search that never covered it (Copilot review, PR #292).
    /// </para>
    /// <para>
    /// Only the rows about <paramref name="login"/> — the very login the search that produced
    /// <paramref name="currentlyRequested"/> was scoped to (independent pre-PR review, cycle 1,
    /// adversarial lens, medium). Two installs can share one database, which is why
    /// <see cref="AutoPrReviewDefaultAdoption"/> is keyed per node, and a search run as one
    /// <c>gh</c> authentication is no evidence at all about a request GitHub made of another
    /// login: deleting on it would clear the other install's row every sweep, which that install
    /// re-records on its own next tick — a delete line and an observe line per standing request
    /// per tick, forever, and a <c>h9k status</c> row that appears or vanishes depending on which
    /// daemon wrote last. The cost of the scope is that a row survives an install
    /// re-authenticating <c>gh</c> as somebody else, since nothing this install can still observe
    /// says whether that request stands; it is recorded evidence about a login, not a guess, and
    /// the honest answer is to leave it rather than delete what can no longer be checked
    /// (AGENTS.md: never guess at unobserved facts).
    /// </para>
    /// </summary>
    private async Task ForgetWithdrawnObservationsAsync(
        string repository, string login,
        IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested, CancellationToken cancellationToken)
    {
        HashSet<int> stillRequested = [.. currentlyRequested.Select(pullRequest => pullRequest.Number)];

        await using IDocumentSession session = store.LightweightSession();
        // lower() on both sides, the same discipline ComputeId's own key already applies: the
        // repository a row recorded is spelled as the observing project's URL spelled it, and
        // GitHub is under no obligation to match that casing from one project to the next.
        IReadOnlyList<ObservedReviewRequest> observed = await session.Query<ObservedReviewRequest>()
            .Where(request => request.MatchesSql("lower(d.data ->> 'repository') = lower(?)", repository))
            .Where(request => request.MatchesSql("lower(d.data ->> 'reviewerLogin') = lower(?)", login))
            .ToListAsync(cancellationToken);

        bool anyForgotten = false;
        foreach (ObservedReviewRequest request in observed
            .Where(request => !stillRequested.Contains(request.Number)))
        {
            session.Delete(request);
            anyForgotten = true;
            logger.LogInformation(
                "Auto-pr-review no longer sees a review of {Repository}#{Number} requested of {Login}; "
                + "clearing its row", repository, request.Number, request.ReviewerLogin);
        }

        if (anyForgotten)
        {
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// One task for one currently review-requested pull request this install has no live task
    /// watching yet — the mirror of <c>TaskAddCommand.RefuseSecondAdoptionAsync</c>'s own dedup
    /// query, since a manually-adopted <c>--from-pr</c> task and an auto-created one share the
    /// identical one-per-item rule (PLAN.md §3.1a): a Done pr-review does not block a fresh
    /// adoption (a completed review does not hold its pull request hostage), so a re-request
    /// after an earlier auto-created review closed mints a fresh task, honestly noted as a
    /// re-review rather than silently indistinguishable from the first one.
    /// <para>
    /// The dedup check runs on the canonical reference the import itself returns
    /// (<see cref="ImportedWorkItem.Reference"/>), never on a reference guessed from
    /// <paramref name="repository"/> before importing — the same discipline
    /// <c>TaskAddCommand.AdoptAsync</c> already follows, and for the identical reason:
    /// <paramref name="repository"/> is parsed from this project's own recorded repository URL,
    /// which is under no obligation to match GitHub's own canonical casing, while every task this
    /// engine has ever created stores the import's own canonical form. A dedup check keyed on a
    /// mismatched guess would never find its own prior task and re-mint a duplicate every sweep —
    /// exactly the failure the one-live-task-per-pull-request rule exists to prevent.
    /// </para>
    /// </summary>
    private async Task<MintAttempt> CreateOneAsync(
        IDocumentSession session, ProjectDetails project, AutoPrReviewSetting setting, string repository,
        ReviewRequestedPullRequest candidate, string login, ReviewRequestActor actor,
        CancellationToken cancellationToken)
    {
        // processRunner threaded through explicitly (independent pre-PR review, cycle 1,
        // adversarial lens): ImporterAsync's own default construction ignores whatever runner it
        // is handed unless asked, which silently shells to the real gh underneath this engine's
        // own injected ProcessRunner and left this whole mint path unreachable by a scripted test.
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken, processRunner: processRunner);
        ImportedWorkItem imported = await importer.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHubPullRequest, $"{repository}#{candidate.Number}", project.RepositoryPath),
            cancellationToken);

        string canonical = imported.Reference.ToString();
        TaskListItem? existing = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            // Newest first for the same reason the fast path above orders: this task's id is
            // recorded against the request and decides whether a line is owed next tick.
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return new MintAttempt(ReviewRequestOutcome.AlreadyCovered, existing.Id, null, actor);
        }

        // Both terminal states, not Done alone (independent pre-PR review, cycle 1, both
        // lenses): an operator who abandons an auto-created review the standing request never
        // cleared must have that stick, exactly like a Done one does, or the very next sweep
        // re-mints it — h9k task abandon cannot decline an auto-created review at all while the
        // request stands otherwise. IsGenuineReRequestAsync below still lets a real re-request
        // through either way; only the same-standing-request case is what this guards.
        // WasAutoPrReviewCreated, not AutoPrReviewAssigneeLogin (independent pre-PR review, cycle
        // 2, adversarial lens): that field is transient and goes null on any recall, even a
        // "work continues" one recorded while this same task's run kept going to Done — reusing
        // it here would silently drop the re-review note for a task recalled mid-run once a real
        // re-request later arrives, because by then AutoPrReviewAssigneeLogin already reads null
        // for reasons unrelated to provenance. WasAutoPrReviewCreated is set once and never
        // cleared, so this only ever finds a task auto-pr-review itself minted — never a task a
        // human created by hand with h9k task add --from-pr, whose own abandonment or closeout
        // says nothing about whether this feature already covered any request at all (independent
        // pre-PR review, cycle 1, adversarial lens: the note below states "auto-created" and this
        // query is what has to make that true rather than assumed).
        TaskListItem? previousReview = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.WasAutoPrReviewCreated)
            .Where(task => task.MatchesSql(
                "d.data ->> 'type' = ? AND d.data ->> 'state' IN (?, ?)",
                TaskType.PrReview.Value, TaskState.Done.Value, TaskState.Abandoned.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (previousReview is not null
            && !await IsGenuineReRequestAsync(session, previousReview, actor.RequestedAt, cancellationToken))
        {
            // Still the same standing request an earlier auto-created review already covered —
            // not a re-review, just a reviewer request GitHub never cleared (walk-pr-review-findings
            // can end with nothing posted, or review resolve can close the task without a GitHub
            // review ever being submitted). Minting again here on every later sweep is the
            // infinite re-mint loop both review lenses found (independent pre-PR review, cycle 1).
            logger.LogDebug(
                "Auto-pr-review skipped {Repository}#{Number}: task {TaskId} already exists for this same "
                + "standing request", repository, candidate.Number, DomainId.Short(previousReview.Id));
            return new MintAttempt(
                ReviewRequestOutcome.AlreadyCovered, previousReview.Id,
                "an earlier auto-created task already covered this same standing request", actor);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string objective = RelayedText.WithoutClosingKeywords(RelayedText.OneLine(imported.Title)).Trim() is { Length: > 0 } seed
            ? seed
            : $"Review pull request {imported.Reference.Key}";

        string provenance = actor.Login is { } assigner
            ? $"GitHub reviewer assignment observed: {assigner} requested {login} as a reviewer"
              + (actor.RequestedAt is { } at ? $" at {at:yyyy-MM-dd HH:mm:ss}Z" : string.Empty) + "."
            : $"GitHub reviewer assignment observed: {login} was requested as a reviewer (the actor who "
              + "requested it could not be read from GitHub's own timeline).";
        // Never previousReview.AddedAt (independent pre-PR review, cycle 1, both lenses):
        // TaskListItem.AddedAt is written once, at TaskAdded, and no Apply ever moves it — it is
        // the earlier task's creation time, not the date it closed or was abandoned, and stating
        // it as the closing date is exactly the plausible-but-unobserved fill-in AGENTS.md's
        // never-guess-at-unobserved-facts rule forbids. TaskListItem carries no close/abandon
        // timestamp to report instead, so the honest fix is to say what is actually observed —
        // the task id and its outcome — and leave the date out rather than mislabel one.
        // Never "an earlier request" (independent pre-PR review, cycle 4, both lenses):
        // control reaches here either because IsGenuineReRequestAsync compared timestamps and
        // found this one postdates the one previousReview was minted from (genuinely a later
        // request), or because previousReview's own stream predates
        // PullRequestReviewAssignmentObserved.RequestedAt and there was nothing to compare
        // against at all (the method's own documented conservative fallback) — a case this note
        // cannot tell apart from the first without re-reading the stream itself, so it states
        // only what is true either way: that an earlier auto-created task existed for this pull
        // request, not that the request it existed for was necessarily a distinct, earlier one.
        // Never "already reviewed this pull request" either (independent pre-PR review, cycle 6,
        // adversarial lens): the most common way an auto-created task reaches Abandoned is
        // ConcludeOneAsync's own pre-dispatch recall, where the task never ran and reviewed
        // nothing, and a Done task can equally be one h9k task resolve closed on a human's
        // attestation with no findings report ever produced. TaskListItem carries no field that
        // says whether a review actually happened, only that the task existed and how it ended —
        // stating "reviewed" would be the same plausible-but-unobserved fill-in the AddedAt
        // comment above already refuses to make. The Abandoned branch drops the "This is a
        // re-review" framing outright, not only the word "reviewed" (independent pre-PR review,
        // cycle 10, adversarial lens): the most common way there — the same pre-dispatch recall —
        // never ran anything, so calling the new request a re-review still asserts the one fact
        // this branch cannot observe. The Done branch keeps it: a Done pr-review task ordinarily
        // did produce a report, so "re-review" is the honest word there.
        string? reReviewNote = previousReview is not null
            ? previousReview.State == TaskState.Abandoned
                ? $"An earlier auto-created task ({DomainId.Short(previousReview.Id)}) existed for this "
                  + "pull request and was abandoned — whether it reviewed anything before that is not recorded."
                : $"This is a re-review: task {DomainId.Short(previousReview.Id)} existed for this "
                  + "pull request and closed Done."
            : null;
        string additionalContext = reReviewNote is null ? provenance : $"{provenance}\n{reReviewNote}";
        // Exactly as h9k task add --from-pr does (independent pre-PR review, cycle 1, conformance
        // lens: the two adoption paths had drifted apart, and an auto-created review carried
        // strictly less to check the diff against than an identical hand-created one).
        string? linkedContext = await LinkedWorkItemImport.TryImportContextAsync(
            session, project, imported, cancellationToken, processRunner: processRunner);
        string composedAdditional = linkedContext.IsNotBlank()
            ? $"{linkedContext}\n\n{additionalContext}"
            : additionalContext;
        string agentContext = WorkItemContext.Compose(imported, composedAdditional);

        string[] criteria =
        [
            "The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed.",
        ];

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, project.Id, objective, criteria, TaskType.PrReview, agentContext, constraints: null,
            imported.Reference, now, node.OwnerId, model: null, blockedBy: null, sourceIdeaId: null, epicId: null);

        PullRequestReviewAssignmentObserved observed = new(
            taskId, imported.Url?.ToString() ?? candidate.Url, login, actor.Login, now, actor.RequestedAt);

        TaskAggregate task = new();
        task.Apply(added);
        task.Apply(observed);

        List<object> events = [added, observed];

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, now, node.OwnerId, project.BacklogPolicy);
        task.Apply(published);
        events.Add(published);

        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, dependencies: [], now, node.OwnerId);
        task.Apply(assigned);
        events.Add(assigned);

        // A Now-speed candidate beyond this sweep's own immediate-launch cap, or while a
        // node-wide launch hold stands, is not silently downgraded: it still takes the
        // queue-first marker First speed uses, so it takes the next free ordinary dispatch slot
        // (behind DispatchEngine's own claim gate, which already refuses to claim into a held
        // node) rather than sitting until the next poll interval.
        bool launchHoldActive = setting.Speed == AutoPrReviewSpeed.Now && await LaunchHoldActiveAsync(cancellationToken);
        bool launchImmediately = setting.Speed == AutoPrReviewSpeed.Now && !launchHoldActive
            && ++_immediateLaunchesThisSweep <= MaxImmediateLaunchesPerSweep;

        Guid? deliberateRunId = null;
        int? deliberateLeaseGeneration = null;
        string? deferral = null;
        if (setting.Speed == AutoPrReviewSpeed.First
            || (setting.Speed == AutoPrReviewSpeed.Now && !launchImmediately))
        {
            if (setting.Speed == AutoPrReviewSpeed.Now)
            {
                // Carried on the observation's own outcome rather than logged on a line of its
                // own (Decisions Log #161): one Info line per pull request is what an
                // orchestrator window's log tail can rely on, so a fact about this pull request
                // belongs in that line rather than beside it.
                deferral = launchHoldActive
                    ? "deferred to the ordinary queue-first slot — a node-wide launch hold stands, so this "
                        + "sweep never claims or launches directly into it"
                    : "deferred to the ordinary queue-first slot — this sweep already used its one "
                        + "immediate ceiling-exempt launch";
            }

            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                now, node.OwnerId, Optional<Guid?>.None, Optional<bool>.Of(true));
            task.Apply(revised);
            events.Add(revised);
        }
        else if (launchImmediately)
        {
            deliberateRunId = DomainId.New();
            // interactiveMode defaults false and stays there: this claim is the daemon's own
            // automated "now"-speed dispatch, not a human at the wheel (task: interactive mode
            // becomes a recorded property of the task) — nobody asked to arbitrate a pr-review
            // task's boundaries by hand, and PrReviewEngine has no boundary this flag could gate
            // anyway.
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, node.OwnerId, deliberateRunId.Value, now, dependencyOverrideAcknowledged: false);
            task.Apply(claimed);
            events.Add(claimed);
            deliberateLeaseGeneration = claimed.LeaseGeneration;
        }

        long claimedVersion = events.Count;
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);

        // No Info line of its own here (Decisions Log #161): the observation this mint answers
        // logs exactly one Info line per pull request, and it names the task, the project, the
        // setting and this outcome — a second line for the same pull request is the noise that
        // rule exists to prevent. The task id is at Debug for a log read at that level.
        logger.LogDebug(
            "Auto-created pr-review task {TaskId} for {Repository}#{Number}, assigned to {Login} at "
            + "{Speed} speed", taskId, repository, candidate.Number, login, setting.Speed.Value);

        if (deliberateRunId is { } runId && deliberateLeaseGeneration is { } generation)
        {
            try
            {
                // The ceiling-exempt sentinel node id, exactly as h9k task start's own claim uses:
                // launched through this daemon's own run-launching mechanism (RunLauncher already
                // knows how to dispatch a pr-review task, per Decisions Log #99 — nothing about
                // that is rebuilt here) rather than waiting for the ordinary claim sweep, which
                // would never pick this run up anyway since NodeLoad never counts a
                // Guid.Empty-claimed run. dispatchingNodeId names this physical daemon so
                // RunSupervisor's own sentinel-run adoption can tell this node's runs apart from
                // another node's sharing the same database.
                await launcher.LaunchAsync(
                    taskId, runId, Guid.Empty, node.OwnerId, generation,
                    dispatchingNodeId: node.NodeId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // RunLauncher.LaunchAsync's own catch handles every ordinary launch failure by
                // failing the task through RecordLaunchFailureAsync, but deliberately excludes
                // cancellation so a daemon shutdown mid-launch propagates rather than being
                // recorded as an agent failure — uncaught here, that would leave this task
                // permanently Claimed with no run stream and nothing that ever recovers it
                // (independent pre-PR review, cycle 1, adversarial lens), the same gap h9k task
                // start's own FailDeliberateClaimAsync closes for the identical deliberate-claim
                // shape.
                await FailDeliberateLaunchAsync(
                    taskId, runId, claimedVersion,
                    "the daemon stopped while launching this auto-created review", CancellationToken.None);
                throw;
            }
        }

        return new MintAttempt(
            ReviewRequestOutcome.TaskCreated, taskId,
            deferral ?? (launchImmediately ? "started immediately, ceiling-exempt" : null), actor);
    }

    /// <summary>
    /// The compensation <c>TaskStartCommand.FailDeliberateClaimAsync</c> runs for the identical
    /// deliberate-claim shape: a fenced check that nothing else already moved this stream past the
    /// claim this call is compensating for, then an honest <see cref="TaskFailed"/> rather than a
    /// permanently stranded Claimed task with no run and no lease.
    /// </summary>
    private async Task FailDeliberateLaunchAsync(
        Guid taskId, Guid runId, long claimedVersion, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        if (fence is null || fence.Version != claimedVersion)
        {
            return;
        }

        TaskAggregate? current = await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken);
        if (current is null || !TaskDecider.CanFail(current))
        {
            return;
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1,
            TaskDecider.Fail(current, runId, $"Auto-pr-review's immediate launch failed: {reason}", DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="currentRequestedAt"/> — the currently-requested candidate's own
    /// most recent <c>ReviewRequestedEvent</c> timestamp — postdates the request
    /// <paramref name="previousReview"/> was minted from, the one fact that tells a genuine
    /// re-request (Alice requests again after every finding was directed) apart from the same
    /// standing request GitHub never cleared (independent pre-PR review, cycle 1, both lenses).
    /// Conservative wherever the evidence is missing: no currently-observed timestamp at all
    /// means there is nothing to prove this is a fresh request, so it is treated as the same
    /// standing one rather than risk the infinite re-mint loop this check exists to close; no
    /// baseline recorded on the previous task (a stream predating <see cref="PullRequestReviewAssignmentObserved.RequestedAt"/>)
    /// means there is nothing to compare against, so any currently-observed timestamp counts as
    /// fresher rather than permanently blocking re-review on tasks this field predates.
    /// <para>
    /// Internal (rather than private) so the dedup-timestamp comparison is directly testable
    /// (test: AutoPrReviewEngine dedup coverage) without also depending on
    /// <c>WorkItemConnections.ImporterAsync</c>'s un-injectable real-<c>gh</c> construction.
    /// </para>
    /// </summary>
    internal static async Task<bool> IsGenuineReRequestAsync(
        IDocumentSession session, TaskListItem previousReview, DateTimeOffset? currentRequestedAt,
        CancellationToken cancellationToken)
    {
        if (currentRequestedAt is not { } current)
        {
            return false;
        }

        PullRequestReviewAssignmentObserved? previousObserved = await MostRecentObservedAsync(
            session, previousReview.Id, cancellationToken);

        return previousObserved?.RequestedAt is not { } previous || current > previous;
    }

    /// <summary>
    /// The most recent <see cref="PullRequestReviewAssignmentObserved"/> this task's own stream
    /// carries — the request (URL and timestamp both) the task was actually minted from, read
    /// fresh from the stream rather than cached on the projection, the same source
    /// <see cref="IsGenuineReRequestAsync"/> already reads on the mint side.
    /// </summary>
    private static async Task<PullRequestReviewAssignmentObserved?> MostRecentObservedAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
        return stream
            .Select(recorded => recorded.Data)
            .OfType<PullRequestReviewAssignmentObserved>()
            .LastOrDefault();
    }

    /// <summary>
    /// Every non-terminal task this feature previously auto-created whose pull request no longer
    /// review-requests this login, per the current sweep's own read — the comparison point is
    /// <see cref="TaskListItem.AutoPrReviewAssigneeLogin"/> itself (set only by this feature), so a
    /// task a human minted by hand with <c>h9k task add --from-pr</c> is never touched here, and a
    /// task already recalled once does not fire again on every later poll.
    /// </summary>
    private async Task<int> ConcludeWithdrawnAsync(
        ProjectDetails project, string repository, string login,
        IReadOnlyList<ReviewRequestedPullRequest> currentlyRequested, CancellationToken cancellationToken)
    {
        HashSet<int> stillRequested = [.. currentlyRequested.Select(pr => pr.Number)];

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> watched = await query.Query<TaskListItem>()
            .Where(task => task.ProjectId == project.Id)
            .Where(task => task.AutoPrReviewAssigneeLogin != null)
            .Where(task => task.MatchesSql("d.data ->> 'type' = ?", TaskType.PrReview.Value))
            .Where(task => task.MatchesSql("d.data ->> 'state' NOT IN (?, ?)", TerminalStates[0], TerminalStates[1]))
            .ToListAsync(cancellationToken);

        int recalled = 0;
        foreach (TaskListItem watchedTask in watched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watchedTask.ExternalReference is not { } reference
                || ExternalReference.Parse(reference) is not { Provider: var provider, Key: { Length: > 0 } key }
                || provider != WorkItemProvider.GitHubPullRequest
                || !int.TryParse(key, out int number)
                || stillRequested.Contains(number))
            {
                continue;
            }

            try
            {
                if (await ConcludeOneAsync(watchedTask, project, repository, number, login, cancellationToken))
                {
                    recalled++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Auto-pr-review could not record the withdrawn assignment for task {TaskId}",
                    watchedTask.Id);
            }
        }

        return recalled;
    }

    /// <summary>Returns whether a recall was actually recorded — false for every path that leaves the task untouched.</summary>
    private async Task<bool> ConcludeOneAsync(
        TaskListItem watchedTask, ProjectDetails project, string repository,
        int number, string login, CancellationToken cancellationToken)
    {
        ReviewRequestActor actor = await reviewAssignments.FindMostRecentRequestActorAsync(
            OwnerFrom(repository), NameFrom(repository), number, login,
            ReviewTimelineEventKind.Removed, project.RepositoryPath, cancellationToken);

        if (!actor.Found)
        {
            // Dropping out of the review-requested search is not proof of a recall (independent
            // pre-PR review, cycle 1, adversarial lens): a merge, a submitted review that cleared
            // the request, or a transient gh failure all look identical from here, and none of
            // them means the assignment was withdrawn. Only a timeline that actually shows the
            // removal event is positive evidence of one — absence alone concludes nothing, and
            // the next sweep's fresh read decides instead.
            logger.LogDebug(
                "Auto-pr-review saw {Repository}#{Number} drop out of the review-requested search for task "
                + "{TaskId}, but the timeline shows no removal event — not concluding a recall this sweep",
                repository, number, watchedTask.Id);
            return false;
        }

        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(watchedTask.Id, cancellationToken);
        if (fence is null)
        {
            return false;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            watchedTask.Id, version: fence.Version, token: cancellationToken);
        if (task is null || task.AutoPrReviewAssigneeLogin is null)
        {
            // Already handled by an earlier tick, or the stream moved since this sweep's own
            // read — a lost race, not a defect: the next poll's own fresh read is authoritative.
            return false;
        }

        // Positive evidence that this removal is the one that actually recalled the request this
        // task was minted from — not just any removal sitting somewhere in the last 20 timeline
        // events, which can be a leftover from an earlier request/re-request cycle whose own
        // removal predates the request that later minted this task (independent pre-PR review,
        // cycle 1, both lenses). Conservative in the same shape IsGenuineReRequestAsync already
        // applies on the mint side: no baseline recorded on this task (a stream predating
        // PullRequestReviewAssignmentObserved.RequestedAt) means there is nothing to compare
        // against, so any removal found counts; a baseline exists but the removal itself carries
        // no timestamp (GitHub's createdAt failed to parse) means there is nothing to prove this
        // evidence is fresh, so it does not conclude a recall. ">=", not ">": GitHub's own
        // createdAt timestamps are second-resolution, and a removal genuinely paired with this
        // task's own minting request can legitimately read identically to it once truncated —
        // a stale removal from a genuinely earlier cycle is strictly, meaningfully earlier than
        // the request that later minted this task, never merely tied with it.
        PullRequestReviewAssignmentObserved? mintObserved = await MostRecentObservedAsync(
            session, watchedTask.Id, cancellationToken);
        bool removalPostdatesMint = mintObserved?.RequestedAt is not { } mintedAt
            || (actor.RequestedAt is { } removedAt && removedAt >= mintedAt);
        if (!removalPostdatesMint)
        {
            logger.LogDebug(
                "Auto-pr-review saw a removal event for {Repository}#{Number} on task {TaskId}, but it "
                + "predates the request the task was minted from — not concluding a recall this sweep",
                repository, number, watchedTask.Id);
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool concludesBeforeDispatch = task.State == TaskState.Published || task.State == TaskState.Queued;

        // The real observed URL from this task's own minting event, matching the sibling Observed
        // event's own field — never a hardcoded github.com URL, which resolves to the wrong
        // repository (or nothing) on a GitHub Enterprise host (independent pre-PR review, cycle 1,
        // adversarial lens), and never the bare "owner/repo#42" canonical reference either, which
        // is a different shape for a different reader.
        string pullRequestUrl = mintObserved?.PullRequestUrl ?? $"https://github.com/{repository}/pull/{number}";
        PullRequestReviewAssignmentRecalled recalled = new(
            task.Id, pullRequestUrl, actor.Login, now, concludesBeforeDispatch);

        if (!concludesBeforeDispatch)
        {
            session.Events.Append(task.Id, recalled);
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Task {TaskId}'s GitHub reviewer assignment was recalled after its run already started — "
                + "recorded as an observation; the work continues", task.Id);
            return true;
        }

        task.Apply(recalled);
        string reason = actor.Login is { } recaller
            ? $"The GitHub reviewer assignment that created this task was recalled by {recaller} before the "
              + "run ever dispatched — the go signal recalled by the same authority that gave it "
              + "(PLAN.md §16 decision #34's amendment)."
            : "The GitHub reviewer assignment that created this task was recalled before the run ever "
              + "dispatched — the go signal recalled by the same authority that gave it "
              + "(PLAN.md §16 decision #34's amendment).";
        TaskAbandoned abandoned = TaskDecider.Abandon(task, reason, now, node.OwnerId);

        // Fenced (independent pre-PR review, cycle 1, both lenses): concludesBeforeDispatch was
        // decided from the aggregate read above, and a claim can commit between that read and
        // this append the same way GenerationFence.LoadFencedAsync's own doc comment warns about
        // — the ordinary claim sweep dispatches independently of this one. An unfenced append
        // would land TaskAbandoned on top of a TaskClaimed it never accounted for, stranding a
        // live agent's worktree and lease under a task that now reads Abandoned. Mirrors
        // FailDeliberateLaunchAsync's identical fenced-compensation shape for the same hazard.
        try
        {
            session.Events.Append(task.Id, expectedVersion: fence.Version + 2, recalled, abandoned);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId} was claimed between this sweep's read and its recall — not concluding it; "
                + "the work continues and the next poll's own fresh read decides instead", task.Id);
            return false;
        }

        logger.LogInformation(
            "Task {TaskId} concluded: its GitHub reviewer assignment was recalled before the run dispatched",
            task.Id);
        return true;
    }

    private static string OwnerFrom(string repository) => repository.Split('/')[0];

    private static string NameFrom(string repository) => repository.Split('/')[1];

    /// <summary>
    /// The mentions half of this sweep (idea 2f079bcd, decision 1): every open pull request
    /// mentioning the install's own login, and every comment on each one that actually names it —
    /// the search alone only proves a pull request carries a mention somewhere, never which
    /// comment. Every comment this method has already decided is skipped outright
    /// (<see cref="ObservedReviewMention"/> is the permanent dedupe record, unlike its sibling
    /// <see cref="ObservedReviewRequest"/>: a comment never stops having been written, so once a
    /// comment id is recorded here it is never re-decided, whatever its outcome was — the same
    /// "a comment id already handled never fires again" rule the request side does not need,
    /// because a standing request can genuinely change and a written comment cannot).
    /// </summary>
    private async Task<int> ObserveMentionsAsync(
        ProjectDetails project, AutoPrReviewSetting setting, string repository, string login,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReviewRequestedPullRequest> mentioned =
            await reviewAssignments.ListMentionedAsync(repository, login, project.RepositoryPath, cancellationToken);

        int created = 0;
        foreach (ReviewRequestedPullRequest candidate in mentioned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<PullRequestMentionComment> comments = await reviewAssignments.FindMentionCommentsAsync(
                OwnerFrom(repository), NameFrom(repository), candidate.Number, login, project.RepositoryPath,
                cancellationToken);

            foreach (PullRequestMentionComment comment in comments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProcessMentionAsync(project, setting, repository, login, cutoff, candidate, comment, cancellationToken))
                {
                    created++;
                }
            }
        }

        return created;
    }

    /// <summary>
    /// One mentioning comment, decided and recorded — returns whether it minted a fresh task, the
    /// one fact <see cref="ObserveMentionsAsync"/> tallies. A comment already recorded is skipped
    /// before anything else runs, including the dedup query the mint path would otherwise pay for
    /// on every single sweep for the life of the pull request.
    /// </summary>
    private async Task<bool> ProcessMentionAsync(
        ProjectDetails project, AutoPrReviewSetting setting, string repository, string login,
        DateTimeOffset cutoff, ReviewRequestedPullRequest candidate, PullRequestMentionComment comment,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        string id = ObservedReviewMention.ComputeId(
            node.NodeId, project.Id, repository, candidate.Number, login, comment.CommentId);
        if (await session.LoadAsync<ObservedReviewMention>(id, cancellationToken) is not null)
        {
            return false;
        }

        (ReviewMentionOutcome Outcome, Guid? TaskId, string? Detail) decision = await DecideMentionAsync(
            session, project, setting, repository, login, cutoff, candidate, comment, cancellationToken);

        ObservedReviewMention observed = new()
        {
            Id = id,
            ObservingNodeId = node.NodeId,
            ProjectId = project.Id,
            Repository = repository,
            Number = candidate.Number,
            PullRequestUrl = candidate.Url,
            MentionedLogin = login,
            CommentId = comment.CommentId,
            CommentAuthorLogin = comment.AuthorLogin,
            CommentBody = comment.Body,
            CommentUrl = comment.Url,
            CommentDatabaseId = comment.DatabaseId,
            CommentCreatedAt = comment.CreatedAt,
            ObservedAt = DateTimeOffset.UtcNow,
            Outcome = decision.Outcome,
            OutcomeDetail = decision.Detail,
            TaskId = decision.TaskId,
        };
        session.Store(observed);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Auto-pr-review observed a mention: {Repository}#{Number} comment {CommentId} by {Author} in "
            + "project {Project} — {Outcome}",
            repository, candidate.Number, comment.CommentId, comment.AuthorLogin, project.Name,
            DescribeMentionOutcome(decision.Outcome, decision.Detail, decision.TaskId));

        return decision.Outcome == ReviewMentionOutcome.TaskCreated;
    }

    private static string DescribeMentionOutcome(ReviewMentionOutcome outcome, string? detail, Guid? taskId)
    {
        string task = taskId is { } id ? DomainId.Short(id) : "none";
        string sentence = ReviewMentionOutcome.FromInput(outcome.Value) switch
        {
            { } known when known == ReviewMentionOutcome.TaskCreated =>
                $"task {task} is created and reviewing",
            { } known when known == ReviewMentionOutcome.Attached =>
                $"attached to task {task}",
            { } known when known == ReviewMentionOutcome.HeldSettingOff =>
                "nothing was created: auto pr-review is off here, so this one is yours to take by hand",
            { } known when known == ReviewMentionOutcome.HeldBeforeCutoff =>
                "nothing was created: the comment predates this project's own auto-pr-review cutoff, so it "
                + "never starts on its own (the no-backfill guard) and is yours to take by hand",
            { } known when known == ReviewMentionOutcome.MintFailed =>
                "nothing was created: the pull request could not be adopted",
            _ => $"an outcome this build does not recognise ({RelayedText.OneLine(outcome.Value)})",
        };

        return detail.IsBlank() ? sentence : $"{sentence} ({detail})";
    }

    /// <summary>
    /// One mentioning comment's own decision, in the same order <see cref="DecideAsync"/> asks its
    /// four questions in, minus the actor-timeline read a mention has no equivalent of (the
    /// comment's own <c>createdAt</c> already IS the fact both the cutoff and the record need):
    /// <list type="number">
    /// <item>Is a live pr-review task already covering this pull request? Attach rather than mint —
    /// never a second task per pull request per install. Attaching always records the mention on
    /// the task's own stream; whether it ALSO dispatches a follow-up lap is gated by the identical
    /// cutoff and setting checks below, computed once here and handed in — a covering task must
    /// never buy an attach-triggered launch a mint on the same comment could not (independent
    /// pre-PR review, cycle 1, both lenses).</item>
    /// <item>Does the comment postdate this project's own cutoff? The no-backfill guard outranks
    /// the setting, exactly as it does on the request side.</item>
    /// <item>Is the setting off here? Then the row is theirs to act on.</item>
    /// <item>Mint.</item>
    /// </list>
    /// </summary>
    private async Task<(ReviewMentionOutcome Outcome, Guid? TaskId, string? Detail)> DecideMentionAsync(
        IDocumentSession session, ProjectDetails project, AutoPrReviewSetting setting, string repository,
        string login, DateTimeOffset cutoff, ReviewRequestedPullRequest candidate, PullRequestMentionComment comment,
        CancellationToken cancellationToken)
    {
        // The identical guessed-reference fast path DecideAsync's own comment explains at length:
        // never trusted as "nothing exists" on its own, only as a cheap check in front of the
        // canonical dedup CreateFromMentionAsync still runs after importing.
        string guessedReference = $"{WorkItemProvider.GitHubPullRequest.Value}:{repository}#{candidate.Number}";
        TaskListItem? likelyCovering = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql("lower(d.data ->> 'externalReference') = lower(?)", guessedReference))
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);

        bool pastCutoff = AutoPrReviewCutoff.StartsOnItsOwn(comment.CreatedAt, cutoff);
        if (likelyCovering is not null)
        {
            return await AttachMentionAsync(session, likelyCovering, setting, pastCutoff, candidate, comment, cancellationToken);
        }

        if (!pastCutoff)
        {
            return (ReviewMentionOutcome.HeldBeforeCutoff, null, null);
        }

        if (!setting.IsOn)
        {
            return (ReviewMentionOutcome.HeldSettingOff, null, null);
        }

        try
        {
            return await CreateFromMentionAsync(
                session, project, setting, repository, candidate, login, comment, pastCutoff, cancellationToken);
        }
        catch (DomainException exception)
        {
            logger.LogWarning(
                exception, "Auto-pr-review could not mint a mention-triggered task for {Repository}#{Number}",
                repository, candidate.Number);
            return (ReviewMentionOutcome.MintFailed, null, RelayedText.OneLine(exception.Message));
        }
    }

    /// <summary>
    /// Attaches a mention to a live pr-review task that already covers this pull request — never a
    /// second task (idea 2f079bcd, decision 2). The attach itself — recording the mention on the
    /// task's own stream — always happens: it costs nothing and keeps the record honest whatever
    /// the setting says, exactly as an observed review request is recorded whatever the setting
    /// says. Dispatching the bounded follow-up lap is a second, separate decision, gated on all of:
    /// <list type="number">
    /// <item>the task is currently eligible for one at all
    /// (<see cref="TaskDecider.AwaitsPrReviewMentionFollowUp"/>: the report is already parked and
    /// unresolved, or the task is waiting on the pull request or the author) — never for a task
    /// that is still Queued, Blocked, or actively being reviewed by a live run, where the mention
    /// is recorded for the record and picked up by whatever runs next rather than interrupting it;</item>
    /// <item><paramref name="pastCutoff"/> and <paramref name="setting"/> being on — the identical
    /// no-backfill and off-silences-it guards a mint answers to, since dispatching here is exactly
    /// as much a "start on its own" action as a mint is (independent pre-PR review, cycle 1, both
    /// lenses: this ordering used to attach-and-launch before ever asking either question); and</item>
    /// <item>this sweep's own shared ceiling — a node-wide launch hold, or the one ceiling-exempt
    /// immediate launch <see cref="MaxImmediateLaunchesPerSweep"/> allows across BOTH triggers —
    /// since this claim uses the identical ceiling-exempt sentinel a mint's own Now-speed launch
    /// does, and bypasses the ordinary dispatcher exactly as that launch does.</item>
    /// </list>
    /// A comment id is a one-shot dedup key (the class doc on <see cref="ObservedReviewMention"/>),
    /// so a mention held back by the ceiling or a launch hold is not retried automatically — it
    /// stays attached and visible on the task (an owner can always run
    /// <c>h9k pr review --since-my-review</c> by hand), rather than silently reappearing to
    /// automation on some later sweep with no record of ever having been seen before.
    /// </summary>
    private async Task<(ReviewMentionOutcome Outcome, Guid? TaskId, string? Detail)> AttachMentionAsync(
        IDocumentSession session, TaskListItem existing, AutoPrReviewSetting setting, bool pastCutoff,
        ReviewRequestedPullRequest candidate, PullRequestMentionComment comment, CancellationToken cancellationToken)
    {
        StreamState? fence = await session.Events.FetchStreamStateAsync(existing.Id, cancellationToken);
        if (fence is null)
        {
            return (ReviewMentionOutcome.MintFailed, null, "the covering task's own stream could not be read");
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            existing.Id, version: fence.Version, token: cancellationToken);
        if (task is null)
        {
            return (ReviewMentionOutcome.MintFailed, null, "the covering task's own stream could not be read");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PullRequestReviewMentionObserved observed = TaskDecider.ObservePrReviewMention(
            task, candidate.Url, comment.CommentId, comment.AuthorLogin, comment.Body, comment.Url,
            comment.CreatedAt, now, comment.DatabaseId);

        bool reportParkedAwaitingWalk = await IsReportParkedAwaitingWalkAsync(session, task, cancellationToken);
        if (!TaskDecider.AwaitsPrReviewMentionFollowUp(task, reportParkedAwaitingWalk))
        {
            session.Events.Append(existing.Id, expectedVersion: fence.Version + 1, observed);
            await session.SaveChangesAsync(cancellationToken);
            return (
                ReviewMentionOutcome.Attached, existing.Id,
                "recorded; the task is not currently waiting on its pull request or holding an unwalked "
                + "report, so no follow-up was dispatched");
        }

        if (!pastCutoff)
        {
            session.Events.Append(existing.Id, expectedVersion: fence.Version + 1, observed);
            await session.SaveChangesAsync(cancellationToken);
            return (
                ReviewMentionOutcome.Attached, existing.Id,
                "recorded; the comment predates this project's own auto-pr-review cutoff, so no follow-up "
                + "was dispatched (the no-backfill guard) — yours to take by hand");
        }

        if (!setting.IsOn)
        {
            session.Events.Append(existing.Id, expectedVersion: fence.Version + 1, observed);
            await session.SaveChangesAsync(cancellationToken);
            return (
                ReviewMentionOutcome.Attached, existing.Id,
                "recorded; auto-pr-review is off here, so no follow-up was dispatched — yours to take by hand");
        }

        // Unconditional on setting.Speed, unlike a mint's own Now-only check: a follow-up has no
        // First-speed queued alternative to fall back to (ClaimForMentionFollowUp always uses the
        // ceiling-exempt sentinel), so the hold and the shared ceiling are the only things standing
        // between this claim and a broken or already-spent node (independent pre-PR review, cycle
        // 1, adversarial lens).
        bool launchHoldActive = await LaunchHoldActiveAsync(cancellationToken);
        bool launchImmediately = !launchHoldActive && ++_immediateLaunchesThisSweep <= MaxImmediateLaunchesPerSweep;
        if (!launchImmediately)
        {
            session.Events.Append(existing.Id, expectedVersion: fence.Version + 1, observed);
            await session.SaveChangesAsync(cancellationToken);
            string reason = launchHoldActive
                ? "a node-wide launch hold stands, so this sweep never claims or launches directly into it"
                : "this sweep already used its one immediate ceiling-exempt launch across both triggers";
            return (
                ReviewMentionOutcome.Attached, existing.Id,
                $"recorded; {reason} — yours to take by hand with h9k pr review --since-my-review");
        }

        Guid runId = DomainId.New();
        // The original review's own run — the one holding review-1-findings.md — rather than
        // task.PrReviewFollowThroughRunId, which OpenPrReviewFollowThrough overwrites with whatever
        // run most recently opened the follow-through, including an earlier follow-up's own run
        // with no findings file of its own (independent pre-PR review, cycle 1, adversarial lens).
        // RunIds[0] is always that original review, because a pr-review task's very first dispatch
        // is always its adversarial-lens review (RunLauncher's own isPrReview branch).
        Guid? priorReviewRunId = task.RunIds.Count > 0 ? task.RunIds[0] : null;
        TaskClaimed claimed = TaskDecider.ClaimForMentionFollowUp(task, node.OwnerId, runId, now, reportParkedAwaitingWalk);
        session.Events.Append(existing.Id, expectedVersion: fence.Version + 2, observed, claimed);
        long claimedVersion = fence.Version + 2;
        await session.SaveChangesAsync(cancellationToken);

        try
        {
            await launcher.LaunchPrReviewMentionFollowUpAsync(
                existing.Id, runId, node.OwnerId, claimed.LeaseGeneration, node.NodeId, comment, priorReviewRunId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The identical fenced compensation FailDeliberateLaunchAsync runs for a mint's own
            // deliberate claim (independent pre-PR review, cycle 1, adversarial lens): uncaught,
            // a daemon shutdown mid-launch would leave this task permanently Claimed under a run
            // with no stream and nothing that ever recovers it.
            await FailDeliberateLaunchAsync(
                existing.Id, runId, claimedVersion,
                "the daemon stopped while launching this mention follow-up", CancellationToken.None);
            throw;
        }

        return (
            ReviewMentionOutcome.Attached, existing.Id,
            "attached, and a bounded follow-up lap was dispatched to answer it");
    }

    /// <summary>
    /// Whether this pr-review task's own report is parked and not yet resolved — a fact that lives
    /// entirely on the run stream (<c>PrReviewEngine.ComposeReportAndParkAsync</c> leaves the task
    /// itself Claimed and parks its current run in <c>RunState.ReviewParked</c>), so
    /// <see cref="TaskAggregate"/> alone can never answer it. Read here, once, and handed to the
    /// domain as an observed fact (<see cref="TaskDecider.AwaitsPrReviewMentionFollowUp"/>).
    /// </summary>
    private async Task<bool> IsReportParkedAwaitingWalkAsync(
        IDocumentSession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.Type != TaskType.PrReview || task.State != TaskState.Claimed || task.CurrentRunId is not { } runId)
        {
            return false;
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        return run?.State == RunState.ReviewParked;
    }

    /// <summary>
    /// One fresh pr-review task for a mention on a pull request no live task covers (idea 2f079bcd,
    /// decision 2) — the mirror of <see cref="CreateOneAsync"/>, minus the re-request-genuineness
    /// dance that method needs and this one does not: a comment id is unique forever, so there is
    /// no "same standing request" a later sweep could ever confuse a fresh comment with. A Done or
    /// Abandoned task's own prior coverage is exactly as silent here as it is on the request side —
    /// nothing about a closed-out earlier review blocks a fresh mint.
    /// </summary>
    private async Task<(ReviewMentionOutcome Outcome, Guid? TaskId, string? Detail)> CreateFromMentionAsync(
        IDocumentSession session, ProjectDetails project, AutoPrReviewSetting setting, string repository,
        ReviewRequestedPullRequest candidate, string login, PullRequestMentionComment comment, bool pastCutoff,
        CancellationToken cancellationToken)
    {
        WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken, processRunner: processRunner);
        ImportedWorkItem imported = await importer.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHubPullRequest, $"{repository}#{candidate.Number}", project.RepositoryPath),
            cancellationToken);

        string canonical = imported.Reference.ToString();
        TaskListItem? existing = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .Where(task => task.MatchesSql(
                "NOT (d.data ->> 'type' = ? AND d.data ->> 'state' = ?)",
                TaskType.PrReview.Value, TaskState.Done.Value))
            .OrderByDescending(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            // The canonical dedup caught a live task the guessed-reference fast path in
            // DecideMentionAsync missed (a casing mismatch) — attach, the same discipline
            // CreateOneAsync's own identical canonical re-check follows. pastCutoff is already
            // known true and setting.IsOn already known on here — DecideMentionAsync only reaches
            // this method after both gates passed.
            return await AttachMentionAsync(session, existing, setting, pastCutoff, candidate, comment, cancellationToken);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string objective = RelayedText.WithoutClosingKeywords(RelayedText.OneLine(imported.Title)).Trim() is { Length: > 0 } seed
            ? seed
            : $"Review pull request {imported.Reference.Key}";

        string provenance = $"GitHub mention observed: {comment.AuthorLogin} tagged {login} in a comment on "
            + $"this pull request at {comment.CreatedAt:yyyy-MM-dd HH:mm:ss}Z.";
        string? linkedContext = await LinkedWorkItemImport.TryImportContextAsync(
            session, project, imported, cancellationToken, processRunner: processRunner);
        string composedAdditional = linkedContext.IsNotBlank() ? $"{linkedContext}\n\n{provenance}" : provenance;
        string agentContext = WorkItemContext.Compose(imported, composedAdditional);

        string[] criteria =
        [
            "The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed.",
            "The report's own \"You were asked\" section answers the tagged comment that minted this task.",
        ];

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, project.Id, objective, criteria, TaskType.PrReview, agentContext, constraints: null,
            imported.Reference, now, node.OwnerId, model: null, blockedBy: null, sourceIdeaId: null, epicId: null);

        PullRequestReviewMentionObserved observed = new(
            taskId, imported.Url?.ToString() ?? candidate.Url, comment.CommentId, comment.AuthorLogin,
            comment.Body, comment.Url, comment.CreatedAt, now, comment.DatabaseId);

        TaskAggregate task = new();
        task.Apply(added);
        task.Apply(observed);

        List<object> events = [added, observed];

        TaskPublished published = TaskDecider.Publish(
            task, TaskDependencyGraph.Empty, now, node.OwnerId, project.BacklogPolicy);
        task.Apply(published);
        events.Add(published);

        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, dependencies: [], now, node.OwnerId);
        task.Apply(assigned);
        events.Add(assigned);

        // The identical ceiling-exempt-launch bookkeeping CreateOneAsync's own tail keeps, for the
        // identical reason: one immediate launch per sweep across BOTH triggers, since the consent
        // text a human agreed to at --auto-pr-review now still promises "an extra concurrent agent
        // session", singular, whichever trigger asked for it.
        bool launchHoldActive = setting.Speed == AutoPrReviewSpeed.Now && await LaunchHoldActiveAsync(cancellationToken);
        bool launchImmediately = setting.Speed == AutoPrReviewSpeed.Now && !launchHoldActive
            && ++_immediateLaunchesThisSweep <= MaxImmediateLaunchesPerSweep;

        Guid? deliberateRunId = null;
        int? deliberateLeaseGeneration = null;
        string? deferral = null;
        if (setting.Speed == AutoPrReviewSpeed.First
            || (setting.Speed == AutoPrReviewSpeed.Now && !launchImmediately))
        {
            if (setting.Speed == AutoPrReviewSpeed.Now)
            {
                deferral = launchHoldActive
                    ? "deferred to the ordinary queue-first slot — a node-wide launch hold stands, so this "
                        + "sweep never claims or launches directly into it"
                    : "deferred to the ordinary queue-first slot — this sweep already used its one "
                        + "immediate ceiling-exempt launch";
            }

            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                now, node.OwnerId, Optional<Guid?>.None, Optional<bool>.Of(true));
            task.Apply(revised);
            events.Add(revised);
        }
        else if (launchImmediately)
        {
            deliberateRunId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, node.OwnerId, deliberateRunId.Value, now, dependencyOverrideAcknowledged: false);
            task.Apply(claimed);
            events.Add(claimed);
            deliberateLeaseGeneration = claimed.LeaseGeneration;
        }

        long claimedVersion = events.Count;
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Auto-created pr-review task {TaskId} for {Repository}#{Number} from a mention by {Author}, at "
            + "{Speed} speed", taskId, repository, candidate.Number, comment.AuthorLogin, setting.Speed.Value);

        if (deliberateRunId is { } runId && deliberateLeaseGeneration is { } generation)
        {
            try
            {
                await launcher.LaunchAsync(
                    taskId, runId, Guid.Empty, node.OwnerId, generation,
                    dispatchingNodeId: node.NodeId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FailDeliberateLaunchAsync(
                    taskId, runId, claimedVersion,
                    "the daemon stopped while launching this auto-created review", CancellationToken.None);
                throw;
            }
        }

        return (
            ReviewMentionOutcome.TaskCreated, taskId,
            deferral ?? (launchImmediately ? "started immediately, ceiling-exempt" : null));
    }
}
