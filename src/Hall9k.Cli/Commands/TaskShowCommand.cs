using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskShowCommand : Hall9kAsyncCommand<TaskShowCommand.Settings>
{
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
        if (details.Model != AgentModel.Unknown)
        {
            header.AddRow("Model", $"{details.Model.Value.EscapeMarkup()} [dim](task override)[/]");
        }

        if (details.SourceIdeaId is { } sourceIdeaId)
        {
            // The other half of promotion's two-way provenance (Decisions Log #35): the idea's
            // stream names this task, and this names the idea it came from.
            header.AddRow("From idea",
                $"[dim]{TaskListCommand.ShortId(sourceIdeaId)}[/] "
                + $"[dim](h9k idea show {TaskListCommand.ShortId(sourceIdeaId)})[/]");
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

        if (details.PullRequestUrl.IsNotBlank())
        {
            header.AddRow("PR", $"[link]{details.PullRequestUrl.EscapeMarkup()}[/]");
        }

        if (details.DependencyFailureReason.IsNotBlank())
        {
            header.AddRow("Dependency", $"[red]{details.DependencyFailureReason.EscapeMarkup()}[/]");
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
            header.AddRow("Retried", $"[yellow]{details.RetryReason.EscapeMarkup()}[/]");
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

        if (details.BlockedBy.Count > 0)
        {
            IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
                session, details.BlockedBy, cancellationToken);
            AnsiConsole.MarkupLine(
                "\n[bold]Blocked by[/] [dim](met only at true closeout: the pull request merged)[/]");
            // Each blocker is named in the lifecycle vocabulary, not the persisted one
            // (Decisions Log #66). This is the one screen that explains the true-closeout rule,
            // so printing the raw state here would show a pushed-but-unmerged blocker as Done
            // beside the mark that says it is still holding this task back — the premature Done
            // the redesign exists to remove, on the screen least able to afford it.
            foreach (TaskDependency dependency in dependencies)
            {
                AnsiConsole.MarkupLine(
                    $"  {TaskStatusComposer.DependencyMark(dependency)} [dim]{TaskListCommand.ShortId(dependency.Id)}[/] "
                    + $"{ExternalText.OneLineMarkup(dependency.Objective)} "
                    + $"({TaskStatusComposer.State(dependency).Markup})");
            }
        }

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
            AnsiConsole.MarkupLine("\n[bold]Runs[/]");
            Table runsTable = new Table().Border(TableBorder.Rounded);
            runsTable.AddColumns("Run", "Gen", "State", "Model", "Dispatched", "PR");
            foreach (RunListItem run in runs)
            {
                // A run dispatched before the model chain existed recorded none; "-" says
                // unknown rather than naming a model the run may never have used.
                runsTable.AddRow(
                    $"[dim]{TaskListCommand.ShortId(run.Id)}[/]",
                    run.LeaseGeneration.ToString(),
                    run.State.Value.EscapeMarkup(),
                    (run.Model == AgentModel.Unknown ? "-" : run.Model.Value).EscapeMarkup(),
                    run.DispatchedAt.ToLocalTime().ToString("g").EscapeMarkup(),
                    (run.PullRequestUrl ?? "-").EscapeMarkup());
            }

            AnsiConsole.Write(runsTable);
            RunDetails? newestRun = await session.LoadAsync<RunDetails>(runs[^1].Id, cancellationToken);
            WriteReviewOutcome(newestRun);
            WriteFixEscalation(newestRun);
        }

        await WriteHandoffAsync(session, details, runs, cancellationToken);

        AnnounceNextStep(details);

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
            standing.AddRow("[bold]Waiting on[/]",
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
    private static string StateGloss(TaskStatusRow row) => row.State.Word switch
    {
        "Draft" => "[dim](being developed; the dispatcher cannot see it)[/]",
        "Published" => "[dim](past the readiness gate; not working yet)[/]",
        "Working" => "[dim](a run owns it and has not pushed yet)[/]",
        "Delivered" => "[dim](pushed; the merge has not been observed)[/]",
        "Done" => "[dim](true closeout: the merge was observed)[/]",
        "Failed" => "[dim](a waypoint, not an ending — log #27)[/]",
        "Archived" => "[dim](walked away from)[/]",
        _ => "[dim](this build does not recognize the recorded state)[/]",
    };

    /// <summary>
    /// How the newest run's pre-PR review ended (Decisions Log #63). Merge-ready is one word for
    /// two different things, and this line is what keeps them apart: clean means a reviewer read
    /// the final tip and found nothing, while settled means the severity gate ended the loop
    /// over findings that were fixed but never read again, or routed to bug tasks of their own.
    /// A reader deciding how much to trust a pull request should not have to dig through the run
    /// stream to learn which of those happened.
    /// </summary>
    private static void WriteReviewOutcome(RunDetails? run)
    {
        if (run is null || run.LastReviewVerdict != ReviewVerdict.MergeReady)
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
                + $"{run.ReviewResidualsRouted} routed{RideAlongClause(run)}{UnroutedClause(run)})[/] "
                + "[dim]— the loop ended without a clean re-read[/]",
            _ => "[green]merge-ready[/] [dim]— how it was reached was not recorded[/]",
        };

        AnsiConsole.MarkupLine($"\n[bold]Pre-PR review[/]  {outcome}");
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
    /// The residuals that were meant to become draft bug tasks and did not, said out loud
    /// rather than counted as routed. "Routed" is what tells a reader the defect is written
    /// down somewhere they can find it; for these it is written down nowhere but the run
    /// stream, and that is the opposite fact. Normally there are none and the line says
    /// nothing extra.
    /// </summary>
    private static string UnroutedClause(RunDetails run) => run.ReviewResidualsRoutingFailed > 0
        ? $", {run.ReviewResidualsRoutingFailed} not routed — creating the draft bug task failed"
        : string.Empty;

    /// <summary>
    /// Ride-alongs (Decisions Log #87) never folded into a fix session this run happened to
    /// dispatch for another reason — below the fix bar on their own, so no cycle was spent
    /// earning them one, and recorded rather than fixed. Names the bar itself, not only the
    /// count: a reader asking why a cycle did not dispatch a fix run over these should not have
    /// to read the raw stream to learn that "below medium" is the rule that decided it.
    /// Normally there are none and the line says nothing extra.
    /// </summary>
    private static string RideAlongClause(RunDetails run) => run.ReviewResidualsRideAlong > 0
        ? $", {run.ReviewResidualsRideAlong} ride-along(s) graded below the fix bar (medium/high) and never claimed"
        : string.Empty;

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
            ? $"[yellow]{provider.EscapeMarkup()}[/]{board} [dim]— a session is writing the card; it "
              + "finishes by running h9k task link-jira[/]"
            : $"[yellow]{provider.EscapeMarkup()}[/]{board} [dim]— requested, waiting for the daemon "
              + "to dispatch the session (h9k daemon status)[/]";
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
    /// The one next act for where the task actually is. The lifecycle has several explicit
    /// steps (Decisions Log #34), so every state says which one it is waiting for rather than
    /// leaving the reader to remember the graph.
    /// </summary>
    private static void AnnounceNextStep(TaskDetails details)
    {
        string shortId = TaskListCommand.ShortId(details.Id);
        string? next = details.State.Value switch
        {
            "Draft" => details.AcceptanceCriteria.Count == 0
                ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]— publishing needs at least one[/]"
                : $"[dim]Next:[/] h9k task publish {shortId} [dim]then[/] h9k task assign {shortId}",
            "Published" => $"[dim]Next:[/] h9k task assign {shortId} [dim]— it will not run until you do[/]",
            "Blocked" => $"[dim]It queues itself when its dependencies close out. To stop waiting:[/] "
                + $"h9k task unassign {shortId} [dim]→[/] h9k task draft {shortId} [dim]→[/] h9k task revise {shortId} --clear-dependencies",
            "Queued" => $"[dim]Waiting for a dispatch cycle on one of the assignee's nodes. To take it back:[/] h9k task unassign {shortId}",
            _ => null,
        };

        if (next is not null)
        {
            AnsiConsole.MarkupLine($"\n{next}");
        }
    }
}
