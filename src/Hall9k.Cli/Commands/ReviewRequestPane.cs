using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>The task covering one observed review request, as the row needs to speak about it.</summary>
/// <param name="Live">Whether it is still running or waiting — a task nobody has closed out yet.</param>
/// <param name="StateWord">
/// The pane's own word for where the task is (Working, Delivered, Done, …), taken from the same
/// <see cref="AttentionBucket"/> the sections are named after so the row and the section it sits
/// above cannot disagree.
/// </param>
/// <param name="AutoCreated">
/// Whether auto pr-review itself minted a task for this pull request — the one this row names or
/// any other on the same reference, read off <see cref="TaskListItem.WasAutoPrReviewCreated"/>,
/// because that is what the engine's own re-mint guard is keyed on. It decides what a closed
/// covering task lets the row promise: the guard never matches a hand-adopted
/// <c>--from-pr</c> task, so one of those closed while the request still stands holds nothing
/// back at all (independent pre-PR review, cycle 1, conformance lens).
/// </param>
/// <param name="GateParked">
/// Whether the covering task itself is still the membership gate's own park — published but never
/// assigned (security review idea 6be68ee2, finding 1) — read off
/// <see cref="TaskListItem.PrReviewGateParked"/> together with its still-Published state, the
/// identical pair <c>AttentionComposer</c>'s own needs-you row already gates on. A mention row
/// checks this ahead of the ordinary "already covers it" case (independent pre-PR review, cycle 1,
/// conformance lens): a task that merely exists is not the same fact as a task an operator still
/// has to run <c>h9k task assign</c> on, and a still-parked task's own mint mention must not read
/// as already handled.
/// </param>
/// <param name="Own">
/// Whether the covering task is the viewer's own. A row names a covering task, its id and its
/// state, only when it is: a teammate's task covers the request as far as the dedup is concerned,
/// but its id, its state and any lever against it are that owner's to read, and a line saying
/// "task 1a2b3c is reviewing" about it would hand this viewer a task they cannot act on. Defaults
/// true so a caller that cannot tell (no row to read the group from) keeps the pre-existing wording.
/// </param>
internal sealed record CoveringReview(
    Guid TaskId, bool Live, string StateWord, bool AutoCreated, bool GateParked, bool Own = true);

/// <summary>One observed review request as <c>h9k status</c> renders it.</summary>
/// <param name="NeedsYou">
/// Whether the operator is the one who has to act. False whenever the daemon is already doing
/// the work: a busy login is never nagged for a review a task is already running (Decisions Log
/// #161).
/// </param>
internal sealed record ReviewRequestRow(bool NeedsYou, string Markup, string Repository, int Number);

/// <summary>
/// What <c>h9k status</c> prints for this feature: one setting line per project, always, and one
/// row per review request GitHub is currently making of this install's own login.
/// </summary>
internal sealed record ReviewRequestPaneContents(
    IReadOnlyList<string> SettingLines, IReadOnlyList<ReviewRequestRow> Requests)
{
    /// <summary>Needs-you rows first, then the informational ones, each in the order they were composed.</summary>
    public IEnumerable<ReviewRequestRow> InReadingOrder =>
        Requests.Where(request => request.NeedsYou).Concat(Requests.Where(request => !request.NeedsYou));
}

/// <summary>
/// The review-request half of the attention pane (Decisions Log #161): what GitHub has asked of
/// this install's own login, what became of each request, and — where nothing started — the lever
/// that actually ends that wait, which is both commands where the setting held it and
/// <c>h9k task add --from-pr</c> alone where the no-backfill cutoff did.
/// <para>
/// It reads recorded facts only, in <see cref="AttentionComposer"/>'s own discipline: the
/// observation row the daemon's sweep wrote, this project's effective setting resolved from its
/// own stream, and whichever task actually covers the pull request now. The last two are resolved
/// at render time rather than trusted from the row, which is what makes a row clear the moment a
/// task adopts the request or the setting is turned on, instead of at the next three-minute
/// sweep.
/// </para>
/// </summary>
internal static class ReviewRequestPane
{
    /// <summary>
    /// One line per project, always — at the default too. The origin incident (2026-09-08) was
    /// not a wrong setting but an invisible one: auto pr-review sat off on both nodes for three
    /// days with four review requests accumulating and nothing anywhere saying so.
    /// </summary>
    internal static IReadOnlyList<string> SettingLines(
        IEnumerable<(string Project, AutoPrReviewSetting Setting)> projects) =>
        [
            .. projects
                .OrderBy(project => project.Project, StringComparer.OrdinalIgnoreCase)
                .Select(project => project.Setting.IsOn
                    ? $"[dim]auto pr-review: [/][green]on[/][dim] for project "
                      + $"'{project.Project.EscapeMarkup()}' ({project.Setting.Speed.Value.ToLowerInvariant()}, "
                      + $"{project.Setting.Origin}) — a review GitHub requests of this install's own login, or "
                      + "a comment that mentions it, here mints a pr-review task[/]"
                    // Turning the setting back on only retries a held REQUEST — a standing request
                    // is re-graded every sweep, so the lever named here genuinely clears it. A held
                    // MENTION is a one-shot dedupe (ObservedReviewMention's own class doc: a comment
                    // id already decided is never re-decided, whatever its outcome), so this lever
                    // does nothing for one already seen while off — it stays yours to take by hand
                    // from the comment itself (independent pre-PR review, cycle 1, adversarial
                    // lens, low: the line used to promise this lever cleared both).
                    : $"[dim]auto pr-review: [/][yellow]off[/][dim] for project "
                      + $"'{project.Project.EscapeMarkup()}' ({project.Setting.Speed.Value.ToLowerInvariant()}, "
                      + $"{project.Setting.Origin}) — a review request here waits for you:[/] "
                      + $"h9k project set {project.Project.EscapeMarkup()} --auto-pr-review normal"
                      + "[dim] (a mention seen while off is not retried by this — answer it by hand)[/]"),
        ];

    /// <summary>
    /// The whole pane in one read: the always-printed per-project setting lines and every
    /// observed request rendered. <paramref name="rows"/> is the pane's own already-composed task
    /// rows, used only to name where a covering task is — never re-derived here, so this pane and
    /// the sections below it cannot disagree about a task's state.
    /// <para>
    /// Two rows that render to the same words print once (independent pre-PR review, cycle 1,
    /// adversarial lens, medium). An observation row is keyed per decider — the node and project
    /// whose own setting, registration and adoption moment graded the request
    /// (<see cref="ObservedReviewRequest.ComputeId"/>) — so a repository two projects both point
    /// at, or a database two installs share under one <c>gh</c> login, yields a row per decider
    /// for one request. Where they agree, the reader has one thing to be told and is told it
    /// once; where they genuinely disagree, both rows stand, each naming the project its own
    /// lever needs. Deduplicating on the composed markup rather than on the pull request is what
    /// makes that distinction: the markup already carries the repository, the number, the project
    /// its lever names and the verdict, so two rows collapse only when there is nothing to tell
    /// apart.
    /// </para>
    /// </summary>
    internal static async Task<ReviewRequestPaneContents> ComposeAllAsync(
        IQuerySession session, IReadOnlyList<TaskStatusRow> rows, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        IReadOnlyDictionary<Guid, AutoPrReviewSetting> settings = await AutoPrReviewSetting.ResolveAllAsync(
            session, projects.Select(project => project.Id), cancellationToken);
        IReadOnlyList<string> settingLines = SettingLines(
            projects.Select(project => (project.Name, settings[project.Id])));

        IReadOnlyList<ObservedReviewRequest> observed =
            await session.Query<ObservedReviewRequest>().ToListAsync(cancellationToken);
        // Every mention this install ever left with nothing that fully answered it, never
        // retried (ObservedReviewMention's own class doc: a comment id already decided is never
        // re-decided) — the needs-you row an operator has to act on is only possible if one of
        // these is surfaced somewhere, and until now nothing in Hall9k did (independent pre-PR
        // review, cycle 1, adversarial lens). Excludes only the two outcomes that need nothing
        // further: TaskCreated (a fresh task is reviewing) and Attached (a follow-up lap was
        // actually dispatched to answer this exact comment) — every other outcome, including one
        // this build cannot even read, is surfaced rather than silently dropped (independent
        // pre-PR review, cycle 1, adversarial lens, low: a row whose outcome reads as Unknown used
        // to vanish here with nothing shown at all).
        IReadOnlyList<ObservedReviewMention> unresolvedMentions = [.. (await session.Query<ObservedReviewMention>()
            .ToListAsync(cancellationToken))
            .Where(mention => mention.Outcome != ReviewMentionOutcome.TaskCreated
                && mention.Outcome != ReviewMentionOutcome.Attached)];
        if (observed.Count == 0 && unresolvedMentions.Count == 0)
        {
            return new ReviewRequestPaneContents(settingLines, []);
        }

        Dictionary<Guid, ProjectDetails> byId = projects.ToDictionary(project => project.Id);
        IReadOnlyDictionary<Guid, string> leaderNames = await HoldLeaderNamesAsync(session, observed, cancellationToken);
        IReadOnlyList<TaskListItem> adopted = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference != null)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, TaskStatusRow> rowsByTask = rows.ToDictionary(row => row.TaskId);

        List<ReviewRequestRow> rendered = [];
        HashSet<string> alreadySaid = [];
        foreach (ObservedReviewRequest request in observed
            .OrderBy(request => request.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.Number)
            // Oldest sighting first among the deciders of one request, so which of two agreeing
            // rows is the one kept below is a recorded fact rather than whatever order Postgres
            // handed them back in.
            .ThenBy(request => request.FirstObservedAt))
        {
            // A row whose project has since been forgotten names no project and no lever that
            // would work, so it is dropped rather than rendered against a name nothing can
            // resolve — the sweep stops writing it the moment the project is gone.
            if (!byId.TryGetValue(request.ProjectId, out ProjectDetails? project))
            {
                continue;
            }

            ReviewRequestRow row = Compose(
                request, project.Name, settings[request.ProjectId],
                Covering(request.Repository, request.Number, adopted, rowsByTask), now,
                request.HoldLeaderNodeId is { } leaderId ? leaderNames.GetValueOrDefault(leaderId) : null);
            // Two deciders that reached the same answer about one request have one thing to say,
            // and say it once (see this method's own remarks).
            if (alreadySaid.Add(row.Markup))
            {
                rendered.Add(row);
            }
        }

        foreach (ObservedReviewMention mention in unresolvedMentions
            .OrderBy(mention => mention.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mention => mention.Number)
            .ThenBy(mention => mention.ObservedAt))
        {
            if (!byId.TryGetValue(mention.ProjectId, out ProjectDetails? project))
            {
                continue;
            }

            // The task the daemon attached the mention to is judged from its own row, not from the
            // covering task the board prefers: a mention recorded before the engine began
            // answering to the owner rule attached to the newest covering task whoever owned it,
            // so the two can differ (independent pre-PR review, cycle 1, both lenses, medium).
            bool attachedToTeammate = mention.TaskId is { } attachedId
                && rowsByTask.TryGetValue(attachedId, out TaskStatusRow? attachedRow)
                && attachedRow.IsTeammates;
            ReviewRequestRow row = ComposeMentionRow(
                mention, project.Name, Covering(mention.Repository, mention.Number, adopted, rowsByTask),
                attachedToTeammate);
            if (alreadySaid.Add(row.Markup))
            {
                rendered.Add(row);
            }
        }

        return new ReviewRequestPaneContents(settingLines, rendered);
    }

    /// <summary>
    /// The machine name of every fleet node a row is being held for, where this store knows the
    /// node at all. A peer this store has no registration for is simply absent, and its row names
    /// the node by its short id alone rather than by a name nothing observed.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> HoldLeaderNamesAsync(
        IQuerySession session, IReadOnlyList<ObservedReviewRequest> observed, CancellationToken cancellationToken)
    {
        Guid[] leaderIds = [.. observed.Where(request => request.HoldLeaderNodeId is not null)
            .Select(request => request.HoldLeaderNodeId.GetValueOrDefault())
            .Distinct()];
        if (leaderIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        IReadOnlyList<NodeDetails> nodes = await session.Query<NodeDetails>()
            .Where(node => leaderIds.Contains(node.Id))
            .ToListAsync(cancellationToken);
        return nodes
            .Where(node => !node.MachineName.IsBlank())
            .ToDictionary(node => node.Id, node => node.MachineName);
    }

    /// <summary>
    /// The task covering this pull request right now, if any: a live one wins over a closed one,
    /// the viewer's own over a teammate's within each, and the newest closed one is what is named
    /// when only closed ones exist. Matched on the
    /// canonical external reference, case-insensitively — GitHub's own casing for an
    /// <c>owner/repo</c> need not match what a project recorded (the same hazard
    /// <c>ObservedReviewRequest.ComputeId</c> lower-cases for).
    /// <para>
    /// <see cref="CoveringReview.AutoCreated"/> is asked of every one of the viewer's own tasks
    /// matching the reference rather than only of the one this row names, because that is the shape
    /// of the engine guard it stands in for: <c>CreateOneAsync</c>'s re-mint guard looks for
    /// <em>any</em> terminal auto-created task of this owner's on the reference, so a closed
    /// hand-adopted task that happens to be the newest must not make the row promise a mint the
    /// older auto-created one beside it holds back, and a teammate's auto-created one holds
    /// nothing back at all.
    /// </para>
    /// </summary>
    internal static CoveringReview? Covering(
        string repository, int number, IReadOnlyList<TaskListItem> adopted,
        IReadOnlyDictionary<Guid, TaskStatusRow> rowsByTask)
    {
        string reference = $"{WorkItemProvider.GitHubPullRequest.Value}:{repository}#{number}";
        IReadOnlyList<TaskListItem> matching = [.. adopted
            .Where(task => string.Equals(task.ExternalReference, reference, StringComparison.OrdinalIgnoreCase))];
        if (matching.Count == 0)
        {
            return null;
        }

        // A live task of any owner outranks a closed one, so a viewer's old Done task never hides a
        // teammate's live review of the same pull request. Within each tier the viewer's own task is
        // chosen ahead of a teammate's however old, because it is the only kind this row may name;
        // a teammate's is chosen only to say that one covers the request, without naming it.
        static bool IsLive(TaskListItem task) => task.State != TaskState.Done && task.State != TaskState.Abandoned;
        IReadOnlyList<TaskListItem> own = [.. matching.Where(task => IsViewers(task, rowsByTask))];
        TaskListItem? live = NewestOrNull(own.Where(IsLive))
            ?? NewestOrNull(matching.Where(task => IsLive(task) && !own.Contains(task)));
        TaskListItem covering = live
            ?? NewestOrNull(own)
            ?? matching.OrderByDescending(task => task.AddedAt).First();
        bool coveringIsOwn = own.Contains(covering);
        string stateWord = rowsByTask.TryGetValue(covering.Id, out TaskStatusRow? row)
            ? row.Group.ToString()
            : covering.State.Value;
        // The identical pair AttentionComposer's own needs-you row gates the membership gate's park
        // on: PrReviewGateParked never clears once set, so State == Published is what tells "still
        // parked, never assigned" apart from "parked once, long since assigned and gone Done".
        bool gateParked = coveringIsOwn && covering.PrReviewGateParked && covering.State == TaskState.Published;
        return new CoveringReview(
            covering.Id, live is not null, stateWord, own.Any(task => task.WasAutoPrReviewCreated), gateParked,
            coveringIsOwn);
    }

    private static TaskListItem? NewestOrNull(IEnumerable<TaskListItem> tasks) =>
        tasks.OrderByDescending(task => task.AddedAt).FirstOrDefault();

    /// <summary>
    /// Whether a task is the viewer's, read from the pane's own composed rows so this pane and the
    /// sections below it cannot disagree. A task with no row (a headless one the pane filtered out)
    /// carries no verdict either way and keeps the pre-existing reading.
    /// </summary>
    private static bool IsViewers(TaskListItem task, IReadOnlyDictionary<Guid, TaskStatusRow> rowsByTask) =>
        !(rowsByTask.TryGetValue(task.Id, out TaskStatusRow? row) && row.IsTeammates);

    /// <summary>
    /// One row's words. The order the cases are asked in is the whole behaviour (Decisions Log
    /// #161): a task covering the request outranks everything, because there is then nothing to
    /// ask of anyone; the no-backfill hold outranks the setting, because turning the setting on
    /// would not start a stale request and a row must never name a lever that changes nothing;
    /// and an off project is the last of the needs-you cases rather than the first.
    /// <para>
    /// The outcome is re-read through <see cref="ReviewRequestOutcome.FromInput"/> rather than
    /// compared as stored, so a row written by a later build carrying an outcome this one has
    /// never heard of is described as unrecognised instead of silently falling into whichever
    /// case happens to be last.
    /// </para>
    /// </summary>
    internal static ReviewRequestRow Compose(
        ObservedReviewRequest request, string projectName, AutoPrReviewSetting setting,
        CoveringReview? covering, DateTimeOffset now, string? holdLeaderName = null)
    {
        string pullRequest = $"{request.Repository.EscapeMarkup()}#{request.Number}";
        string project = projectName.EscapeMarkup();
        // The login the request was actually made of, never "you" (independent pre-PR review,
        // cycle 1, adversarial lens, low): a row is keyed per reviewer login precisely because
        // two installs with two gh authentications can share one database, and this pane reads
        // recorded facts only — it has no observation of which login the reader is, so asserting
        // one would be the plausible-but-unobserved fill-in AGENTS.md forbids. Naming the login
        // costs the ordinary single-login install nothing and is the whole answer on a shared
        // one, where a red row for the other install's request otherwise reads as the reader's
        // own and invites a by-hand adoption that duplicates their work. A row recorded before
        // the login was part of the key carries none, and says so rather than filling one in.
        string requestedOf = request.ReviewerLogin.IsBlank()
            ? "was requested of a login this row does not record"
            : $"was requested of {request.ReviewerLogin.EscapeMarkup()}";
        string opening = $"a review of {pullRequest} {requestedOf}";
        // Empty rather than a parenthetical when GitHub's own time could not be read: the one row
        // that turns on that absence — the request-time-unknown hold below — says so in its own
        // cause, and every other row would only be repeating a caveat that changes nothing about
        // what it is telling the reader to do.
        string age = request.RequestedAt is { } requestedAt
            ? $" {TaskStatusComposer.RelativeAge(now - requestedAt)}"
            : string.Empty;
        string byHand = $"h9k task add --project {project} --from-pr {request.Number}";
        ReviewRequestOutcome outcome = ReviewRequestOutcome.FromInput(request.Outcome.Value);

        if (covering is { Own: true } task)
        {
            string id = DomainId.Short(task.TaskId);
            return Informational(
                request.Repository, request.Number,
                task.Live
                    ? $"{opening}{age}; task {id} is created and reviewing ({task.StateWord})"
                    : $"{opening}{age}; task {id} already covered it ({task.StateWord}) — {ClosedTaskHold(setting, task)}");
        }

        // A teammate's task covers nothing for this viewer, because the engine answers a request
        // only with a task of the viewer's own (OwnerScopedCoverage), so it never outranks the
        // held and needs-you outcomes below: the lever those name is still the viewer's. Only the
        // outcome the engine records when a task it cannot tell from the viewer's own covers the
        // request, which this pane reads as a teammate's, is worded here, because nothing will
        // start for it and nothing is asked of anyone.
        if (covering is { Own: false } && outcome == ReviewRequestOutcome.AlreadyCovered)
        {
            return Informational(
                request.Repository, request.Number,
                $"{opening}{age}; a task this install cannot yet tell from a teammate's already covers it "
                + "(h9k status --everyone lists it)");
        }

        if (outcome == ReviewRequestOutcome.HeldBeforeCutoff)
        {
            return NeedsYou(
                request.Repository, request.Number,
                $"{opening}{age}; it predates auto pr-review's start on this install, so nothing starts on "
                + "its own (no backfill — Decisions Log #161)",
                byHand);
        }

        if (outcome == ReviewRequestOutcome.HeldRequestTimeUnknown)
        {
            return NeedsYou(
                request.Repository, request.Number,
                $"{opening}; GitHub's own requested-at time could not be read, so nothing proves the request "
                + "postdates auto pr-review's start on this install and nothing starts on its own",
                byHand);
        }

        if (!setting.IsOn)
        {
            return NeedsYou(
                request.Repository, request.Number,
                $"{opening}{age}; auto pr-review is off here",
                $"{byHand} [dim]or turn it on with:[/] h9k project set {project} --auto-pr-review normal");
        }

        if (outcome == ReviewRequestOutcome.MintFailed)
        {
            return NeedsYou(
                request.Repository, request.Number,
                $"{opening}{age}; auto pr-review could not adopt it"
                + (request.OutcomeDetail.IsBlank() ? string.Empty : $" ({request.OutcomeDetail.EscapeMarkup()})"),
                byHand);
        }

        if (outcome == ReviewRequestOutcome.Unknown)
        {
            // Never guessed at (AGENTS.md): a row this build cannot read is said to be
            // unreadable, with the one lever that works regardless.
            return NeedsYou(
                request.Repository, request.Number,
                $"{opening}{age}; what became of it was recorded by a newer build and cannot be read here",
                byHand);
        }

        // Informational, never needs-you: the daemon is already handling it and nothing is asked of
        // the operator, who only learns which node is expected to mint and when this one would.
        if (outcome == ReviewRequestOutcome.HeldForPeer)
        {
            string leader = request.HoldLeaderNodeId is { } leaderId
                ? holdLeaderName.IsBlank()
                    ? $"node {DomainId.Short(leaderId)}"
                    : $"node {holdLeaderName.EscapeMarkup()} ({DomainId.Short(leaderId)})"
                : "a fleet peer";
            string endsAt = request.HoldEndsAt is { } holdEndsAt
                ? $"at {holdEndsAt.ToLocalTime():HH:mm}"
                : "when the hold ends";
            return Informational(
                request.Repository, request.Number,
                $"{opening}{age}; held for {leader}, which mints it first, and this node mints {endsAt} only if "
                + "nothing covers it by then");
        }

        // Informational, never needs-you, for the identical reason HeldForPeer is above: this
        // repository's own hourly cap is re-graded every sweep, so the operator is only told why
        // nothing started yet, never handed a lever that would change nothing (task 7ae690f5).
        if (outcome == ReviewRequestOutcome.HeldMintCapReached)
        {
            return Informational(
                request.Repository, request.Number,
                $"{opening}{age}; this repository's own hourly auto-pr-review mint cap is reached — re-graded "
                + "every sweep, so it mints once the window rolls");
        }

        return Informational(
            request.Repository, request.Number,
            $"{opening}{age}; auto pr-review is on here ({setting.Speed.Value.ToLowerInvariant()}) — a task "
            + "starts on the next sweep");
    }

    /// <summary>
    /// One unresolved mention's own row — held by the setting, held by the no-backfill cutoff, or
    /// refused while trying to mint — none of which a later sweep ever revisits:
    /// <see cref="ObservedReviewMention"/>'s own class doc makes a comment id's dedupe permanent, so
    /// turning the setting on or waiting past the cutoff never re-grades it, and a refused mint is
    /// a one-shot dedup key exactly like the other two. The only lever is the operator reading the
    /// comment and adopting the pull request by hand — the same gap the setting line above (idea
    /// 2f079bcd) already says in prose for the held cases, but said nowhere per-comment, and never
    /// at all for a refused mint, until this row existed (independent pre-PR review, cycle 1,
    /// adversarial lens, medium).
    /// <para>
    /// <paramref name="attachedToTeammate"/> says the task the mention was attached to
    /// (<see cref="ObservedReviewMention.TaskId"/>) is a teammate's. It changes only how that task
    /// is worded, never whether the operator is asked: the comment mentioned this viewer's login,
    /// so a follow-up that was never dispatched is theirs to chase whoever owns the task.
    /// </para>
    /// </summary>
    internal static ReviewRequestRow ComposeMentionRow(
        ObservedReviewMention mention, string projectName, CoveringReview? covering,
        bool attachedToTeammate = false)
    {
        string pullRequest = $"{mention.Repository.EscapeMarkup()}#{mention.Number}";
        string project = projectName.EscapeMarkup();
        string taggedBy = mention.CommentAuthorLogin.IsBlank()
            ? "a comment"
            : $"a comment from {mention.CommentAuthorLogin.EscapeMarkup()}";
        // The login actually mentioned, never "this install's" (independent pre-PR review, cycle
        // 1, conformance lens, low): the sibling request row already names request.ReviewerLogin
        // for the identical reason — two installs with two gh authentications can share one
        // database, and a mention of the OTHER install's login must not read as the reader's own.
        string mentionedLogin = mention.MentionedLogin.IsBlank()
            ? "a login this row does not record"
            : mention.MentionedLogin.EscapeMarkup();
        string opening = $"{taggedBy} mentioned {mentionedLogin} on {pullRequest}";
        string byHand = $"h9k task add --project {project} --from-pr {mention.Number}";
        ReviewMentionOutcome outcome = ReviewMentionOutcome.FromInput(mention.Outcome.Value);

        // Checked before the covering-task branch below, deliberately: the covering task genuinely
        // does cover the pull request, but no run of its own was ever dispatched to answer THIS
        // comment, so rendering it the same as an already-answered mention would bury exactly the
        // loss this outcome exists to keep visible (independent pre-PR review, cycle 1, both
        // lenses).
        if (outcome == ReviewMentionOutcome.AttachedNoFollowUp)
        {
            // Only the viewer's own task is named by id; a teammate's is said to be one, the same
            // rule every other row follows. A mention with no recorded task is judged by the
            // covering task instead.
            string taskRef = mention.TaskId switch
            {
                { } taskId when !attachedToTeammate => $"task {DomainId.Short(taskId)}",
                null when covering is not { Own: false } => "its covering task",
                _ => "a teammate's task",
            };
            string detail = mention.OutcomeDetail.IsBlank() ? string.Empty : $" ({mention.OutcomeDetail.EscapeMarkup()})";
            return NeedsYou(
                mention.Repository, mention.Number,
                $"{opening}; attached to {taskRef}, but no follow-up was dispatched to answer it{detail}",
                $"h9k pr review {pullRequest} --since-my-review");
        }

        // Checked ahead of the ordinary covering-task branch below (independent pre-PR review,
        // cycle 1, conformance lens): a still-parked task genuinely does exist for this pull
        // request, but the membership gate never assigned it, so telling the reader "already
        // covers it" would bury the one thing the row exists to say — h9k task assign is still
        // theirs to run. AttentionComposer's own needs-you row already names the park's own facts
        // in full; this row only has to point at the task and the lever.
        if (covering is { GateParked: true } parkedTask)
        {
            string parkedId = DomainId.Short(parkedTask.TaskId);
            return NeedsYou(
                mention.Repository, mention.Number,
                $"{opening}; task {parkedId} was minted but not assigned by the membership gate "
                + "(security review idea 6be68ee2, finding 1)",
                $"h9k task assign {parkedId}");
        }

        // Ahead of the informational teammate-covered branch below, which would otherwise say a
        // teammate's task "already covers it" and bury the one fact that matters: the daemon saw a
        // mention of this viewer's login, decided it was not its to act on, and recorded it
        // against no task at all. The row offers no by-hand lever, because the ones that exist
        // (h9k task add --from-pr, h9k pr review) are blind to whose task covers the pull request
        // and would attach to or refuse around the teammate's own; the reply is theirs to write on
        // GitHub. A task of the viewer's own that has covered it since is judged below instead.
        // It stays a needs-you row only while a teammate's task is still live (or none can be
        // found at all): once that review has closed, nothing is left running that the reply could
        // be waiting on, so the closed-teammate line below says so informationally instead of the
        // row staying red for good, since a recorded mention is never deleted.
        if (outcome == ReviewMentionOutcome.CoveredByTeammate && covering is null or { Own: false, Live: true })
        {
            string commentLink = mention.CommentUrl.IsBlank()
                ? string.Empty
                : $" ([link={mention.CommentUrl.EscapeMarkup()}]the comment[/])";
            return NeedsYouWithoutLever(
                mention.Repository, mention.Number,
                $"{opening}; this pull request is a teammate's review, so the reply happens on GitHub{commentLink}");
        }

        if (covering is { Own: false } teammates)
        {
            return Informational(
                mention.Repository, mention.Number,
                teammates.Live
                    ? $"{opening}; a teammate's task already covers it (h9k status --everyone lists it)"
                    : $"{opening}; a teammate's task already covered it and is closed");
        }

        if (covering is { } task)
        {
            string id = DomainId.Short(task.TaskId);
            return Informational(
                mention.Repository, mention.Number,
                task.Live
                    ? $"{opening}; task {id} already covers it ({task.StateWord})"
                    : $"{opening}; task {id} already covered it ({task.StateWord})");
        }

        if (outcome == ReviewMentionOutcome.MintFailed)
        {
            return NeedsYou(
                mention.Repository, mention.Number,
                $"{opening}; auto pr-review could not adopt it"
                + (mention.OutcomeDetail.IsBlank() ? string.Empty : $" ({mention.OutcomeDetail.EscapeMarkup()})"),
                byHand);
        }

        if (outcome == ReviewMentionOutcome.HeldBeforeCutoff)
        {
            return NeedsYou(
                mention.Repository, mention.Number,
                $"{opening} before auto pr-review's start on this install, so nothing started on its own "
                + "(no backfill — Decisions Log #161); a mention already seen is never retried",
                byHand);
        }

        if (outcome == ReviewMentionOutcome.HeldSettingOff)
        {
            return NeedsYou(
                mention.Repository, mention.Number,
                $"{opening} while auto pr-review was off here, so nothing started on its own; a mention "
                + "already seen is never retried",
                byHand);
        }

        // Never guessed at (AGENTS.md), and never dropped the way this outcome used to be
        // (independent pre-PR review, cycle 1, adversarial lens, low): a row this build cannot
        // read is said to be unreadable, with the one lever that works regardless — the identical
        // treatment Compose already gives ReviewRequestOutcome.Unknown on the request side.
        return NeedsYou(
            mention.Repository, mention.Number,
            $"{opening}; what became of it was recorded by a newer build and cannot be read here",
            byHand);
    }

    /// <summary>
    /// What a closed covering task actually holds back, which is not the same answer for every
    /// one of them (independent pre-PR review, cycle 1, conformance lens). The engine's re-mint
    /// guard is keyed on <see cref="TaskListItem.WasAutoPrReviewCreated"/>, so:
    /// <list type="bullet">
    /// <item>with the setting off here, nothing starts whatever the task was;</item>
    /// <item>a closed task auto pr-review minted holds this same standing request — and only
    /// this one, since a fresh request from GitHub is a re-review the guard lets through;</item>
    /// <item>a task a human adopted by hand holds nothing: the guard never matches it, so the
    /// next sweep is free to mint one of its own. Said as "may" rather than "will" because the
    /// other two guards this pane cannot resolve from a covered row — the no-backfill cutoff and
    /// the one-live-task dedup against a task adopted since — still get their say.</item>
    /// </list>
    /// </summary>
    private static string ClosedTaskHold(AutoPrReviewSetting setting, CoveringReview task) =>
        (setting.IsOn, task.AutoCreated) switch
        {
            (false, _) => "nothing new starts on its own: auto pr-review is off here",
            (true, true) => "nothing new starts on its own unless GitHub requests the review again",
            (true, false) => "auto pr-review does not count a task adopted by hand as one of its own, so it "
                + "may start one of its own on the next sweep",
        };

    private static ReviewRequestRow NeedsYou(string repository, int number, string cause, string lever) =>
        new(
            NeedsYou: true,
            $"[red bold]NEEDS YOU[/] [red]{cause}.[/] [dim]Take it with:[/] {lever}",
            repository,
            number);

    /// <summary>A needs-you row for something the viewer has to read and answer elsewhere, so no command is offered.</summary>
    private static ReviewRequestRow NeedsYouWithoutLever(string repository, int number, string cause) =>
        new(
            NeedsYou: true,
            $"[red bold]NEEDS YOU[/] [red]{cause}.[/]",
            repository,
            number);

    private static ReviewRequestRow Informational(string repository, int number, string cause) =>
        new(NeedsYou: false, $"[dim]{cause}.[/]", repository, number);
}
