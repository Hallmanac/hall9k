using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Run.Queries;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskShowCommand : Hall9kAsyncCommand<TaskShowCommand.Settings>
{
    /// <summary>The mentioning comment's own body is externally authored and unbounded; this is what the "Tagged by" row quotes of it.</summary>
    private const int MentionBodyMaxLength = 200;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(details.ProjectId, cancellationToken);

        // State, phase, attention — the three surfaces, before the context mountain
        // (Decisions Log #66). Composed by the same composer h9k status reads, so the answer to
        // "why am I looking at this" is the same answer on both screens.
        TaskStatusRow? row = await TaskStatusComposer.ComposeOneAsync(
            session, details, DateTimeOffset.UtcNow, cancellationToken);
        WriteStanding(row, details);

        Table header = new Table().Border(TableBorder.None).HideHeaders();
        header.AddColumns("k", "v");
        header.AddRow("[bold]Objective[/]", ExternalText.OneLineMarkup(details.Objective));
        header.AddRow("Type", details.Type.Value.EscapeMarkup());
        header.AddRow("Id", $"[dim]{details.Id}[/]");
        header.AddRow("Assigned to", await AssigneeMarkupAsync(session, details, cancellationToken));
        if (details.InteractiveModeEnabled)
        {
            // Task: interactive mode becomes a recorded property of the task. Named plainly here
            // because it is the reason a task's review/fix/re-review/pull-request boundaries keep
            // parking for a routine h9k review proceed rather than advancing on their own — the
            // one recorded, task-level fact behind every one of those rows.
            header.AddRow("Interactive mode", "[blue]on[/] [dim](every review/fix/PR boundary parks for h9k review proceed; h9k task handback or a default h9k task release turns it off)[/]");
        }

        if (details.Model != AgentModel.Unknown)
        {
            header.AddRow("Model", $"{details.Model.Value.EscapeMarkup()} [dim](task override)[/]");
        }

        if (details.SessionCap is { } sessionCap)
        {
            header.AddRow("Session cap", $"{sessionCap} [dim](task override — h9k task set-session-cap)[/]");
        }

        // A Draft is included now, and used not to be. The exclusion existed because
        // TaskDecider.Publish re-recorded the flag unconditionally (defaulting false), so stating
        // pre-approval on a draft claimed a promise one plain `h9k task publish` away from being
        // silently cleared (independent pre-PR review, cycle 1, conformance lens). Publish carries
        // a standing grant forward now (task: a published task's GitHub issue carries the whole
        // task record — an adopted task needs its own answer settable while still a Draft), so the
        // promise survives the republish and a draft that genuinely holds it must say so: the flag
        // removes the owner as a gate at the merge, and a surface that hides it is where somebody
        // finds out afterwards. A task at TRUE closeout (row.State == LifecycleState.Done — the
        // merge observed) is excluded the identical way: raw TaskState.Done alone does not say
        // so, since it is recorded the moment the pull request opens and never changes at the
        // later merge, so gating on it alone would claim a future merge for a pull request that
        // has already merged (independent pre-PR review, cycle 2, both lenses). An Abandoned task
        // is excluded too: TaskAggregate.Apply(TaskAbandoned) leaves the flag untouched as well,
        // but TaskDecider.SetPreApproved itself refuses to flip it on an abandoned task ("there is
        // no future pull request left for pre-approval to govern"), so a stale true surviving
        // abandonment must not go on claiming a merge the platform will never attempt (independent
        // pre-PR review, cycle 1, adversarial lens). A row that could not be composed carries no
        // closeout answer to gate on, so it falls back to the raw state's own Abandoned check
        // rather than guessing.
        bool trueCloseout = row is not null && row.State == LifecycleState.Done;
        if (details.EffectivePreApproval.MergesAutomatically
            && details.State != TaskState.Abandoned
            && !trueCloseout)
        {
            header.AddRow("Pre-approved",
                $"[green]{PreApprovalInput.Word(details.EffectivePreApproval).EscapeMarkup()}[/] [dim]— "
                + $"{PreApprovalInput.Describe(details.EffectivePreApproval)} (h9k task set-pre-approved to "
                + "change)[/]");

            // Its own row rather than a second line inside the one above, matching every other
            // fact on this table: one row, one fact.
            if (await HumanReviewRequestedMarkupAsync(session, details, cancellationToken) is { } humanReview)
            {
                header.AddRow("Human review", humanReview);
            }
        }

        // One row per cap actually overridden (task: the review cycle caps become settable at
        // three levels) — an operator glancing at a task can see a one-off limit without having
        // to resolve the whole task > project > node > default chain by hand.
        if (details.MaxComplianceReviewCycles is { } maxComplianceReviewCycles)
        {
            header.AddRow("Max compliance review cycles", $"{maxComplianceReviewCycles} [dim](task override)[/]");
        }

        if (details.MaxAdversarialReviewCycles is { } maxAdversarialReviewCycles)
        {
            header.AddRow("Max adversarial review cycles", $"{maxAdversarialReviewCycles} [dim](task override)[/]");
        }

        if (details.MaxFinalFullPassRounds is { } maxFinalFullPassRounds)
        {
            header.AddRow("Max final-full-pass rounds", $"{maxFinalFullPassRounds} [dim](task override)[/]");
        }

        if (details.LifetimeReviewCycleBudget is { } lifetimeReviewCycleBudget)
        {
            header.AddRow("Lifetime review-cycle budget", $"{lifetimeReviewCycleBudget} [dim](task override)[/]");
        }

        if (details.ReviewStageComposition is { } taskReviewStageComposition)
        {
            header.AddRow(
                "Review stage composition", $"{taskReviewStageComposition.Value.EscapeMarkup()} [dim](task override)[/]");
        }

        if (details.SourceIdeaId is { } sourceIdeaId)
        {
            // The other half of promotion's two-way provenance (Decisions Log #35): the idea's
            // stream names this task, and this names the idea it came from.
            header.AddRow("From idea",
                $"[dim]{TaskListCommand.ShortId(sourceIdeaId)}[/] "
                + $"[dim](h9k idea show {TaskListCommand.ShortId(sourceIdeaId)})[/]");
        }

        if (details.EpicId is { } epicId)
        {
            // Membership, independent of provenance above (Decisions Log #100): a task
            // promoted from an idea and one hand-added with no lineage can sit in the same epic.
            EpicDetails? epic = await session.LoadAsync<EpicDetails>(epicId, cancellationToken);
            string shortEpicId = TaskListCommand.ShortId(epicId);
            header.AddRow("Epic", epic is null
                ? $"[dim]{shortEpicId}[/]"
                : $"{epic.Title.EscapeMarkup()} [dim]({shortEpicId}, h9k epic show {shortEpicId})[/]");
        }

        if (details.ExternalReference.IsNotBlank())
        {
            // The importer is built from the registered connections rather than the default one:
            // placing a Jira reference needs the site that connection carries, which is the one
            // asymmetry between the two sources (PLAN.md §10).
            WorkItemImporter importer = await WorkItemConnections.ImporterAsync(session, cancellationToken);
            header.AddRow("External", ExternalMarkup(importer, details.ExternalReference));
            if (details.ExternalStatusObserved.IsNotBlank() && details.ExternalObservedAt is { } observedAt)
            {
                header.AddRow(string.Empty,
                    $"[dim]{ExternalText.OneLineMarkup(details.ExternalStatusObserved)} when read at "
                    + $"{observedAt.ToLocalTime():g}[/]");
            }

            // Effective value AND provenance, always — unlike the task-override rows above (Model,
            // review caps, review stage composition) that render only when this task set one, the
            // acceptance criteria for close-linked-issue (task: a task's linked GitHub issue is
            // closed at true closeout under a configurable rule) call for both states to be
            // legible without resolving the task > project chain by hand. GitHub only — Jira's
            // merge comment behaviour is unchanged, so the row would name a rule that can never
            // apply to a Jira card.
            if (details.ExternalReference.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                header.AddRow("Close linked issue", CloseLinkedIssueMarkup(details, project));
            }
        }
        else if (details.CloseLinkedIssue is not null && project?.BacklogPolicy == BacklogPolicy.GitHubIssues)
        {
            // An override recorded at publish is real before the linked issue itself exists — the
            // daemon has not yet created it, so there is no ExternalReference to gate the row on
            // above. Shown only under a github-issues backlog policy, since that is the only
            // provider this rule ever applies to.
            header.AddRow("Close linked issue", CloseLinkedIssueMarkup(details, project));
        }

        // The mention the task's currently parked run actually answers, when one exists — never
        // details.LatestMention*, which always names the most recently OBSERVED mention, whatever
        // run it landed against. A second mention attaching while an earlier one's own follow-up
        // lap (or mint) is still running moves every LatestMention* field to itself
        // (TaskDetailsProjection.Apply(PullRequestReviewMentionObserved)), so a walker using this
        // row to correlate a reply's in_reply_to would post into the wrong thread — the exact hole
        // PrReviewEngine's own park reason already closes by reading ObservedReviewMention keyed on
        // run.PrReviewMentionCommentId (or, for a mint, the TaskCreated row) instead of task.Latest*
        // (independent pre-PR review, cycle 3, both lenses). Falls back to LatestMention* only when
        // no currently-parked run answers a mention at all, so a mention merely observed for the
        // record still shows here.
        ObservedReviewMention? answeredMention = await AnsweredMentionAsync(session, details, cancellationToken);
        string? mentionCommentId = answeredMention?.CommentId ?? details.LatestMentionCommentId;
        if (mentionCommentId is not null)
        {
            string? mentionAuthorLogin = answeredMention?.CommentAuthorLogin ?? details.LatestMentionAuthorLogin;
            string? mentionBody = answeredMention?.CommentBody ?? details.LatestMentionBody;
            DateTimeOffset? mentionCreatedAt = answeredMention is not null
                ? answeredMention.CommentCreatedAt
                : details.LatestMentionCreatedAt;
            long? mentionCommentDatabaseId = answeredMention is not null
                ? answeredMention.CommentDatabaseId
                : details.LatestMentionCommentDatabaseId;

            // idea 2f079bcd, auto-pr-review's second trigger: the triggering comment's own id,
            // author and time, exactly what a review request's own PullRequestReviewAssignmentObserved
            // lets the External row above name for a request — this is the mention's equivalent,
            // always shown once any mention has ever been observed on this task, even after a later
            // one has replaced it as the "latest".
            string when = mentionCreatedAt is { } createdAt
                ? createdAt.ToLocalTime().ToString("g")
                : "an unrecorded time";
            // The reply id is shown only when the tagged comment was an inline review-comment-thread
            // reply — the one shape the REST reply endpoint's own in_reply_to accepts. Its absence is
            // itself the signal walk-pr-review-findings reads to know a plain issue comment (or the
            // pull request's own description) needs an ordinary comment instead (independent pre-PR
            // review, cycle 1, conformance lens: sending the comment id shown here to that endpoint
            // 404s, since it is the GraphQL node id, not the numeric REST id).
            string replyIdSuffix = mentionCommentDatabaseId is { } databaseId
                ? $", reply id {databaseId}"
                : string.Empty;
            header.AddRow(
                "Tagged by",
                $"{(mentionAuthorLogin ?? "unknown").EscapeMarkup()} at {when} "
                + $"[dim](comment {mentionCommentId.EscapeMarkup()}{replyIdSuffix.EscapeMarkup()})[/]");
            if (mentionBody.IsNotBlank())
            {
                // Bounded, unlike ExternalText.OneLineMarkup alone: a mentioning comment is
                // externally authored and free to carry no newline at all, and this row's own
                // sibling in PrReviewEngine's own park-reason quoting was found unbounded for the
                // identical reason (independent pre-PR review, cycle 1, adversarial lens) — the
                // class sweep for that finding, since this row quotes the same untrusted comment
                // body and was introduced by this same branch.
                string bounded = RelayedText.Truncate(mentionBody, MentionBodyMaxLength);
                header.AddRow(string.Empty, $"[dim]{ExternalText.OneLineMarkup(bounded)}[/]");
            }
        }

        if (details.Origin is { } origin)
        {
            // A mirror told apart from local work without anybody keeping a ledger (task: a
            // published task's GitHub issue carries the whole task record). Every value here is the
            // other install's, copied from the record once at adoption and never re-read, which the
            // row says outright — the ids mean nothing in this store and looking them up here would
            // find the wrong thing or nothing at all.
            header.AddRow("Origin", OriginMarkup(origin));
        }

        if (details.UntrackedAttested)
        {
            // Honest by construction: this row only ever appears when TaskPublished actually
            // carried the attestation, so a task that predates the policy or was published
            // under policy none — both of which leave the field false — never shows it.
            OwnerDetails? attester = details.UntrackedAttestedByOwnerId is { } attesterId
                ? await session.LoadAsync<OwnerDetails>(attesterId, cancellationToken)
                : null;
            string who = attester?.Name.EscapeMarkup() ?? "[dim]unknown[/]";
            string when = details.UntrackedAttestedAt is { } attestedAt
                ? attestedAt.ToLocalTime().ToString("g")
                : "an unrecorded time";
            header.AddRow("Backlog",
                $"[yellow]untracked by choice[/] [dim]— attested by {who} at {when}; no external item "
                + "was created for it at publish[/]");
        }

        if (details.PendingPublicationProvider is { } publishingTo)
        {
            header.AddRow("Publishing to", PublicationMarkup(details, publishingTo));
        }
        else if (details.PublicationOutcome.IsNotBlank() && details.ExternalReference.IsBlank())
        {
            // A publication that ended without a link is the case worth surfacing: the session
            // said something, and nothing came back through the gate, so the human is the one who
            // has to decide whether a card exists.
            header.AddRow("Publication", $"[yellow]{ExternalText.OneLineMarkup(details.PublicationOutcome)}[/]");
        }

        if (details.PendingJiraWriteId is not null)
        {
            header.AddRow("Jira write", JiraWriteMarkup(details));
        }

        if (details.PullRequestUrl.IsNotBlank())
        {
            header.AddRow("PR", $"[link]{details.PullRequestUrl.EscapeMarkup()}[/]");
        }

        if (details.DependencyFailureReason.IsNotBlank())
        {
            header.AddRow("Dependency", $"[red]{details.DependencyFailureReason.EscapeMarkup()}[/]");
        }

        // A reviewer's own lap (Decisions Log #149): open, or ended with the verdict it submitted.
        // Both rows are worth the space for the same reason: the whole deliverable of a pr-review
        // task is a review that lives on somebody else's pull request, so without this the task's
        // own screen could only say it closed, never what it said.
        if (details.ReviewLapOpen)
        {
            header.AddRow(
                "Review lap",
                "[blue]open[/] [dim]— " + (details.ReviewLapWorktreePath.IsNotBlank()
                    ? $"reading in {details.ReviewLapWorktreePath.EscapeMarkup()}; "
                    : "no checkout (--no-worktree); ")
                + $"ends at h9k pr approve {details.Id} or h9k pr request-changes {details.Id}[/]");
        }
        else if (details.ReviewerVerdict != ReviewerVerdict.Unknown)
        {
            header.AddRow("Review verdict", ReviewerVerdictMarkup(details));
        }

        if (details.RemoteStackedParentHoldReason.IsNotBlank())
        {
            header.AddRow("Stacked parent", $"[red]{details.RemoteStackedParentHoldReason.EscapeMarkup()}[/]");
        }

        // A failure the task has already moved on from — retried, resolved, or abandoned. While
        // it is still Failed the attention block above leads with the composed cause, so this
        // row exists for the history rather than for the ask. That suppression is only honest
        // because the cause is composed from this document's reason when the lifecycle row has
        // none (TaskStatusComposer.RecordedFailureReason); without it, hiding this row would
        // leave the screen claiming a failure nobody explained while holding the explanation.
        if (details.FailureReason.IsNotBlank() && details.State != TaskState.Failed)
        {
            header.AddRow("Earlier failure", $"[red]{details.FailureReason.EscapeMarkup()}[/]");
        }

        if (details.RetryReason.IsNotBlank())
        {
            string label = details.RetryReasonIsHandback ? "Handed back" : "Retried";
            header.AddRow(label, $"[yellow]{details.RetryReason.EscapeMarkup()}[/]");
        }

        if (details.ResolvedReason.IsNotBlank())
        {
            header.AddRow("Resolved", $"[green]{details.ResolvedReason.EscapeMarkup()}[/]");
        }

        if (details.AbandonedReason.IsNotBlank())
        {
            header.AddRow("Abandoned", $"[dim]{details.AbandonedReason.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(header);

        AnsiConsole.MarkupLine("\n[bold]Acceptance criteria[/]");
        if (details.AcceptanceCriteria.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "  [dim]none yet — publishing requires at least one checkable criterion (PLAN.md §4)[/]");
        }

        foreach (string criterion in details.AcceptanceCriteria)
        {
            AnsiConsole.MarkupLine($"  • {criterion.EscapeMarkup()}");
        }

        bool hasOpenDependency = false;
        if (details.BlockedBy.Count > 0)
        {
            IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
                session, details.BlockedBy, cancellationToken);
            hasOpenDependency = dependencies.Any(
                dependency => StackedEdgeRules.Blocks(details.StackedOnTaskId, dependency));
            // "Only at true closeout" is the whole rule for a plain edge (Decisions Log #34) and
            // stays the whole sentence on a task that declared no stack. A stacked task carries
            // exactly one edge that is met earlier, so keeping "only" there would contradict, in
            // the same breath, the mark this same screen prints beside that parent two lines down
            // ("waiting (stacked: met at Delivered)"). The exception is named as an exception
            // rather than appended to a rule that claims there are none.
            AnsiConsole.MarkupLine(details.StackedOnTaskId is null
                ? "\n[bold]Blocked by[/] [dim](met only at true closeout: the pull request merged)[/]"
                : "\n[bold]Blocked by[/] [dim](met at true closeout — the pull request merged — "
                  + "except the stacked edge below, which is met at Delivered)[/]");
            // Each blocker is named in the lifecycle vocabulary, not the persisted one
            // (Decisions Log #66). This is the one screen that explains the true-closeout rule,
            // so printing the raw state here would show a pushed-but-unmerged blocker as Done
            // beside the mark that says it is still holding this task back — the premature Done
            // the redesign exists to remove, on the screen least able to afford it.
            foreach (TaskDependency dependency in dependencies)
            {
                bool isStackParent = details.StackedOnTaskId == dependency.Id;
                AnsiConsole.MarkupLine(
                    $"  {TaskStatusComposer.DependencyMark(dependency, details.StackedOnTaskId)} "
                    + $"[dim]{TaskListCommand.ShortId(dependency.Id)}[/] "
                    + $"{ExternalText.OneLineMarkup(dependency.Objective)} "
                    + $"({TaskStatusComposer.State(dependency).Markup})"
                    + (isStackParent ? " [blue]— stacked on this[/]" : string.Empty));
            }

            // Recorded on the current claim's own TaskClaimed (task 8a56af78-h9k, extended by
            // task 0ac72cb8-h9k to h9k task work's own claim too): a human warned about the
            // still-open blocker(s) above chose to claim this task anyway, either with
            // --acknowledge-unmet-dependencies or, per design ruling R7, relying on an
            // acknowledgment an earlier claim on this task already recorded (DependencyOverrideCarriedForward)
            // — the stream's own answer to "which acknowledgment did this claim rely on". Cleared
            // only when the claim is given back to run again (requeue, handback, retry, …) — left
            // set when the claim instead ends the task's story (complete, resolve, fail, abandon),
            // so this can still read true on a Done, Failed, or Abandoned task
            // (TaskDetails.DependencyOverrideAcknowledged's own doc comment).
            if (details.DependencyOverrideAcknowledged)
            {
                AnsiConsole.MarkupLine(details.DependencyOverrideCarriedForward
                    ? "  [yellow]Claimed deliberately despite the open blocker(s) above, relying on an "
                      + "acknowledgment an earlier claim on this task already recorded — nothing was asked "
                      + "again[/]"
                    : "  [yellow]Claimed deliberately despite the open blocker(s) above (h9k task work or "
                      + "h9k task start --acknowledge-unmet-dependencies)[/]");
            }
        }

        await WriteRemoteStackedParentAsync(session, details, cancellationToken);

        if (details.AgentContext.IsNotBlank())
        {
            // Agent context is the one field on a task that can arrive from outside the machine:
            // since adoption (PLAN.md §3.1a) it may be an issue body written by anyone who can
            // file an issue. The agent reads that text out of a prompt, where an escape sequence
            // is inert; a human reads it out of a terminal, where it is not.
            AnsiConsole.MarkupLine("\n[bold]Agent context[/]");
            AnsiConsole.WriteLine(ExternalText.ForTerminal(details.AgentContext));
        }

        await WriteStartingContextAsync(session, details, cancellationToken);

        if (details.Conversation.Count > 0)
        {
            // A question is written by the agent, which has just read an issue body it was told
            // to treat as quoted source (WorkItemContext.Compose) and to quote back when it
            // summarises. Relaying is exactly how adopted text arrives here, so the same rule
            // covers it: escaping Spectre's syntax leaves the terminal's own untouched.
            AnsiConsole.MarkupLine("\n[bold]Conversation[/]");
            foreach (TaskQuestion question in details.Conversation)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]Q[/] {ExternalText.OneLineMarkup(question.Question)} [dim]({question.AskedAt:g})[/]");
                if (question.Answer.IsNotBlank())
                {
                    AnsiConsole.MarkupLine(
                        $"  [green]A[/] {ExternalText.OneLineMarkup(question.Answer)} [dim]({question.AnsweredAt:g})[/]");
                }
            }
        }

        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(r => r.TaskId == taskId)
            .OrderBy(r => r.DispatchedAt)
            .ToListAsync(cancellationToken);
        if (runs.Count > 0)
        {
            // Loaded once for every run, not just the newest (task: h9k task show surfaces each
            // run's coordinates): the worktree path and branch never change after dispatch, so
            // this is the one read that answers both the table's new columns and the sessions
            // block below, rather than a per-row LoadAsync each.
            Dictionary<Guid, RunDetails> runDetailsById = (await session.LoadManyAsync<RunDetails>(
                cancellationToken, [.. runs.Select(r => r.Id)])).ToDictionary(r => r.Id);

            AnsiConsole.MarkupLine("\n[bold]Runs[/]");
            Table runsTable = new Table().Border(TableBorder.Rounded);
            runsTable.AddColumns("Run", "Gen", "State", "Model", "Stages", "Dispatched", "PR", "Gates");
            foreach (RunListItem run in runs)
            {
                // A run dispatched before the model chain existed recorded none; "-" says
                // unknown rather than naming a model the run may never have used.
                runsTable.AddRow(
                    $"[dim]{TaskListCommand.ShortId(run.Id)}[/]",
                    run.LeaseGeneration.ToString(),
                    run.State.Value.EscapeMarkup(),
                    (run.Model == AgentModel.Unknown ? "-" : run.Model.Value).EscapeMarkup(),
                    ReviewStageCompositionMarkup(run.ReviewStageComposition),
                    run.DispatchedAt.ToLocalTime().ToString("g").EscapeMarkup(),
                    (run.PullRequestUrl ?? "-").EscapeMarkup(),
                    FormatGateDurations(run.GateDurations));
            }

            AnsiConsole.Write(runsTable);
            WriteCoordinates(runs, runDetailsById);
            RunDetails? newestRun = runDetailsById.GetValueOrDefault(runs[^1].Id);
            WriteReviewScopeSeed(newestRun);
            WriteReviewOutcome(newestRun);
            WriteReviewEndedByMerge(newestRun);
            WriteUnfixedFindings(newestRun);
            WriteRideAlongFindings(newestRun);
            WriteFixEscalation(newestRun);
            WriteBackgroundGateWait(newestRun);
            WriteSessionErrorRetries(newestRun);
            WriteUncommittedWorkRecovery(newestRun);

            // Not necessarily newestRun: a fallback supersedes the run it recorded the outcome
            // on within the same sweep that dispatches the follow-up, so the newest run by
            // dispatch order is routinely a different one that never carries this field at all
            // (independent pre-PR review, cycle 1, adversarial lens).
            RunDetails? mechanicalRebaseRun = runDetailsById.Values
                .Where(r => r.LastMechanicalRebaseAt is not null)
                .OrderByDescending(r => r.LastMechanicalRebaseAt)
                .FirstOrDefault();
            // Across every run, not just the newest: closeout observes the review on the run that
            // was watching the pull request and the fix lap parks its disagreement on the run
            // dispatched to answer it, so a reader who only saw the newest run would see one half
            // of the story (task: a changes-requested pull-request review from a human becomes a
            // fix lap). Ordered by dispatch, which is the order the laps happened in.
            WriteChangesRequestedReviews([.. runs.Select(r => runDetailsById.GetValueOrDefault(r.Id)).OfType<RunDetails>()]);
            WriteMechanicalRebaseOutcome(mechanicalRebaseRun);
            RunDetails? preFinalPassRebaseRun = runDetailsById.Values
                .Where(r => r.LastPreFinalPassRebaseAt is not null)
                .OrderByDescending(r => r.LastPreFinalPassRebaseAt)
                .FirstOrDefault();
            WritePreFinalPassRebaseOutcome(preFinalPassRebaseRun);
            RunDetails? autoMergeRun = runDetailsById.Values
                .Where(r => r.LastAutoMergeAttemptedAt is not null)
                .OrderByDescending(r => r.LastAutoMergeAttemptedAt)
                .FirstOrDefault();
            WriteAutoMergeOutcome(autoMergeRun);
            await WriteSessionsAsync(session, runs, runDetailsById, cancellationToken);
            await WriteGateDurationAnomaliesAsync(session, details.ProjectId, runs[^1], cancellationToken);
        }

        await WriteHandoffAsync(session, details, runs, cancellationToken);

        AnnounceNextStep(details, hasOpenDependency);

        if (details.State == TaskState.Failed)
        {
            string shortId = TaskListCommand.ShortId(details.Id);
            AnsiConsole.MarkupLine("\n[bold]Failed is a waypoint, not an ending — three exits:[/]");
            AnsiConsole.MarkupLine($"  [yellow]retry[/]    h9k task retry {shortId} --reason <why>              — run it again");
            AnsiConsole.MarkupLine($"  [green]resolve[/]  h9k task resolve {shortId} --reason <why> [[--pr <url>]] — the objective was met despite the failure");
            AnsiConsole.MarkupLine($"  [dim]abandon[/]  h9k task abandon {shortId} [[--reason <why>]]          — walk away");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The three surfaces, first, in the order a reader asks them: where the work is, what the
    /// machinery is doing right now, and whether any of it is waiting on them (Decisions Log
    /// #66). Everything below this is reference material; this is the answer.
    /// <para>
    /// Backlog 36's per-task token accounting composes in here, beside the phase line, when it
    /// lands: the block is a list of labelled rows for exactly that reason.
    /// </para>
    /// </summary>
    private static void WriteStanding(TaskStatusRow? row, TaskDetails details)
    {
        Table standing = new Table().Border(TableBorder.None).HideHeaders();
        standing.AddColumns("k", "v");

        // A task whose lifecycle row could not be composed still has a persisted state worth
        // printing; it says what it read rather than nothing at all.
        standing.AddRow("[bold]State[/]", row is null
            ? $"[dim]{details.State.Value.EscapeMarkup()}[/] [dim](no lifecycle row composed)[/]"
            : $"{row.StateMarkup} {StateGloss(row)}");

        if (row?.Phase.HasPhase == true)
        {
            standing.AddRow("[bold]Phase[/]", row.Phase.Markup);
        }

        if (row is { Facts.Count: > 0 })
        {
            // PublishedFacts.Compose states the queue-first marker and the pre-approval marker
            // wherever either is set, Published row or not (Decisions Log #127; task: a task can
            // be published pre-approved), so a non-Published row's Facts is whichever of those
            // markers apply — never only "Queue priority", which named just the first of the two
            // and misdescribed the row once pre-approval could appear here too (independent pre-PR
            // review, cycle 1, both lenses). "Waiting on" would misdescribe a task that is running,
            // done, or otherwise not actually waiting on anything.
            string label = row.State == LifecycleState.Published ? "Waiting on" : "Facts";
            standing.AddRow($"[bold]{label}[/]",
                string.Join(" [dim]·[/] ", row.Facts.Select(fact => $"[dim]{fact.EscapeMarkup()}[/]")));
        }

        if (row is { Attention.HasCause: true })
        {
            standing.AddRow(row.Attention.NeedsYou ? "[red bold]Needs you[/]" : "[dim]Waiting[/]",
                row.Attention.Markup);
        }

        AnsiConsole.Write(standing);
    }

    /// <summary>
    /// What the lifecycle word means, spelled out once where there is room for it. The board has
    /// only the word; this screen can afford the sentence, and Delivered in particular is a new
    /// word that has to teach itself.
    /// </summary>
    internal static string StateGloss(TaskStatusRow row) => row.State.Word switch
    {
        "Draft" => "[dim](being developed; the dispatcher cannot see it)[/]",
        "Published" => "[dim](past the readiness gate; not working yet)[/]",
        "Working" => "[dim](a run owns it and has not pushed yet)[/]",
        "Delivered" => "[dim](pushed; the merge has not been observed)[/]",
        "Done" => $"[dim](true closeout: {DoneReason(row.Type, row.PullRequestUrl)})[/]",
        "Failed" => "[dim](a waypoint, not an ending — log #27)[/]",
        "Archived" => "[dim](walked away from)[/]",
        _ => "[dim](this build does not recognize the recorded state)[/]",
    };

    /// <summary>
    /// What true closeout actually observed for a Done task — the wording follows the closeout
    /// cause, not the task type alone. A pr-review task never opens a pull request of its own
    /// (Decisions Log #99): its Done means the owner delivered the review verdict (<c>h9k review
    /// resolve --merge-ready</c>), and no merge was ever watched for it, even though
    /// <c>PrReviewEngine.FinalizeAsync</c> records the reviewed pull request's URL on the same
    /// task (Windows field report item 12, 2026-09-03: a pr-review task rendered "the merge was
    /// observed" while the pull request it reviewed sat open). Every other task type reaches
    /// Done by one of two doors <see cref="TaskStatusComposer.Closed"/> already discriminates:
    /// pushed and merged (or its run completed), or closed by hand with no pull request ever
    /// opened — and only the first of those is a merge anyone observed. A blank
    /// <paramref name="pullRequestUrl"/> is that second door: the platform's
    /// never-guess-at-unobserved-facts rule applies to this ledger's prose exactly as it does to
    /// its data.
    /// </summary>
    internal static string DoneReason(TaskType taskType, string pullRequestUrl) => taskType switch
    {
        _ when taskType == TaskType.PrReview => "the review was delivered",
        _ when pullRequestUrl.IsBlank() => "the task was closed with no pull request to watch",
        _ => "the merge was observed",
    };

    /// <summary>
    /// This run's opening Discovery cycle scope seed (task: a lap reviews only what it changed) —
    /// the pull request head the previous run pushed, observed by closeout when it reopened this
    /// lap's task for a ReviewFeedback or FailingChecks follow-up. Null, and nothing renders, for a
    /// fresh run, a Rebase follow-up (excluded, unchanged), a manual h9k pr resolve reopen, or a run
    /// dispatched before this field existed — in every one of those cases the opening cycle read
    /// the full branch and there is nothing to say here.
    /// <para>
    /// A seed being recorded is not the same as it having applied (independent pre-PR review,
    /// cycle 1 conformance and adversarial findings): <c>ReviewEngine.ResolveOpeningDiscoverySinceShaAsync</c>
    /// re-verifies the seed against the worktree at dispatch time and silently degrades to a full
    /// read when a history rewrite — the mandated fixup-and-autosquash rebase among them — leaves it
    /// no longer an ancestor of HEAD. <see cref="RunDetails.OpeningReviewSinceShaApplied"/> is what
    /// the opening cycle actually used, so this renders that observation rather than the dispatch-time
    /// intent — never a fact about the review that was never observed.
    /// </para>
    /// </summary>
    private static void WriteReviewScopeSeed(RunDetails? run)
    {
        if (run is not { OpeningReviewSinceSha: { } sinceSha })
        {
            return;
        }

        if (run.OpeningReviewSinceShaApplied is { } appliedSha)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold]Review scope[/]  opening cycle scoped to changes since "
                + $"[dim]{appliedSha.EscapeMarkup()}[/] [dim]— the pull request head the previous run "
                + "pushed, observed when this lap's task reopened[/]");
            return;
        }

        if (run.ReviewCycle < 1)
        {
            // The opening cycle has not dispatched yet — nothing observed to report either way,
            // so say nothing rather than assert a scoping (or a degrade) nobody has seen happen.
            return;
        }

        AnsiConsole.MarkupLine(
            $"\n[bold]Review scope[/]  seeded to changes since [dim]{sinceSha.EscapeMarkup()}[/], but the "
            + "[dim]seed no longer resolved against the worktree when the opening cycle dispatched — it read "
            + "the full branch instead[/]");
    }

    /// <summary>
    /// How the newest run's pre-PR review ended (Decisions Log #63). Merge-ready is one word for
    /// two different things, and this line is what keeps them apart: clean means a reviewer read
    /// the final tip and found nothing, while settled means the severity gate ended the loop
    /// over findings that were fixed but never read again, or routed away — to a draft bug task
    /// of their own, or folded into the project's standing sweep draft (Decisions Log #117).
    /// A reader deciding how much to trust a pull request should not have to dig through the run
    /// stream to learn which of those happened.
    /// <para>
    /// Silent whenever <see cref="WriteReviewEndedByMerge"/> has something to say instead: a cycle
    /// can conclude merge-ready (setting <see cref="RunDetails.LastReviewVerdict"/>) and then have
    /// its mandatory final pass short-circuited by a merge observed at the Settling boundary
    /// (task: a post-PR follow-up's review loop checks the pull request's merge state between
    /// passes) — a verdict this line would otherwise report alongside a line saying the loop never
    /// reached one on its own. <see cref="RunDetails.ReviewEndedByMergeAtCycle"/> having a value at
    /// all is what that short-circuit leaves behind, regardless of what the last cycle concluded.
    /// </para>
    /// </summary>
    private static void WriteReviewOutcome(RunDetails? run)
    {
        if (run is null || run.ReviewEndedByMergeAtCycle is not null || run.LastReviewVerdict != ReviewVerdict.MergeReady)
        {
            return;
        }

        // A run whose review was already in flight before settlements existed recorded none, so
        // the line says merge-ready and stops rather than claiming a cleanliness nobody observed.
        string outcome = run.ReviewSettlement switch
        {
            var settlement when settlement == ReviewSettlement.Clean =>
                "[green]merge-ready (clean)[/] [dim]— a reviewer read the final diff and found nothing[/]",
            var settlement when settlement == ReviewSettlement.Settled =>
                $"[yellow]merge-ready (settled[/] [yellow]— {run.ReviewResidualsFixed} residual(s) fixed, "
                + $"{run.ReviewResidualsRouted} routed{RideAlongClause(run)}{UnfixedClause(run)}{UnroutedClause(run)})[/] "
                + "[dim]— the loop ended without a clean re-read[/]",
            _ => "[green]merge-ready[/] [dim]— how it was reached was not recorded[/]",
        };

        AnsiConsole.MarkupLine($"\n[bold]Pre-PR review[/]  {outcome}");
    }

    /// <summary>
    /// Whether the newest run's review loop ended not by settling but because its own pull
    /// request merged mid-review (task: a post-PR follow-up's review loop checks the pull
    /// request's merge state between passes) — a human merging under a live follow-up. Shown
    /// instead of <see cref="WriteReviewOutcome"/>'s line, never alongside it: this method's own
    /// guard is the one that decides which of the two shows, since a cycle that already concluded
    /// <see cref="ReviewVerdict.MergeReady"/> can still be short-circuited before its mandatory
    /// final pass — <see cref="RunDetails.ReviewEndedByMergeAtCycle"/> having a value always wins
    /// over whatever the last cycle's own verdict says.
    /// </summary>
    private static void WriteReviewEndedByMerge(RunDetails? run)
    {
        if (run is not { ReviewEndedByMergeAtCycle: { } cycle })
        {
            return;
        }

        string reLand = run.ReLandDraftTaskId is { } draftId
            ? $" [dim]— stranded commits saved and routed to draft task {draftId}[/]"
            : string.Empty;
        AnsiConsole.MarkupLine(
            $"\n[bold]Pre-PR review[/]  [yellow]ended — the pull request merged mid-review[/] "
            + $"[dim](cycle {cycle}, no further passes dispatched){reLand}[/]");
    }

    /// <summary>
    /// Whether the newest run's most recently dispatched fix session ran on the review role's
    /// model instead of the fix role's (task: a second fix round over the same findings) — shown
    /// regardless of where the review loop currently stands, since the escalation matters most
    /// while a human is deciding whether to trust a still-running loop, not only once it settles.
    /// De-escalation is automatic once the repeated findings clear, so this line simply stops
    /// appearing the next time the newest run's fix dispatch resolves the ordinary fix role.
    /// </summary>
    private static void WriteFixEscalation(RunDetails? run)
    {
        if (run is not { LastFixSessionEscalated: true })
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"\n[bold]Fix escalation[/]  [yellow]cycle {run.LastFixSessionEscalationCycle} dispatched on the review role's model[/] "
            + $"[dim]— {ExternalText.OneLineMarkup(run.LastFixSessionEscalationReason ?? "reason not recorded")}[/]");
    }

    /// <summary>
    /// The stage composition this run's own stream recorded at dispatch (task: the review
    /// pipeline's stage composition becomes configuration recorded per run) — dim for
    /// FullPipeline, the shape every run had before this setting existed, so an operator's eye is
    /// drawn only to a run that ran under something else.
    /// </summary>
    private static string ReviewStageCompositionMarkup(ReviewStageComposition composition) =>
        composition == ReviewStageComposition.FullPipeline
            ? "[dim]full[/]"
            : $"[yellow]{composition.Value.EscapeMarkup()}[/]";

    /// <summary>
    /// Names the most recent fix session ending on a pending background task explicitly (task: a
    /// headless build, fix, or recovery session never ends its turn while a gate it started is
    /// still running in the background) — the named outcome this task adds so a reader sees why
    /// the fix session left no resolution, rather than only the generic "(undeclared)" every other
    /// unmarked ending shares. The dirty tree it usually leaves behind, if any, is its own line
    /// just below (<see cref="WriteUncommittedWorkRecovery"/>).
    /// </summary>
    private static void WriteBackgroundGateWait(RunDetails? run)
    {
        if (run is not { LastFixEndedWaitingOnBackgroundGate: true })
        {
            return;
        }

        AnsiConsole.MarkupLine(
            "\n[bold]Fix session outcome[/]  [yellow]ended waiting on a background gate[/] "
            + "[dim]— its own final message named a background task the platform will never receive a result from[/]");
    }

    /// <summary>
    /// Every session-error retry the newest run recorded (task: a session that reports an error
    /// result is retried once in place) — a leg that reported a generic error and was
    /// redispatched fresh after a short backoff rather than failing the run outright. The
    /// outcome shown is inferred rather than tracked separately, off the run's own
    /// <see cref="RunDetails.FailureReason"/>: the plain "reported an error result" text is the
    /// one case where a retry's own resumed leg errored a second consecutive time and spent the
    /// run's one retry, while <see cref="RunDetails.ErrorResultRetryCouldNotResume"/> names the
    /// other failure shape apart — the retry's spawn itself never got the chance to run
    /// (missing docs, or the resume throwing) — rather than reporting it as the same "errored
    /// twice" outcome the agent never actually produced (independent pre-PR review, cycle 1,
    /// adversarial finding: AGENTS.md's "never guess at unobserved facts"). Anything else means
    /// at least one retried leg went on to complete normally.
    /// </summary>
    private static void WriteSessionErrorRetries(RunDetails? run)
    {
        if (run is not { SessionErrorRetries.Count: > 0 })
        {
            return;
        }

        string legs = string.Join(", ", run.SessionErrorRetries.Select(SessionErrorRetryLabel));
        string reason = run.FailureReason ?? string.Empty;
        string outcome = run.State != RunState.Failed
            ? "[green]recovered[/]"
            : reason == RunDetails.ErrorResultRetryCouldNotResume
                ? "[red]the retry itself could not be resumed — the run failed[/]"
                : reason.Contains("reported an error result", StringComparison.OrdinalIgnoreCase)
                    ? "[red]a retry still errored a second time — the run failed[/]"
                    : "[green]recovered[/]";

        AnsiConsole.MarkupLine(
            $"\n[bold]Session error retries[/]  {run.SessionErrorRetries.Count} ({legs.EscapeMarkup()}) {outcome}");
    }

    /// <summary>
    /// Every automatic uncommitted-work recovery the newest run has gotten (task: when a session
    /// ends with finished work uncommitted, the daemon recovers on its own) — a commit-only
    /// session spawned onto the retained worktree before the run was allowed to fail on a dirty
    /// tree, at most one per leg (task: a headless build, fix, or recovery session never ends its
    /// turn while a gate it started is still running in the background — a run's build leg
    /// spending its own attempt must not read as the whole run having spent its only one). The
    /// outcome is read from each recovery's own recorded verdict — a fresh re-detection of the
    /// worktree, taken at the time that recovery session ended, never inferred from whatever the
    /// run's own state happens to be by the time this renders: a run that recovered cleanly can
    /// still fail later for an unrelated reason (a downstream gate, a push refusal), and reading
    /// that as "the recovery also ended dirty" would be false (independent pre-PR review, cycle 1,
    /// both lenses). A null <see cref="UncommittedWorkRecoveryRecord.RecoveredCleanly"/> is
    /// ambiguous on its own — it is also what a completed recovery records when its own
    /// re-detection could not read `git status` — so this reads <see cref="UncommittedWorkRecoveryRecord.CompletedAt"/>
    /// too, to tell "not finished yet" apart from "finished, but genuinely unobservable" rather than
    /// rendering both as "outcome not yet recorded" (independent pre-PR review, cycle 3, conformance
    /// finding). <see cref="UncommittedWorkRecoveryRecord.DiscardedFiles"/> also gets its own
    /// wording on a false verdict: the tree can read perfectly clean and still be a discard, when
    /// a stranded file was reverted or deleted rather than committed, so "still did not leave the
    /// tree clean" would misstate what was actually observed (independent pre-PR review, cycle 3,
    /// adversarial finding).
    /// </summary>
    private static void WriteUncommittedWorkRecovery(RunDetails? run)
    {
        if (run is not { UncommittedWorkRecoveries.Count: > 0 })
        {
            return;
        }

        foreach (UncommittedWorkRecoveryRecord recovery in run.UncommittedWorkRecoveries)
        {
            string outcome = recovery switch
            {
                { RecoveredCleanly: true } => "[green]recovered — the run reached its gates[/]",
                { RecoveredCleanly: false, DiscardedFiles.Count: > 0 } =>
                    "[red]discarded stranded work rather than committing it[/]",
                { RecoveredCleanly: false } => "[red]still did not leave the tree clean[/]",
                { CompletedAt: null } => "[yellow]outcome not yet recorded[/]",
                _ => "[yellow]recovery finished, but its own re-check could not read the worktree — outcome unknown[/]",
            };
            string legLabel = LegLabel(recovery.Leg) is { } label ? $" ({label})" : string.Empty;

            AnsiConsole.MarkupLine(
                $"\n[bold]Uncommitted-work recovery[/]{legLabel.EscapeMarkup()}  attempted {recovery.AttemptedAt.ToLocalTime():g} "
                + $"({recovery.StrandedFiles.Count} file(s)) {outcome}");

            if (recovery.DiscardedFiles.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]discarded rather than committed:[/] {string.Join(", ", recovery.DiscardedFiles).EscapeMarkup()}");
            }
        }
    }

    private static string SessionErrorRetryLabel(SessionErrorRetryRecord retry)
    {
        string label = LegLabel(retry.Leg)
            ?? (retry.Lens is { } lens && lens != ReviewLens.Unknown
                ? $"{lens.Value.ToLowerInvariant()} review pass"
                : "review pass");
        return retry.Cycle is { } cycle ? $"{label} (cycle {cycle})" : label;
    }

    /// <summary>
    /// The human label for a <see cref="RunSessionLeg"/> shared between
    /// <see cref="SessionErrorRetryLabel"/> and <see cref="WriteUncommittedWorkRecovery"/> — the
    /// two renderers that name which leg a run event belongs to. Null for
    /// <see cref="RunSessionLeg.ReviewPass"/> and <see cref="RunSessionLeg.Unknown"/>: a review
    /// pass names itself by lens instead (<see cref="SessionErrorRetryLabel"/>'s own fallback),
    /// and Unknown is what a pre-this-field stream reads as (<see cref="UncommittedWorkRecoveryRecord"/>'s
    /// own doc) — a caller decides what "no leg recorded" should say rather than this guessing one.
    /// </summary>
    private static string? LegLabel(RunSessionLeg leg) =>
        leg == RunSessionLeg.Build
            ? "build session"
            : leg == RunSessionLeg.Fix
                ? "fix session"
                : leg == RunSessionLeg.HumanResolvedFix
                    ? "human-resolved fix session"
                    : leg == RunSessionLeg.RebaseRecovery
                        ? "rebase-recovery session"
                        : leg == RunSessionLeg.SettlingGateRepair
                            ? "Settling-gate repair session"
                            : null;

    /// <summary>
    /// Every changes-requested review this task's pull request has taken, and what each fix lap
    /// did about it (task: a changes-requested pull-request review from a human becomes a fix
    /// lap): who asked, when, how many findings, and — under the review a disagreement names —
    /// what the lap could not accept, what it would have said, and whether the implementer has
    /// sent it yet. One row per review rather than per observation of it, since a review a lap
    /// did not satisfy is read again by the next sweep.
    /// <para>
    /// A disagreement that named no review is rendered unattributed rather than filed under
    /// whichever review happened to be first: a lap can answer more than one reviewer, and the
    /// pairing is only a fact when the session stated it (AGENTS.md's never-guess rule).
    /// </para>
    /// </summary>
    private static void WriteChangesRequestedReviews(IReadOnlyList<RunDetails> runs)
    {
        foreach (string line in ComposeChangesRequestedReviews(runs))
        {
            AnsiConsole.MarkupLine(line);
        }
    }

    /// <summary>
    /// The block's markup lines, composed rather than printed, so what a reader sees is assertable
    /// (test: changes-requested rendering coverage) — the same split every pure rendering helper on
    /// this command already uses (<see cref="DoneReason"/>, <see cref="StateGloss"/>). Empty when
    /// this task has never taken a changes-requested review, which is what makes the block absent
    /// rather than an empty heading.
    /// <para>
    /// Every outside string — a reviewer's login, their prose, the drafted reply — goes through
    /// <see cref="ExternalText.OneLineMarkup"/>, which is what keeps a reviewer's stray bracket
    /// from being read as Spectre markup.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> ComposeChangesRequestedReviews(IReadOnlyList<RunDetails> runs)
    {
        List<ChangesRequestedReviewObservation> observations =
            [.. runs.SelectMany(run => run.ChangesRequestedReviewObservations)];
        List<ReviewDisagreement> disagreements = [.. runs.SelectMany(run => run.ChangesRequestedDisagreements)];
        List<ReviewDisagreementReplyDirection> directions =
            [.. runs.SelectMany(run => run.ChangesRequestedReplyDirections)];
        if (observations.Count == 0 && disagreements.Count == 0)
        {
            return [];
        }

        List<string> lines = ["\n[bold]Changes-requested reviews[/]"];
        // One row per review, not per observation: the review's own url is the obstruction identity
        // closeout keys on (CloseoutEngine.ObstructionKey), so a lap that pushed nothing the
        // reviewer accepted leaves the next sweep re-reading the identical url and appending a
        // second PullRequestChangesRequested. Rendering per observation printed that review twice,
        // each row repeating the same parked disagreement underneath — the disagreements key on the
        // url, not on which sweep saw it (independent pre-PR review, cycle 1, adversarial lens).
        // Grouped by url, with the earliest read's facts on the row and the later reads counted
        // beside them: that a reviewer's review was still standing after a lap is a fact of this
        // task's history, and duplicate rows stated it by accident rather than saying it. An
        // observation whose url the provider never reported groups with nothing — two reviews
        // nobody can name are not thereby the same review (AGENTS.md's never-guess rule), so each
        // keeps its own row exactly as it had before.
        IEnumerable<IGrouping<string, ChangesRequestedReviewObservation>> reviews = observations
            .Select((observation, index) => (observation, key: observation.ReviewUrl.IsNotBlank()
                ? observation.ReviewUrl
                : $"unidentified review {index}"))
            .GroupBy(read => read.key, read => read.observation, StringComparer.Ordinal);
        foreach (IGrouping<string, ChangesRequestedReviewObservation> review in reviews)
        {
            // Ordered by observation time rather than taken by position: the row's "still standing
            // at N later sweeps" is a claim about when, and the run order this reads across is
            // dispatch order rather than anything that guarantees it.
            ChangesRequestedReviewObservation[] reads = [.. review.OrderBy(read => read.ObservedAt)];
            ChangesRequestedReviewObservation first = reads[0];
            ChangesRequestedReviewObservation latest = reads[^1];
            int rereads = reads.Length - 1;
            // The provider's own submission time when it reported one, this install's observation
            // time otherwise — labelled, so the two are never read as the same fact.
            string when = first.SubmittedAt is { } submitted
                ? $"{submitted.ToLocalTime():g}"
                : $"observed {first.ObservedAt.ToLocalTime():g}, submission time not reported";
            // A later read's finding count is named only when it differs from the first, which
            // closeout's thread read being capped at the pull request's first 100 threads can
            // genuinely produce: collapsing the rows must not quietly drop a read that saw
            // something else.
            string reread = (rereads, latest.FindingCount == first.FindingCount) switch
            {
                (0, _) => "",
                (_, true) => $", still standing at {Sweeps(rereads)} "
                    + $"(last read {latest.ObservedAt.ToLocalTime():g})",
                (_, false) => $", still standing at {Sweeps(rereads)} "
                    + $"(last read {latest.ObservedAt.ToLocalTime():g}: {Findings(latest.FindingCount)})",
            };
            lines.Add(
                $"  [yellow]@{ExternalText.OneLineMarkup(first.Reviewer)}[/] "
                + $"[dim]{when} — {Findings(first.FindingCount)}{reread}[/] "
                + $"[link]{ExternalText.OneLineMarkup(first.ReviewUrl)}[/]");
            // OrdinalIgnoreCase, the comparison ReviewResolveCommand's own vet of this exact pair
            // uses: the disagreement's url came out of a fix session's free-text summary while the
            // observation's came off the provider, so casing alone can differ between two spellings
            // of one review. Compared case-sensitively, a reply the resolve command accepts and
            // posts against that review rendered here under "not attributed to a specific review",
            // leaving the two surfaces disagreeing about one fact (independent pre-PR review, cycle
            // 1, both lenses).
            lines.AddRange(ComposeDisagreements(disagreements.Where(
                d => string.Equals(d.ReviewUrl, first.ReviewUrl, StringComparison.OrdinalIgnoreCase))));
        }

        // Never dropped for want of a review to sit under: a disagreement the session left
        // unattributed still parked a run and still awaits a human's decision. The same
        // OrdinalIgnoreCase pairing as above, so a disagreement is never both rendered under its
        // review and repeated here as unattributed.
        IReadOnlyList<ReviewDisagreement> unattributed =
            [.. disagreements.Where(d => !observations.Exists(
                o => string.Equals(o.ReviewUrl, d.ReviewUrl, StringComparison.OrdinalIgnoreCase)))];
        if (unattributed.Count > 0)
        {
            lines.Add("  [dim]disagreements not attributed to a specific review[/]");
            lines.AddRange(ComposeDisagreements(unattributed));
        }

        // Listed once, after the reviews, rather than under each: one h9k review resolve directs
        // whatever its park held rather than a single finding, so nesting a direction under a
        // particular review would claim a pairing nobody recorded.
        foreach (ReviewDisagreementReplyDirection direction in directions)
        {
            string what = direction.PostedTarget is not { } target
                ? "nothing was posted — the reviewer has heard nothing"
                : $"posted {(direction.Choice == ReviewDisagreementReplyChoice.Edited ? "an edited reply" : "the drafted reply")} "
                    + $"to {ExternalText.OneLineMarkup(target)}";
            lines.Add($"  [green]you directed:[/] [dim]{what} ({direction.DirectedAt.ToLocalTime():g})[/]");
        }

        return lines;

        static string Findings(int count) => count == 1 ? "1 finding" : $"{count} findings";

        static string Sweeps(int count) => count == 1 ? "1 later sweep" : $"{count} later sweeps";
    }

    /// <summary>One review's parked disagreements — the reviewer's point, the session's position, and the draft nobody has sent.</summary>
    private static IEnumerable<string> ComposeDisagreements(IEnumerable<ReviewDisagreement> disagreements)
    {
        foreach (ReviewDisagreement disagreement in disagreements)
        {
            string at = disagreement.Location.IsNotBlank()
                ? ExternalText.OneLineMarkup(disagreement.Location!)
                : "the review's own body (no thread to reply inside)";
            yield return $"    [red]disagreed[/] [dim]at {at}[/]";
            if (disagreement.Finding.IsNotBlank())
            {
                yield return $"      [dim]reviewer asked:[/] {ExternalText.OneLineMarkup(disagreement.Finding)}";
            }

            if (disagreement.Reasoning.IsNotBlank())
            {
                yield return $"      [dim]session's reasoning:[/] {ExternalText.OneLineMarkup(disagreement.Reasoning)}";
            }

            yield return disagreement.ProposedReply.IsNotBlank()
                ? $"      [dim]proposed reply:[/] {ExternalText.OneLineMarkup(disagreement.ProposedReply)}"
                : "      [dim]proposed reply: none drafted[/]";
        }
    }

    /// <summary>
    /// Closeout's mechanical rebase-before-reopen fast path (recommendation 3, idea fc85f609): the
    /// most recent attempt across every one of the task's runs, if any — a clean apply pushed with
    /// no reopen, or a fallback and why, immediately followed (still in the same sweep) by the
    /// ordinary reopen-and-review lap the fallback reads as ongoing. Not necessarily the newest run
    /// by dispatch order: a fallback supersedes the run it recorded the outcome on within the same
    /// sweep that dispatches the follow-up, so the caller selects by <c>LastMechanicalRebaseAt</c>
    /// across every run rather than passing the newest one (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    private static void WriteMechanicalRebaseOutcome(RunDetails? run)
    {
        if (run is not { LastMechanicalRebaseAt: not null })
        {
            return;
        }

        string detail = ExternalText.OneLineMarkup(run.LastMechanicalRebaseDetail ?? string.Empty);
        string outcome = run.LastMechanicalRebaseSucceeded == true
            ? $"[green]clean-pushed[/] [dim]— {detail}[/]"
            : $"[yellow]fell back to the full review lap[/] [dim]— {detail}[/]";

        AnsiConsole.MarkupLine(
            $"\n[bold]Mechanical rebase[/]  {outcome} "
            + $"[dim]({run.LastMechanicalRebaseAt.Value.ToLocalTime().ToString("g").EscapeMarkup()})[/]");
    }

    /// <summary>
    /// Whatever rebase this run took before anything was pushed (task: a run rebases its branch
    /// onto the current base branch) — the before-push counterpart to
    /// <see cref="WriteMechanicalRebaseOutcome"/>'s after-push line, selected across every run the
    /// same defensive way for the same reason: this feature never reopens the task on its own, but
    /// an ordinary <c>h9k task retry</c> after a Failed run still starts a fresh one.
    /// <para>
    /// Labelled for when it happens rather than for what it precedes, and PLAN.md #138's own
    /// "rendered as Pre-final-pass rebase" is superseded by that (task: a stacked child absorbs its
    /// parent's post-delivery churn safely, #146): a stacked child's first checkpoint rebase lands
    /// before its own first review cycle, hours before any final pass, and the recorded detail
    /// beside this label says so — a heading that contradicted the sentence under it is the
    /// word-versus-mark contradiction this repo has already been bitten by once (2026-08-22).
    /// </para>
    /// </summary>
    private static void WritePreFinalPassRebaseOutcome(RunDetails? run)
    {
        if (run is not { LastPreFinalPassRebaseAt: not null })
        {
            return;
        }

        string detail = ExternalText.OneLineMarkup(run.LastPreFinalPassRebaseDetail ?? string.Empty);
        string outcome = run.LastPreFinalPassRebaseWasNoOp == true
            ? $"[dim]no-op[/] [dim]— {detail}[/]"
            : run.LastPreFinalPassRebaseRecovered
                ? $"[yellow]recovered by a narrow session[/] [dim]— {detail}[/]"
                : $"[green]clean[/] [dim]— {detail}[/]";

        AnsiConsole.MarkupLine(
            $"\n[bold]Pre-push rebase[/]  {outcome} "
            + $"[dim]({run.LastPreFinalPassRebaseAt.Value.ToLocalTime().ToString("g").EscapeMarkup()})[/]");
    }

    /// <summary>
    /// The pre-approved task's own auto-merge attempt line (task: a task can be published
    /// pre-approved) — deterministic daemon code, never an agent (design ruling 8). Not
    /// necessarily the newest run by dispatch order for the same reason the mechanical-rebase
    /// line above is not: the caller selects by <c>LastAutoMergeAttemptedAt</c> across every run.
    /// </summary>
    private static void WriteAutoMergeOutcome(RunDetails? run)
    {
        if (run is not { LastAutoMergeAttemptedAt: not null })
        {
            return;
        }

        string outcome = run.LastAutoMergeSucceeded == true
            ? "[green]merged[/]"
            : $"[yellow]failed[/] [dim]— {ExternalText.OneLineMarkup(run.LastAutoMergeFailureReason ?? "reason not recorded")}[/]";

        AnsiConsole.MarkupLine(
            $"\n[bold]Auto-merge[/]  {outcome} "
            + $"[dim]({run.LastAutoMergeAttemptedAt.Value.ToLocalTime().ToString("g").EscapeMarkup()})[/]");
    }

    /// <summary>
    /// The Runs table's own Gates cell (task: gate wall-clock duration is recorded and
    /// surfaced): each gate this run's most recently recorded verification pass or failure
    /// carried, name and duration beside its own outcome. "-" covers two different observed
    /// facts alike: a run recorded before this field existed or one that has not verified yet
    /// (an unobserved duration, never a claimed zero), and a project with no verify commands
    /// configured, which deliberately records an empty list (<c>VerificationRunner</c>'s own
    /// "no gates configured" pass) rather than leaving the field unset. Both read as "-" here
    /// because there is nothing to show either way; <see cref="GateDuration"/>'s own null-vs-empty
    /// distinction is preserved on the read model for anything that needs to tell them apart.
    /// </summary>
    private static string FormatGateDurations(List<GateDuration>? gateDurations) =>
        gateDurations is not { Count: > 0 } durations
            ? "-"
            : string.Join(", ", durations.Select(gate =>
                $"{gate.Gate.EscapeMarkup()} {FormatDuration(gate.Duration)}{(gate.Passed ? string.Empty : " [red]✗[/]")}"));

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
        : duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s"
            : $"{duration.TotalSeconds:0.#}s";

    /// <summary>
    /// The plain flag for a gate whose newest recorded duration materially exceeds this
    /// project's own recent recorded average for that gate (task: gate wall-clock duration is
    /// recorded and surfaced — origin incident 2026-09-01, a suite that roughly doubled in a
    /// week going unnoticed for three days). Read live from <see cref="GateDurationHistoryQuery"/>
    /// every time, never precomputed: the comparison is against whatever recent runs actually
    /// recorded, and stays honest as more of them land. Says nothing at all for a gate with too
    /// few recorded runs to compare against, rather than inventing a norm. History is loaded
    /// once for the whole run, not once per gate (independent pre-PR review, cycle 1), and only
    /// a gate this pass actually passed is compared — a failed gate's own truncated duration is
    /// already flagged by the ✗ beside it, and comparing it for "too long" on top of that would
    /// be a second, less honest signal about the same failure. Each comparison is scoped to
    /// samples that ran under the same full/scoped classification as this pass's own gate
    /// (<see cref="GateDuration.RanFullScope"/>), so a fix cycle's narrowed reverify never gets
    /// averaged in against a full suite run of the same gate.
    /// </summary>
    private static async Task WriteGateDurationAnomaliesAsync(
        IQuerySession session, Guid projectId, RunListItem newestRun, CancellationToken cancellationToken)
    {
        if (newestRun.GateDurations is not { Count: > 0 } durations)
        {
            return;
        }

        GateDurationHistory history = await GateDurationHistoryQuery.LoadRecentHistoryAsync(
            session, projectId, newestRun.Id, cancellationToken);

        List<string> lines = [];
        foreach (GateDuration gate in durations)
        {
            if (!gate.Passed)
            {
                continue;
            }

            GateDurationComparison? comparison = history.Compare(gate.Gate, gate.Duration, gate.RanFullScope);
            if (comparison is null)
            {
                continue;
            }

            string classification = gate.RanFullScope ? "full-scope" : "scoped";
            lines.Add(
                $"[yellow]{comparison.Gate.EscapeMarkup()}[/] took {FormatDuration(comparison.Observed)} — "
                + $"well above this project's recent average of {FormatDuration(comparison.RecentAverage)} "
                + $"against {comparison.SampleCount} comparable {classification} passing run(s) [dim](drawn from "
                + "recently recorded runs, not a fixed baseline)[/]");
        }

        if (lines.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Gate duration flag[/]");
        foreach (string line in lines)
        {
            AnsiConsole.MarkupLine($"  {line}");
        }
    }

    /// <summary>
    /// A run's worktree path and branch, as a per-run detail line rather than two more columns on
    /// the Runs table (independent pre-PR review, cycle 1, conformance finding): an absolute
    /// worktree path routinely runs 60+ characters, which forces the table to wrap in a default
    /// 80-120 column terminal and squeezes every other column with it. This mirrors the shape
    /// <see cref="WriteSessionsAsync"/> already uses for the same reason, header included. A run
    /// whose coordinates were never recorded (a stream written before this task, or a
    /// reconstructed record) prints nothing for that run rather than a bare "-" line.
    /// </summary>
    private static void WriteCoordinates(
        IReadOnlyList<RunListItem> runs, IReadOnlyDictionary<Guid, RunDetails> runDetailsById)
    {
        List<(RunListItem Run, RunDetails Details)> withCoordinates = [.. runs
            .Select(run => (Run: run, Details: runDetailsById.GetValueOrDefault(run.Id)))
            .Where(pair => pair.Details is { } details
                && (details.WorktreePath.IsNotBlank() || details.Branch.IsNotBlank()))
            .Select(pair => (pair.Run, Details: pair.Details!))];
        if (withCoordinates.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Worktree and branch[/]");
        foreach ((RunListItem run, RunDetails details) in withCoordinates)
        {
            string worktree = details.WorktreePath.IsNotBlank() ? details.WorktreePath : "-";
            string branch = details.Branch.IsNotBlank() ? details.Branch : "-";
            AnsiConsole.MarkupLine(
                $"  [dim]{TaskListCommand.ShortId(run.Id)}[/]  {worktree.EscapeMarkup()}  {branch.EscapeMarkup()}");
        }
    }

    /// <summary>
    /// The session identities each run's stream actually recorded (task: h9k task show surfaces
    /// each run's coordinates), so stepping into or contacting a run's agents needs no
    /// archaeology: the name printed here is the exact name that session's Claude Code process
    /// launched under (verified against `claude --help` and empirically —
    /// <see cref="Hall9k.Domain.Features.Run.SessionRoleName"/>'s own doc has the mechanism),
    /// which is what another Claude Code session addresses it by. Liveness is observed the same
    /// way the phase line already observes it (<see cref="ProcessSessionObserver"/>) — honest
    /// about a process this machine cannot check rather than assuming either way.
    /// <para>
    /// Only a run whose stream last recorded an in-flight session prints anything here: a
    /// finished run's <see cref="RunDetails.ActiveSessions"/> is cleared the moment its session
    /// ended (<see cref="RunDetailsProjection"/>'s own doc), so silence for an older run is the
    /// honest reading of "nothing was running the last time this run's stream was touched," not
    /// a gap in this display.
    /// </para>
    /// </summary>
    private static async Task WriteSessionsAsync(
        IQuerySession session, IReadOnlyList<RunListItem> runs,
        IReadOnlyDictionary<Guid, RunDetails> runDetailsById, CancellationToken cancellationToken)
    {
        List<(RunListItem Run, RunDetails Details)> withSessions = [.. runs
            .Select(run => (Run: run, Details: runDetailsById.GetValueOrDefault(run.Id)))
            .Where(pair => pair.Details is { ActiveSessions.Count: > 0 })
            .Select(pair => (pair.Run, Details: pair.Details!))];
        if (withSessions.Count == 0)
        {
            return;
        }

        Guid[] nodeIds = [.. withSessions.Select(pair => pair.Details.NodeId).Distinct()];
        Dictionary<Guid, string> nodeMachines = (await session.Query<NodeDetails>()
                .Where(n => n.Id.IsOneOf(nodeIds))
                .ToListAsync(cancellationToken))
            .ToDictionary(n => n.Id, n => n.MachineName);
        string thisMachine = Environment.MachineName;

        AnsiConsole.MarkupLine("\n[bold]Sessions[/]");
        foreach ((RunListItem run, RunDetails details) in withSessions)
        {
            bool runOnThisMachine = nodeMachines.GetValueOrDefault(details.NodeId) == thisMachine;
            foreach (ActiveSession active in details.ActiveSessions)
            {
                bool onThisMachine = active.MachineName.IsNotBlank()
                    ? active.MachineName == thisMachine
                    : runOnThisMachine;
                SessionLiveness liveness = ProcessSessionObserver.Instance.Observe(
                    active.ProcessId, active.StartedAt, onThisMachine);
                // A self-registered session's name comes from TaskRegisterSessionCommand reading
                // ~/.claude/sessions/<pid>.json's own name field — the operator's own --name, or
                // one Claude Code derived from the launch directory — not a platform-authored
                // value, so it goes through ExternalText the same way an adopted issue's title
                // does: EscapeMarkup alone neutralises only Spectre's own syntax, not a terminal
                // control sequence or bidirectional override this session's own name could carry
                // (independent pre-PR review, adversarial lens, cycle 1).
                string name = active.Name.IsNotBlank() ? active.Name : "-";
                AnsiConsole.MarkupLine(
                    $"  [dim]{TaskListCommand.ShortId(run.Id)}[/]  {ExternalText.OneLineMarkup(name)}  "
                    + $"pid {active.ProcessId}  {LivenessMarkup(liveness)}");
            }
        }
    }

    /// <summary>
    /// The same three phrases <see cref="TaskPhase"/>'s own liveness markup uses (its mapping is
    /// private to that record), so a session named here reads identically to the phase line's own
    /// "session alive" / "the recorded process is gone" / "session liveness not observed here" —
    /// one vocabulary for what was actually observed, not two that could drift apart.
    /// </summary>
    private static string LivenessMarkup(SessionLiveness liveness) => liveness switch
    {
        SessionLiveness.Alive => "[dim]session alive[/]",
        SessionLiveness.Gone => "[red]the recorded process is gone[/]",
        SessionLiveness.Unobserved => "[dim]session liveness not observed here[/]",
        _ => string.Empty,
    };

    /// <summary>
    /// The residuals that were meant to be routed away — to a draft bug task or the standing
    /// sweep — and were not, said out loud rather than counted as routed. "Routed" is what tells
    /// a reader the defect is written down somewhere they can find it; for these it is written
    /// down nowhere but the run stream, and that is the opposite fact. Normally there are none
    /// and the line says nothing extra.
    /// </summary>
    private static string UnroutedClause(RunDetails run) => run.ReviewResidualsRoutingFailed > 0
        ? $", {run.ReviewResidualsRoutingFailed} not routed — routing failed"
        : string.Empty;

    /// <summary>
    /// Ride-alongs (Decisions Log #87) never folded into a fix session this run happened to
    /// dispatch for another reason — below the fix bar on their own, so no cycle was spent
    /// earning them one, and recorded rather than fixed. Names the bar itself, not only the
    /// count: a reader asking why a cycle did not dispatch a fix run over these should not have
    /// to read the raw stream to learn what rule decided it. The bar is not one fixed severity
    /// range, though (Decisions Log #119): an ordinary cycle rides along a Low or ungraded
    /// finding, while the mandatory FinalFullPass immediately before the pull request opens
    /// tightens that same bar to High alone, so a residual it recorded may be a Medium — stating
    /// a single "medium/high" bar here would misdescribe that one. Normally there are none and
    /// the line says nothing extra.
    /// </summary>
    private static string RideAlongClause(RunDetails run) => run.ReviewResidualsRideAlong > 0
        ? $", {run.ReviewResidualsRideAlong} ride-along(s) — each below the fix bar of the cycle that recorded it "
          + "(low/ungraded on an ordinary cycle, medium/low/ungraded on the mandatory final pass) — and never claimed"
        : string.Empty;

    /// <summary>
    /// The opposite fact from <see cref="RideAlongClause"/> (Decisions Log #87, adversarial
    /// review, the routed finding that opened this task): a finding the platform had already decided
    /// met the fix bar, whose track was still active when the run settled without a fix session
    /// ever reading it — most often a human resolving a capped park with `h9k review resolve
    /// --merge-ready`. Folding this into the ride-along count would understate it as polish nobody
    /// was owed; a settled line that never names it at all reads as though nothing serious was
    /// left behind. Normally there are none and the line says nothing extra.
    /// </summary>
    private static string UnfixedClause(RunDetails run) => run.ReviewResidualsUnfixed > 0
        ? $", {run.ReviewResidualsUnfixed} left unfixed — decided fix-here but never handed to a fix session"
        : string.Empty;

    /// <summary>
    /// <see cref="UnfixedClause"/>'s findings themselves, named the same way
    /// <see cref="WriteRideAlongFindings"/> names its own tally. A run settled before this field
    /// existed has an empty list even when the count above is non-zero, an honest gap rather than
    /// a reconstruction.
    /// </summary>
    private static void WriteUnfixedFindings(RunDetails? run)
    {
        if (run is null || run.ReviewUnfixedFindings.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Left unfixed[/]  [dim]decided fix-here but never handed to a fix session[/]");
        foreach (ReviewUnfixedFinding finding in run.ReviewUnfixedFindings)
        {
            string severity = finding.Severity == ReviewSeverity.Unknown ? "ungraded" : finding.Severity.Value.ToLowerInvariant();
            string location = finding.Location.IsBlank() ? "no location stated" : finding.Location;
            AnsiConsole.MarkupLine($"  [red]{severity}[/] — {ExternalText.OneLineMarkup(location)}");
        }
    }

    /// <summary>
    /// The ride-alongs themselves, named rather than left to the count above (independent pre-PR
    /// review, cycle 2, conformance finding): the pull request body points here for "the review
    /// history", and until this line existed that pointer was circular — this command rendered
    /// only the same count the body already had. Each entry is <see cref="RunDetails.ReviewRideAlongFindings"/>'s
    /// own severity and location, named the same way <c>PullRequestBody</c> now does. A run
    /// settled before that field existed has an empty list even when the count above is non-zero,
    /// which is an honest gap rather than a reconstruction, so this prints nothing extra for it.
    /// </summary>
    private static void WriteRideAlongFindings(RunDetails? run)
    {
        if (run is null || run.ReviewRideAlongFindings.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Ride-alongs[/]  [dim]below the fix bar of the cycle that recorded them, never claimed[/]");
        foreach (ReviewRideAlongFinding finding in run.ReviewRideAlongFindings)
        {
            string severity = finding.Severity == ReviewSeverity.Unknown ? "ungraded" : finding.Severity.Value.ToLowerInvariant();
            string location = finding.Location.IsBlank() ? "no location stated" : finding.Location;
            AnsiConsole.MarkupLine($"  [yellow]{severity}[/] — {ExternalText.OneLineMarkup(location)}");
        }
    }

    /// <summary>
    /// What this task hands down, once its run reaches true closeout (Decisions Log #36) — the
    /// reciprocal of the starting-context section above, and the surface that makes a missing
    /// handoff visible on the task that failed to leave one rather than only on the dependents
    /// that go without it.
    /// <para>
    /// It reads through <see cref="BlockerHandoffQuery"/> against this task's own id, because
    /// "what does this task hand down" is the same question the query answers about a blocker;
    /// asking it twice in two ways is how the two answers start to disagree.
    /// </para>
    /// </summary>
    private static async Task WriteHandoffAsync(
        IQuerySession session, TaskDetails details, IReadOnlyList<RunListItem> runs,
        CancellationToken cancellationToken)
    {
        // Nothing has closed out yet, so there is nothing to hand down and no absence to
        // report either: a task still working has simply not been asked.
        if (!runs.Any(run => run.State == RunState.Completed))
        {
            return;
        }

        IReadOnlyList<BlockerHandoff> own = await BlockerHandoffQuery.LoadAsync(
            session, [details.Id], cancellationToken);
        if (own is not [{ } handoff])
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Handoff[/] [dim](what a task blocked by this one would start with)[/]");
        if (handoff is { HasSummary: true, Summary: { } summary })
        {
            // The agent wrote this, and for an adopted task the agent has just been told to quote
            // the issue body into what it writes. That makes a handoff a carrier for outside text
            // by design, not by accident, so it is printed the way outside text is printed.
            AnsiConsole.WriteLine(ExternalText.ForTerminal(summary));
            return;
        }

        AnsiConsole.MarkupLine(
            $"  [dim]none recorded: {handoff.Outcome.Describe().EscapeMarkup()}. "
            + "Dependents fall back to this task's objective and acceptance criteria.[/]");
    }

    /// <summary>
    /// The stacked parent that is a pull request another install owns (task: a stacked child can
    /// stand on a pull request another install owns). It gets a block of its own rather than a row
    /// in "Blocked by", because it is not a blocker: there is no local task, so nothing here can be
    /// listed among this task's dependencies.
    /// <para>
    /// Everything printed is labelled as an observation and carries when it was made. That is not
    /// hedging — the state is read on the closeout watcher's cadence, so it genuinely can be a few
    /// minutes behind GitHub, and a human comparing this screen against the pull request in a
    /// browser needs to be able to tell a lag from a disagreement.
    /// </para>
    /// </summary>
    private static async Task WriteRemoteStackedParentAsync(
        IQuerySession session, TaskDetails details, CancellationToken cancellationToken)
    {
        if (details.StackedOnPullRequestNumber is not { } parentNumber)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[bold]Stacked on[/] [dim](a pull request another install owns)[/]");

        string headBranch = details.RemoteStackedParentHeadBranch.IsNotBlank()
            ? $"branch [blue]{details.RemoteStackedParentHeadBranch.EscapeMarkup()}[/]"
            : "[dim]no head branch observed[/]";
        string link = details.RemoteStackedParentUrl.IsNotBlank()
            ? $" [link]{details.RemoteStackedParentUrl.EscapeMarkup()}[/]"
            : string.Empty;
        AnsiConsole.MarkupLine($"  pull request [blue]#{parentNumber}[/], {headBranch}{link}");

        AnsiConsole.MarkupLine(details.RemoteStackedParentObservedAt is { } observedAt
            // "as of", not "last looked at": an unchanged look appends nothing
            // (RemoteStackedParentObserved's own doc), so this timestamp is when the reading last
            // CHANGED. Spelled out because the whole point of the block is letting a human compare
            // it against the pull request in a browser — reading it as the last look would have
            // them conclude the sweep has been down for the hours a stable parent sat unchanged
            // (independent pre-PR review, cycle 1, adversarial lens).
            ? $"  observed [yellow]{details.RemoteStackedParentState.Describe().EscapeMarkup()}[/] "
              + $"[dim]as of {observedAt:g} — when that reading last changed, not when the sweep last "
              + "looked: a look that sees no change records nothing[/]"
            // Never looked at yet, which is what a child added moments ago reads as. Said plainly
            // rather than shown as a state, because "not observed" is not a state the pull request
            // is in (AGENTS.md's never-guess rule).
            : "  [dim]not observed yet — the closeout watcher's sweep looks on its own cadence[/]");

        // That cadence only exists for a task the sweep actually reads: it queries tasks assigned
        // to this owner in Blocked/Queued/Claimed/Done (RemoteStackedParentSweep.SweepOnceAsync).
        // A Draft or Published child is in neither set, so its parent is never observed and it
        // would not dispatch even if it were — leaving the line above as the last word would send a
        // human off to wait for a look nothing takes (independent pre-PR review, cycle 1,
        // adversarial lens; the claim refusal carries the same correction).
        if (details.State == TaskState.Draft || details.State == TaskState.Published)
        {
            string shortId = TaskListCommand.ShortId(details.Id);
            string step = details.State == TaskState.Draft
                ? $"h9k task publish {shortId}, then h9k task assign {shortId},"
                : $"h9k task assign {shortId}";
            AnsiConsole.MarkupLine(
                "  [yellow]Nothing is watching that pull request for this task yet[/] [dim]— the sweep reads a "
                + "remote parent only for an assigned task, and an unassigned one never dispatches on its own. "
                + $"{step} holds it Blocked and starts that watch.[/]");
        }

        if (details.RemoteStackedParentDetail.IsNotBlank())
        {
            AnsiConsole.MarkupLine($"  [dim]{details.RemoteStackedParentDetail.EscapeMarkup()}[/]");
        }

        // What a child that has not dispatched is waiting for, in its own words. Only while it is
        // actually waiting: once the pull request is open (or merged) the edge has released it, and
        // repeating the bar would read as a hold that is no longer there.
        if (details.State == TaskState.Blocked && !details.RemoteStackedParentState.ReleasesChild)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]Waiting for that pull request to be open — an open pull request is the remote "
                + "parent's Delivered, and this task dispatches then, cutting its branch from that head "
                + "branch on origin[/]");
        }

        // A local task linked to the same issue or tracker item the pull request closes, when one
        // exists — nothing requires one to (Brian's ruling, 2026-09-07: the edge is declared by
        // pull request number precisely because not every repository tracks its backlog in GitHub
        // issues). Named because it is the one thread back into this install's own records for a
        // parent that otherwise lives entirely on somebody else's node.
        if (details.RemoteStackedParentWorkItem.IsNotBlank())
        {
            AnsiConsole.MarkupLine(
                $"  [dim]that pull request closes {details.RemoteStackedParentWorkItem.EscapeMarkup()}[/]");
            IReadOnlyList<TaskListItem> linked = await session.Query<TaskListItem>()
                .Where(task => task.ExternalReference == details.RemoteStackedParentWorkItem)
                .ToListAsync(cancellationToken);
            // Named, not graded: this row exists so a human can find the local task, and printing
            // a lifecycle word for it would need that task's own run to say anything honest — a
            // read this screen has no reason to pay for about a task it is only mentioning.
            foreach (TaskListItem task in linked.Where(task => task.Id != details.Id))
            {
                AnsiConsole.MarkupLine(
                    $"  [dim]tracked here as[/] {TaskListCommand.ShortId(task.Id)} "
                    + ExternalText.OneLineMarkup(task.Objective));
            }
        }
    }

    /// <summary>
    /// The context this task would receive if a node claimed it right now (Decisions Log #36):
    /// its immediate blockers' handoffs, rendered by the same
    /// <see cref="BlockerContextDocument"/> the daemon pastes into the agent's prompt. Sharing
    /// the renderer is the point — a human checking what an agent will start with is reading
    /// that context itself, not a second telling of it that could drift.
    /// <para>
    /// The screen says what it cannot know: whether the fan-in exceeds the claiming node's
    /// synthesis threshold is that node's configuration, so it is named as a possibility
    /// rather than predicted here (the AGENTS.md never-guess rule).
    /// </para>
    /// </summary>
    private static async Task WriteStartingContextAsync(
        IQuerySession session, TaskDetails details, CancellationToken cancellationToken)
    {
        if (details.BlockedBy.Count == 0)
        {
            return;
        }

        IReadOnlyList<BlockerHandoff> handoffs = await BlockerHandoffQuery.LoadAsync(
            session, details.BlockedBy, cancellationToken);
        if (BlockerContextDocument.Render(handoffs) is not { } context)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            "\n[bold]Starting context[/] [dim](what a run would be handed if this were claimed now)[/]");
        int missing = handoffs.Count(handoff => !handoff.HasSummary);
        if (missing > 0)
        {
            AnsiConsole.MarkupLine(
                $"  [dim]{missing} of {handoffs.Count} blocker(s) have no handoff yet; those fall back to their objective and criteria.[/]");
        }

        AnsiConsole.MarkupLine(
            "  [dim]Above the claiming node's blocker-synthesis threshold, a synthesis pass condenses this first.[/]\n");

        // Same reason as the handoff section above, one remove further out: this document is
        // assembled from other tasks' handoffs, so it relays what they relayed.
        AnsiConsole.WriteLine(ExternalText.ForTerminal(context));
    }

    /// <summary>
    /// Which install published the work this task mirrors, and under what id there. Written as the
    /// other install's facts and nothing more: the ids are that store's, the branch is the one the
    /// origin cuts, and the stamp is when the record was published — none of it is re-read, so none
    /// of it is presented as current.
    /// </summary>
    internal static string OriginMarkup(TaskOrigin origin)
    {
        string who = origin.NodeName.IsNotBlank()
            ? ExternalText.OneLineMarkup(origin.NodeName)
            : "[dim]an install that did not name itself[/]";
        string node = origin.NodeId == Guid.Empty
            ? string.Empty
            : $" [dim]({TaskListCommand.ShortId(origin.NodeId)})[/]";
        string branch = origin.BranchName.IsNotBlank()
            ? $", branch {ExternalText.OneLineMarkup(origin.BranchName)}"
            : string.Empty;
        // The stamp is the record's publish time, and the row has to say which event it belongs to:
        // printed straight after "read once at adoption" it read as the adoption's own moment,
        // which is the one judgment this stamp exists to inform — how old the copy that was read is
        // (TaskOrigin.PublishedAt's own doc; independent pre-PR review, cycle 1, adversarial lens).
        string when = origin.PublishedAt == DateTimeOffset.MinValue
            ? "at a time the record did not state"
            : $"{origin.PublishedAt.ToLocalTime():g}";
        return $"published by {who}{node} as task {TaskListCommand.ShortId(origin.TaskId)}{branch} "
            + $"[dim]— record published {when} and read once at adoption; adopt again to pick up its "
            + "later revisions[/]";
    }

    /// <summary>
    /// The adopted work item as something a human can click (PLAN.md §3.1a). The URL comes from
    /// the source's own rule through <see cref="WorkItemImporter"/>, so a provider Hall9k cannot
    /// place still prints its canonical reference rather than a link built on a guess.
    /// <para>
    /// The trailing note is the honest part: the platform read that item once at import and has
    /// not looked since, so the row must not read as live status.
    /// </para>
    /// </summary>
    internal static string ExternalMarkup(WorkItemImporter importer, string canonicalReference)
    {
        string label = canonicalReference.EscapeMarkup();
        return importer.WebUrl(canonicalReference) is { } url
            ? $"[link={url}]{label}[/] [dim](read once; never re-checked)[/]"
            : label;
    }

    /// <summary>
    /// The verdict a reviewer's lap submitted (Decisions Log #149) — the verdict itself, the head
    /// it was submitted against, and a link to the review when GitHub answered with one. The head
    /// is named rather than assumed current: a review is an opinion about one tree, and the pull
    /// request's head can have moved several times since. An absent review URL is left absent
    /// rather than composed from the pull request and a review id nobody read.
    /// </summary>
    private static string ReviewerVerdictMarkup(TaskDetails details)
    {
        string colour = details.ReviewerVerdict == ReviewerVerdict.Approved ? "green" : "yellow";
        string head = details.ReviewerVerdictHeadSha.IsNotBlank()
            ? $" [dim]on {details.ReviewerVerdictHeadSha[..Math.Min(12, details.ReviewerVerdictHeadSha.Length)].EscapeMarkup()}[/]"
            : string.Empty;
        string note = details.ReviewerVerdictNote.IsNotBlank()
            ? $" [dim]— {ExternalText.OneLineMarkup(details.ReviewerVerdictNote)}[/]"
            : string.Empty;
        string findings = details.ReviewerVerdictFindings.Count > 0
            ? $" [dim]({details.ReviewerVerdictFindings.Count} line comment(s))[/]"
            : string.Empty;
        string link = details.ReviewerVerdictReviewUrl.IsNotBlank()
            ? $" [link]{details.ReviewerVerdictReviewUrl.EscapeMarkup()}[/]"
            : string.Empty;
        return $"[{colour}]{details.ReviewerVerdict.Value.EscapeMarkup()}[/]{head}{findings}{note}{link}";
    }

    /// <summary>
    /// Whether true closeout closes this task's linked GitHub issue, and when (task: a task's
    /// linked GitHub issue is closed at true closeout under a configurable rule) — the effective
    /// value AND whether it was inherited from the project or set explicitly on this task, so an
    /// operator does not have to resolve the task-over-project chain by hand. Without a task
    /// override, the shown value can still be overturned by a never-close label the issue carries
    /// at closeout time — this row cannot resolve that without a live GitHub read, so it names the
    /// label list instead of rendering a value closeout might not actually honor.
    /// </summary>
    internal static string CloseLinkedIssueMarkup(TaskDetails details, ProjectDetails? project)
    {
        if (details.CloseLinkedIssue is { } taskOverride)
        {
            return $"{taskOverride.CliSpelling.EscapeMarkup()} [dim](task override)[/]";
        }

        CloseLinkedIssueRule effective = project?.CloseLinkedIssue ?? CloseLinkedIssueRule.WhenAllTasksClose;
        string labelNote = project?.NeverCloseLabels is { Count: > 0 } neverCloseLabels
            ? $" [dim](never if the issue carries one of the project's never-close labels: "
              + $"{string.Join(", ", neverCloseLabels).EscapeMarkup()})[/]"
            : string.Empty;
        return $"{effective.CliSpelling.EscapeMarkup()} [dim](inherited from the project's close-linked-issue setting)[/]{labelNote}";
    }

    /// <summary>
    /// What is happening to an outstanding publication, in the two states it has: waiting for the
    /// daemon, or running. Both are worth distinguishing, because they fail for different reasons
    /// — a request that never leaves the first state means no daemon is running, and one stuck in
    /// the second means a session that is not finishing.
    /// </summary>
    private static string PublicationMarkup(TaskDetails details, string provider)
    {
        string board = details.PendingPublicationProjectKey.HasValue
            ? $" [dim]under {details.PendingPublicationProjectKey.Value.EscapeMarkup()}[/]"
            : string.Empty;
        return details.PublicationSessionDispatched
            ? $"[yellow]{provider.EscapeMarkup()}[/]{board} [dim]— a session is composing the card; it "
              + "submits it through h9k task write-jira[/]"
            : $"[yellow]{provider.EscapeMarkup()}[/]{board} [dim]— requested, waiting for the daemon "
              + "to dispatch the session (h9k daemon status)[/]";
    }

    /// <summary>
    /// A Jira write outstanding on this task (Brian's design, 2026-08-28): pending on a rejected
    /// credential reads red, since that is exactly what h9k status's needs-you section
    /// surfaces for the same row; a write nobody has retried yet reads dim, since nothing has
    /// gone wrong — it is only waiting for the daemon's retry sweep or a fresh attempt.
    /// </summary>
    private static string JiraWriteMarkup(TaskDetails details)
    {
        string target = details.PendingJiraWriteIssueKey.IsNotBlank()
            ? $" [dim]on {details.PendingJiraWriteIssueKey.EscapeMarkup()}[/]"
            : string.Empty;
        // The fallback names no cause: "the registered Jira credential was rejected" was the same
        // misattribution this diff removed elsewhere on this flag (AuthorizeAsync classifies a
        // credential the vault could never resolve — never asked about by Jira at all — the same
        // way as one Jira itself rejected), and the recorded reason is non-blank on every path that
        // exists today, so this only guards against ever falling back to a guess (independent pre-PR
        // review, conformance lens, cycle 8).
        return details.PendingJiraWriteIsAuthFailure
            ? $"[red]{details.PendingJiraWriteOperation.Value.EscapeMarkup()}[/]{target} [dim]— "
              + $"{ExternalText.OneLineMarkup(details.PendingJiraWriteFailureReason ?? "the registered Jira connection needs attention")}[/]"
            : $"[yellow]{details.PendingJiraWriteOperation.Value.EscapeMarkup()}[/]{target} "
              + "[dim]— recorded, executing[/]";
    }

    /// <summary>
    /// Under <see cref="PreApprovalMode.AfterHumanReview"/> only: whether a human review has
    /// actually been requested on the pull request yet (task: the people a pull request is waiting
    /// on are named, and pre-approval gains a mode that waits for human review). Null in every
    /// other mode, which is what keeps the row off a screen it would say nothing on.
    /// <para>
    /// It earns a row because it is the one gate nothing else here answers and the one an owner
    /// most often has to act on: the mode holds the merge indefinitely until somebody is asked, so
    /// naming the mode and stopping there would leave the reader guessing whether it waits on a
    /// reviewer or on them. Three readings, never two — not observed at all (no closeout sweep has
    /// recorded one, or the stream predates the field) is stated as unobserved rather than folded
    /// into "not requested": the merge holds either way, but only one of the two is a fact about
    /// GitHub (AGENTS.md, never guess at unobserved facts). Reads the task's own current run, which
    /// is the run whose pull request this mode governs.
    /// </para>
    /// </summary>
    private static async Task<string?> HumanReviewRequestedMarkupAsync(
        IQuerySession session, TaskDetails details, CancellationToken cancellationToken)
    {
        if (!details.EffectivePreApproval.WaitsForHumanReview)
        {
            return null;
        }

        RunDetails? run = details.CurrentRunId is { } currentRunId
            ? await session.LoadAsync<RunDetails>(currentRunId, cancellationToken)
            : null;

        return run?.ExternalHumanReviewEverRequested switch
        {
            true => "[green]requested[/] [dim]— a human review has been asked for on the pull request[/]",
            false => "[yellow]not requested yet[/] [dim]— nothing merges until you add a reviewer in "
                + "GitHub, or switch this to on with h9k task set-pre-approved "
                + $"{TaskListCommand.ShortId(details.Id)} on[/]",
            null => "[dim]not observed — no closeout sweep has read this pull request's reviewers yet[/]",
        };
    }

    /// <summary>
    /// Whose nodes may claim this task. Unassigned is a fact, not a gap: nothing dispatches
    /// until a human assigns it (Decisions Log #34).
    /// </summary>
    private static async Task<string> AssigneeMarkupAsync(
        IQuerySession session, TaskDetails details, CancellationToken cancellationToken)
    {
        if (details.AssignedOwnerId is not { } ownerId)
        {
            return "[dim]nobody — an unassigned task never dispatches[/]";
        }

        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken);
        return owner is null ? $"[dim]{ownerId}[/]" : owner.Name.EscapeMarkup();
    }

    /// <summary>
    /// The mention the task's currently parked run actually answers, when its park came from one
    /// (idea 2f079bcd): a follow-up lap's own frozen comment id (<see cref="RunDetails.PrReviewMentionCommentId"/>,
    /// set at dispatch and never moved by a later mention), or — for a mint whose own first run
    /// carries the answer instead — the row that minted this task in the first place. Null when the
    /// currently parked run (if any) answers no mention at all, in which case the caller falls back
    /// to <c>TaskDetails.LatestMention*</c> as a purely informational "a mention was observed" line.
    /// </summary>
    private static async Task<ObservedReviewMention?> AnsweredMentionAsync(
        IQuerySession session, TaskDetails details, CancellationToken cancellationToken)
    {
        if (details.CurrentRunId is not { } runId)
        {
            return null;
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (run is null || run.State != RunState.ReviewParked)
        {
            return null;
        }

        if (run.PrReviewMentionCommentId is { } answeredCommentId)
        {
            return await session.Query<ObservedReviewMention>()
                .Where(mention => mention.CommentId == answeredCommentId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // Only the task's very first run ever carries a mint's own "You were asked" section
        // (RunLauncher's own isPrReview branch writes mention-answer.md only there — the identical
        // RunIds[0]-is-the-original-review invariant AutoPrReviewEngine.AttachMentionAsync's own
        // priorReviewRunId comment states) — a later re-review's own ReviewParked run answers no
        // mention at all, and must not resurrect an already-answered one just because this task
        // happens to have been minted from a mention once.
        if (details.RunIds.Count == 0 || details.RunIds[0] != runId)
        {
            return null;
        }

        return await session.Query<ObservedReviewMention>()
            .Where(mention => mention.TaskId == details.Id)
            .Where(mention => mention.MatchesSql("d.data ->> 'outcome' = ?", ReviewMentionOutcome.TaskCreated.Value))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The one next act for where the task actually is. The lifecycle has several explicit
    /// steps (Decisions Log #34), so every state says which one it is waiting for rather than
    /// leaving the reader to remember the graph.
    /// </summary>
    private static void AnnounceNextStep(TaskDetails details, bool hasOpenDependency)
    {
        string shortId = TaskListCommand.ShortId(details.Id);
        // h9k task work is refused outright for a pr-review task (TaskWorkCommand.ClaimAndCutAsync:
        // "it has no diff of its own for an interactive session to build"), so the hint is named
        // only where the claim would actually be accepted (DescribeDoneRemedy's own rule,
        // independent pre-PR review, cycle 3). An open dependency no longer suppresses the hint
        // (task 0ac72cb8-h9k): h9k task work now warns and asks instead of refusing outright, so
        // the hint just names the flag that answers it (adversarial lens, cycle 1).
        string interactiveClaimHint = details.Type == TaskType.PrReview
            ? string.Empty
            : hasOpenDependency
                ? $" (or h9k task work {shortId} --acknowledge-unmet-dependencies to claim across the open dependency yourself)"
                : $" (or h9k task work {shortId} to claim and work it yourself)";
        string? next = details.State.Value switch
        {
            "Draft" => details.AcceptanceCriteria.Count == 0
                ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]— publishing needs at least one[/]"
                : $"[dim]Next:[/] h9k task publish {shortId} [dim]then[/] h9k task assign {shortId}",
            "Published" => $"[dim]Next:[/] h9k task assign {shortId} [dim]— it will not run until you do"
                + $"{interactiveClaimHint}[/]",
            "Blocked" => $"[dim]It queues itself when its dependencies close out. To stop waiting:[/] "
                + $"h9k task unassign {shortId} [dim]→[/] h9k task draft {shortId} [dim]→[/] h9k task revise {shortId} --clear-dependencies"
                + $" [dim]— or claim across the open dependency yourself:[/] h9k task work {shortId} --acknowledge-unmet-dependencies",
            "Queued" => $"[dim]Waiting for a dispatch cycle on one of the assignee's nodes. To take it back:[/] h9k task unassign {shortId}",
            // A posted review waiting on its author (task: a pr-review task stays open while the
            // pull request's review threads are unresolved). Nothing is being asked of the
            // reviewer, so the hint says what the platform is doing and names the one lever that
            // stops it — never h9k task resolve, which refuses anything but a Failed task, or
            // h9k review resolve, whose park has already been resolved to get here.
            "AwaitingAuthor" => "[dim]The closeout watcher polls the pull request on its own cadence and flags "
                + "this needs-you the moment its author replies, pushes, re-requests your review, or someone "
                + "mentions this install's own login in a fresh comment on it. It reaches Done only when the "
                + $"pull request itself merges or closes. To stop watching:[/] h9k task abandon {shortId}",
            _ => null,
        };

        if (next is not null)
        {
            AnsiConsole.MarkupLine($"\n{next}");
        }
    }
}
