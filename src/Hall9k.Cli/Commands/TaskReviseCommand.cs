using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Draft-only revision (Decisions Log #34). Each option that is passed replaces that part of
/// the task; each one left off is left alone, so the stream never claims something was
/// retyped when it wasn't.
/// </summary>
public sealed class TaskReviseCommand : Hall9kAsyncCommand<TaskReviseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--objective <OBJECTIVE>")]
        [Description("Replace the objective: one sentence, outcome-phrased (readiness contract, PLAN.md §4)")]
        public string? Objective { get; init; }

        [CommandOption("--criteria <CRITERION>")]
        [Description(
            "Replace the whole acceptance-criteria set; repeat the option for each criterion. "
            + "Publishing requires at least one, and it must be checkable")]
        public string[] Criteria { get; init; } = [];

        [CommandOption("--context <CONTEXT>")]
        [Description("Replace the agent-facing context (pointers, constraints, boundaries)")]
        public string? AgentContext { get; init; }

        [CommandOption("--type <TYPE>")]
        [Description(
            "Change the task type: feature | bugfix | refactor | chore | research. Not pr-review — "
            + "that type needs a pull-request reference only h9k task add --from-pr attaches, so "
            + "revising an ordinary task to it here is refused")]
        public string? Type { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description(
            "Change this task's model override (Decisions Log #33): a tier alias (fable, opus, sonnet, "
            + "haiku) or an exact model id; 'default' clears the override and defers to the node's "
            + "per-role, the project's, and the platform's defaults")]
        public string? Model { get; init; }

        [CommandOption("--blocked-by <TASK>")]
        [Description(
            "Replace the whole dependency set: each task's id or an unambiguous fragment; repeat the "
            + "option for more. A dependency is met only at true closeout (its pull request merged and "
            + "the closeout monitor observed it). A cycle is allowed here and refused at publish")]
        public string[] BlockedBy { get; init; } = [];

        [CommandOption("--clear-dependencies")]
        [Description("Drop every dependency, so nothing blocks this task")]
        public bool ClearDependencies { get; init; }

        [CommandOption("--stacked-on <TASK>")]
        [Description(
            "Declare this task STACKED ON that blocker rather than merely blocked by it (see "
            + "h9k task add --stacked-on for what the edge changes): its id or an unambiguous "
            + "fragment. Implies the dependency edge, so it needs no separate --blocked-by for the "
            + "same task — but a --blocked-by passed in the same call replaces the whole set, so "
            + "include the parent there too if you pass both")]
        public string? StackedOn { get; init; }

        [CommandOption("--stacked-on-pull-request <NUMBER>")]
        [Description(
            "Declare this task STACKED ON a pull request another install owns rather than on a task in "
            + "this install's records (see h9k task add --stacked-on-pull-request for what the edge "
            + "changes): the number as GitHub shows it (264 or #264). Carries no dependency edge — there "
            + "is no local task to name — so nothing joins --blocked-by. Mutually exclusive with "
            + "--stacked-on in one call, but it needs no --clear-stacked-on to replace one: declaring a "
            + "parent replaces whatever this task stood on, across both forms")]
        public string? StackedOnPullRequest { get; init; }

        [CommandOption("--clear-stacked-on")]
        [Description(
            "Drop the stacked edge, in whichever of its two forms this task holds: its branch is cut "
            + "from the base branch and its pull request targets the base branch again. A local "
            + "parent's blocked-by dependency itself is untouched — clear that separately if you want "
            + "it gone")]
        public bool ClearStackedOn { get; init; }

        [CommandOption("--file <PATH>")]
        [Description(
            "Take the revision from a task file (frontmatter + markdown body), the same format "
            + "h9k task add --file reads. Explicit options win over the file")]
        public string? File { get; init; }

        [CommandOption("--epic <EPIC>")]
        [Description(
            "Join this epic: its id or an unambiguous fragment. Must be Open and belong to this "
            + "task's own project; a closed or another project's epic is refused. A task belongs to "
            + "at most one epic. Since h9k task revise is Draft-only (Decisions Log #34), a "
            + "Published task returns with h9k task draft <id> alone; an assigned task (Queued or "
            + "Blocked) needs h9k task unassign <id> && h9k task draft <id> first — then this "
            + "option, then publish (and assign) again")]
        public string? Epic { get; init; }

        [CommandOption("--clear-epic")]
        [Description("Leave the epic this task currently belongs to")]
        public bool ClearEpic { get; init; }

        [CommandOption("--queue-first")]
        [Description(
            "Mark this task to take the next free dispatch slot regardless of assignment age — a "
            + "recorded task-level fact (task 45136b29) the dispatcher's claim query orders on ahead "
            + "of assignment age. Clears itself automatically once the run it earns actually "
            + "dispatches. The one field this command still accepts once a task has left Draft, as "
            + "long as nothing else is revised in the same call")]
        public bool QueueFirst { get; init; }

        [CommandOption("--clear-queue-first")]
        [Description("Remove the queue-first marker without waiting for it to dispatch")]
        public bool ClearQueueFirst { get; init; }

        [CommandOption("--review-stage-composition <COMPOSITION|default>")]
        [Description(
            "Change this task's own review stage composition (task: the review pipeline's stage "
            + "composition becomes configuration recorded per run) — full-pipeline, adversarial-only, "
            + "conformance-only, skip-final-pass, or none; 'default' clears the override and defers to "
            + "the project's, then the node's, then the compiled default. skip-final-pass and none waive "
            + "Decisions Log #92's mandatory pre-merge fresh-context read; adversarial-only, "
            + "conformance-only, and none each drop a lens's own attention budget entirely — every one "
            + "of those needs --accept-reduced-review")]
        public string? ReviewStageComposition { get; init; }

        [CommandOption("--accept-reduced-review")]
        [Description(
            "Acknowledges the consequence --review-stage-composition just named, when the value passed "
            + "removes a load-bearing review guarantee. Required for skip-final-pass, none, "
            + "adversarial-only, or conformance-only; passed alongside any other value it is silently "
            + "dropped rather than refused, since there is no consequence to acknowledge there")]
        public bool AcceptReducedReview { get; init; }

        [CommandOption("--clear-interactive-mode")]
        [Description(
            "Clear the task's interactive-mode flag directly. h9k task handback and a default h9k task "
            + "release are the ordinary exit doors, but both need an active interactive claim to act on — "
            + "a headless follow-up CloseoutEngine dispatches under a real node claim while the flag is "
            + "still on, or a task that has already reached Done with its pull request open, leaves "
            + "neither door reachable. The one other field this command still accepts once a task has "
            + "left Draft (alongside --queue-first), as long as nothing else is revised in the same call")]
        public bool ClearInteractiveMode { get; init; }

        [CommandOption("--close-linked-issue <on-closeout|never|when-all-tasks-close|default>")]
        [Description(
            "Override whether true closeout closes THIS task's linked GitHub issue, and when (task: a "
            + "task's linked GitHub issue is closed at true closeout under a configurable rule). Left "
            + "unset, the task defers to the project's own close-linked-issue setting (h9k project set), "
            + "live. 'default' clears the override. The right lever for an issue that covers more than "
            + "this one task: an epic, a PRD, an ADR, or an issue split into several tasks, where an "
            + "explicit 'never' or 'on-closeout' here beats an inherited default on every sibling task")]
        public string? CloseLinkedIssue { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.ClearDependencies && settings.BlockedBy.Length > 0)
        {
            throw new DomainValidationException(
                "--clear-dependencies and --blocked-by say opposite things; pass one.");
        }

        if (settings.ClearEpic && settings.Epic.IsNotBlank())
        {
            throw new DomainValidationException("--clear-epic and --epic say opposite things; pass one.");
        }

        if (settings.ClearStackedOn && settings.StackedOn.IsNotBlank())
        {
            throw new DomainValidationException(
                "--clear-stacked-on and --stacked-on say opposite things; pass one.");
        }

        // Refused here rather than left to the decider, which would refuse it too: this command
        // resolves --stacked-on against the task store first, so a mistyped fragment would come
        // back as a not-found before the human was ever told the two options cannot travel
        // together.
        if (settings.StackedOn.IsNotBlank() && settings.StackedOnPullRequest.IsNotBlank())
        {
            throw new DomainValidationException(
                "--stacked-on and --stacked-on-pull-request name two parents, and a child stands on one; "
                + "pass one. Use --stacked-on-pull-request when the parent's run lives on somebody else's "
                + "node.");
        }

        if (settings.ClearStackedOn && settings.StackedOnPullRequest.IsNotBlank())
        {
            throw new DomainValidationException(
                "--clear-stacked-on and --stacked-on-pull-request say opposite things; pass one. Declaring a "
                + "parent already replaces whatever this task was stacked on, in either form, so a swap "
                + "needs only the new declaration.");
        }

        if (settings.QueueFirst && settings.ClearQueueFirst)
        {
            throw new DomainValidationException(
                "--queue-first and --clear-queue-first say opposite things; pass one.");
        }

        if (settings.AcceptReducedReview && settings.ReviewStageComposition is null)
        {
            throw new DomainValidationException(
                "--accept-reduced-review has nothing to acknowledge without --review-stage-composition.");
        }

        string? objective = settings.Objective;
        string? type = settings.Type;
        string? agentContext = settings.AgentContext;
        string? model = settings.Model;
        string? epic = settings.Epic;
        IReadOnlyList<string> criteria = settings.Criteria;
        IReadOnlyList<string> blockedBy = settings.BlockedBy;
        string? stackedOn = settings.StackedOn;
        string? stackedOnPullRequest = settings.StackedOnPullRequest;

        if (settings.File.IsNotBlank())
        {
            if (!System.IO.File.Exists(settings.File))
            {
                throw new DomainNotFoundException($"Task file not found: {settings.File}");
            }

            TaskFileContent file = TaskFileParser.Parse(
                await System.IO.File.ReadAllTextAsync(settings.File, cancellationToken));
            objective ??= file.Objective;
            type ??= file.Type;
            agentContext ??= file.AgentContext;
            model ??= file.Model;
            criteria = criteria.Count > 0 ? criteria : file.Criteria;
            blockedBy = blockedBy.Count > 0 ? blockedBy : file.BlockedBy;
            if (!settings.ClearEpic)
            {
                epic ??= file.Epic;
            }

            // Same shape as --clear-epic above: an explicit clear on the command line is not
            // quietly re-set by a file that still names a parent.
            if (!settings.ClearStackedOn)
            {
                stackedOn ??= file.StackedOn;
                stackedOnPullRequest ??= file.StackedOnPullRequest;
            }

            // The file can name both forms where the command line could not, and the same refusal
            // applies: a child stands on one parent. Refused here, before either is resolved, for
            // the reason the command-line guard above gives.
            if (stackedOn.IsNotBlank() && stackedOnPullRequest.IsNotBlank())
            {
                throw new DomainValidationException(
                    "stacked-on and stacked-on-pull-request name two parents, and a child stands on one; "
                    + "declare one.");
            }
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Optional<IReadOnlyList<Guid>> dependencies = settings.ClearDependencies
            ? Optional<IReadOnlyList<Guid>>.Of([])
            : blockedBy.Count > 0
                ? Optional<IReadOnlyList<Guid>>.Of(await ResolveAsync(session, blockedBy, cancellationToken))
                : Optional<IReadOnlyList<Guid>>.None;

        // A newly declared stacked edge implies its dependency edge, exactly as h9k task add's own
        // option does — the invariant TaskDecider.VetStackedEdge enforces rather than repairs. The
        // parent joins whichever set this revision leaves behind: the one --blocked-by just replaced
        // when it was passed, or the task's existing set when it was not. Only then, and only when
        // it is actually missing, so a revision that changes nothing about the set still records
        // nothing about it.
        Optional<Guid?> stackedOnTaskId = Optional<Guid?>.None;
        // Parsed before the clear below reads it, and never alongside one — the guards above
        // already refused that pairing.
        Optional<int?> stackedOnPullRequestNumber =
            StackedPullRequestOption.Parse(stackedOnPullRequest) is { } declaredNumber
                ? Optional<int?>.Of(declaredNumber)
                : Optional<int?>.None;
        if (settings.ClearStackedOn)
        {
            // Whichever form this task actually holds, so a clear on a remotely stacked child does
            // not record a local edge it never had — and so a clear on an unstacked task still
            // records something, which is what keeps `--clear-stacked-on` alone from tripping the
            // decider's nothing-to-revise guard, exactly as it did before the second form existed.
            if (task.IsStackedOnRemotePullRequest)
            {
                stackedOnPullRequestNumber = Optional<int?>.Of(null);
            }
            else
            {
                stackedOnTaskId = Optional<Guid?>.Of(null);
            }
        }
        else if (stackedOn.IsNotBlank())
        {
            Guid parentId = await TaskIdResolver.ResolveAsync(session, stackedOn, cancellationToken);
            stackedOnTaskId = Optional<Guid?>.Of(parentId);

            IReadOnlyList<Guid> effectiveDependencies = dependencies.HasValue
                ? dependencies.Value ?? []
                : task.BlockedBy;
            if (!effectiveDependencies.Contains(parentId))
            {
                dependencies = Optional<IReadOnlyList<Guid>>.Of([.. effectiveDependencies, parentId]);
            }
        }

        bool namesCurrentEpic = !settings.ClearEpic && epic.IsNotBlank() && NamesCurrentEpic(epic, task.EpicId);
        Optional<Guid?> epicId = settings.ClearEpic
            ? Optional<Guid?>.Of(null)
            : epic.IsNotBlank() && !namesCurrentEpic
                ? Optional<Guid?>.Of(await EpicIdResolver.ResolveForMembershipAsync(
                    session, epic, task.ProjectId, cancellationToken))
                : Optional<Guid?>.None;

        Optional<bool> queuePriority = settings.QueueFirst
            ? Optional<bool>.Of(true)
            : settings.ClearQueueFirst
                ? Optional<bool>.Of(false)
                : Optional<bool>.None;

        if (namesCurrentEpic && task.EpicId is { } currentEpic && objective.IsBlank() && criteria.Count == 0
            && agentContext.IsBlank() && !dependencies.HasValue && type.IsBlank() && model.IsBlank()
            && !queuePriority.HasValue && settings.ReviewStageComposition is null
            && !settings.ClearInteractiveMode && settings.CloseLinkedIssue is null)
        {
            AnsiConsole.MarkupLine(
                $"[green]Already in epic[/] {TaskListCommand.ShortId(currentEpic)}. [dim]Nothing to do.[/]");
            return ExitCodes.Ok;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskRevised revised = TaskDecider.Revise(
            task,
            objective.IsBlank() ? Optional<string>.None : Optional<string>.Of(objective),
            criteria.Count > 0 ? Optional<IReadOnlyList<string>>.Of([.. criteria]) : Optional<IReadOnlyList<string>>.None,
            agentContext.IsBlank() ? Optional<string>.None : Optional<string>.Of(agentContext),
            dependencies,
            type.IsBlank() ? Optional<TaskType>.None : Optional<TaskType>.Of(TaskType.Parse(type)),
            model.IsBlank() ? Optional<AgentModel>.None : Optional<AgentModel>.Of(AgentModel.FromInput(model)),
            DateTimeOffset.UtcNow,
            context.OwnerId,
            epicId,
            queuePriority,
            settings.ReviewStageComposition is { } composition
                ? Optional<string?>.Of(composition)
                : Optional<string?>.None,
            settings.AcceptReducedReview,
            settings.ClearInteractiveMode,
            stackedOnTaskId,
            stackedOnPullRequestNumber,
            settings.CloseLinkedIssue is { } closeLinkedIssue
                ? Optional<string?>.Of(closeLinkedIssue)
                : Optional<string?>.None);

        session.Events.Append(taskId, revised);
        await session.SaveChangesAsync(cancellationToken);
        task.Apply(revised);

        string shortId = TaskListCommand.ShortId(taskId);

        // The two revisions TaskDecider.Revise lets through past Draft (task 45136b29 for
        // queue-first; the interactive-mode gap independent pre-PR review, cycle 1, found in
        // h9k task start): a call that touched only a marker gets its own confirmation, since
        // "Draft X revised" and "Next: h9k task publish" are both wrong for a task that already
        // left Draft.
        bool markerFieldsOnly = (revised.QueuePriority.HasValue || revised.ClearInteractiveMode)
            && !revised.Objective.HasValue && !revised.AcceptanceCriteria.HasValue
            && !revised.AgentContext.HasValue && !revised.BlockedBy.HasValue && !revised.Type.HasValue
            && !revised.Model.HasValue && !revised.EpicId.HasValue;
        if (markerFieldsOnly && task.State != TaskState.Draft)
        {
            if (revised.QueuePriority.HasValue)
            {
                AnsiConsole.MarkupLine(revised.QueuePriority.Value
                    ? $"[blue]Task {shortId} marked queue-first[/] — it takes the next free dispatch slot regardless of assignment age."
                    : $"[blue]Task {shortId}'s queue-first marker cleared[/].");
            }

            if (revised.ClearInteractiveMode)
            {
                AnsiConsole.MarkupLine(
                    $"[blue]Task {shortId}'s interactive-mode flag cleared[/] — its run stops parking at phase boundaries from here on.");
            }

            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine($"[blue]Draft {shortId} revised[/]: {string.Join(", ", Changed(revised))}.");
        // After the confirmation, not before it: what changed here is the news, and the tracker
        // write is what followed from it.
        await RewriteRecordAsync(session, task, revised, context, cancellationToken);
        // The refusal path names the consequence (TaskDecider.Revise's own call into
        // ReviewStageCompositionValidation.VetInput); the accepted path has to name it too, or the
        // only operator who ever reads it is the one who tried the command without
        // --accept-reduced-review first (task: removing a load-bearing guarantee names the
        // decision it overrides at set time and requires the consequence to be acknowledged in
        // the command's own output; independent pre-PR review, cycle 1, both lenses).
        // ReviewStageCompositionAcknowledged is already clamped true only when a value that
        // genuinely needed it was actually accepted, so ReviewStageComposition.Value is guaranteed
        // non-null here.
        if (revised.ReviewStageCompositionAcknowledged
            && ReviewStageCompositionValidation.DescribeAcceptedConsequence(
                ReviewStageComposition.FromInput(revised.ReviewStageComposition.Value)) is { Length: > 0 } consequence)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]review-stage-composition consequence: {consequence}[/]");
        }

        AnsiConsole.MarkupLine($"[dim]Next:[/] h9k task publish {shortId}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Rewrite the task record in this task's linked GitHub issue, so the issue keeps describing the
    /// task it names (task: a published task's GitHub issue carries the whole task record). Only the
    /// record section moves; the prose above it is a human's to keep, and the acceptance-criteria
    /// checklist is regenerated only when this revision actually replaced the criteria.
    /// <para>
    /// Reported and swallowed like every other tracker write around a committed transaction: the
    /// revision landed either way, and the write is idempotent — the next revise, or a republish,
    /// writes the same record.
    /// </para>
    /// </summary>
    private static async Task RewriteRecordAsync(
        IQuerySession session,
        TaskAggregate task,
        TaskRevised revised,
        BootstrapContext context,
        CancellationToken cancellationToken)
    {
        if (task.ExternalReference is not { } reference || reference.Provider != WorkItemProvider.GitHub
            || !TouchesTheRecord(revised))
        {
            return;
        }

        try
        {
            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
            if (project is null)
            {
                return;
            }

            NodeDetails? node = await session.LoadAsync<NodeDetails>(context.NodeId, cancellationToken);
            TaskRecordPublication.WriteOutcome outcome = await TaskRecordPublication.WriteAsync(
                session, task, project, context.NodeId, node?.MachineName ?? Environment.MachineName,
                DateTimeOffset.UtcNow, revised.AcceptanceCriteria.HasValue,
                cancellationToken: cancellationToken);
            AnsiConsole.MarkupLine(DescribeRewrite(
                outcome, task.Origin is not null, revised.AcceptanceCriteria.HasValue, project.Name,
                reference.ToString()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]The revision landed, but rewriting the task record in "
                + $"{reference.ToString().EscapeMarkup()} failed: {exception.Message.EscapeMarkup()} "
                + "Nothing in the issue was changed; the next revise writes it.[/]");
        }
    }

    /// <summary>
    /// What to tell the operator about the record write — only ever the outcome
    /// <see cref="TaskRecordPublication.WriteAsync"/> actually answered, never a write it refused.
    /// <para>
    /// <see cref="TaskRecordPublication.WriteOutcome.NotTracked"/> has exactly two causes once this
    /// task is known to carry a GitHub reference, and both are permanent for this task rather than a
    /// transient miss: the task is a MIRROR, whose issue belongs to the install that published it,
    /// or the project does not track its backlog in GitHub issues. Each is named outright, because
    /// the alternative — the confirmation this used to print unconditionally — told the operator
    /// that a shared issue now reflects their revision when nothing was written and, in the mirror
    /// case, never will be (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// <para>
    /// <paramref name="criteriaChanged"/> is the same fact the writer is handed, and it is here for
    /// the same reason the causes above are named: this revision regenerated the checklist ABOVE
    /// the record whenever it replaced the criteria, so saying "everything above it is untouched"
    /// there would describe a write that did not happen — and it is the sentence a human editing
    /// that issue's prose reads to decide whether to go look (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </para>
    /// </summary>
    internal static string DescribeRewrite(
        TaskRecordPublication.WriteOutcome outcome,
        bool mirror,
        bool criteriaChanged,
        string projectName,
        string reference) =>
        (outcome, mirror) switch
        {
            (TaskRecordPublication.WriteOutcome.Written, _) when criteriaChanged =>
                $"[dim]  Task record rewritten in {reference.EscapeMarkup()}, and the acceptance-criteria "
                + "checklist above it regenerated from the new criteria; the rest of the issue — every line "
                + "that is neither the checklist nor the record — is untouched.[/]",
            (TaskRecordPublication.WriteOutcome.Written, _) =>
                $"[dim]  Task record rewritten in {reference.EscapeMarkup()}; everything above it in the "
                + "issue is untouched.[/]",
            (_, true) =>
                $"[dim]  Nothing was written to {reference.EscapeMarkup()}: this copy was adopted from that "
                + "issue's own task record, so the record there belongs to the install that published it — "
                + "rewriting it from here would overwrite the origin's record with this local copy. The "
                + "revision is yours and it landed; the issue keeps describing the origin's task.[/]",
            _ =>
                $"[dim]  Nothing was written to {reference.EscapeMarkup()}: "
                + $"{projectName.EscapeMarkup()} does not track its backlog in GitHub issues, so hall9k "
                + "leaves the linked issue's body alone — h9k project set "
                + $"{projectName.EscapeMarkup()} --backlog github-issues to have it maintain the record.[/]",
        };

    /// <summary>
    /// Whether this revision changed anything the task record carries. A queue-first marker, a
    /// cleared interactive-mode flag, a stacked edge, a review stage composition — none of those are
    /// in the record, and a revision touching only those buys a gh round trip that rewrites the
    /// issue with byte-identical text.
    /// </summary>
    private static bool TouchesTheRecord(TaskRevised revised) =>
        revised.Objective.HasValue || revised.AcceptanceCriteria.HasValue || revised.AgentContext.HasValue
        || revised.BlockedBy.HasValue || revised.Type.HasValue || revised.Model.HasValue
        || revised.EpicId.HasValue;

    /// <summary>What the revision actually touched, so the confirmation is a fact, not a shrug.</summary>
    internal static IEnumerable<string> Changed(TaskRevised revised)
    {
        if (revised.Objective.HasValue)
        {
            yield return "objective";
        }

        if (revised.AcceptanceCriteria.HasValue)
        {
            yield return $"{revised.AcceptanceCriteria.Value?.Count ?? 0} acceptance criteria";
        }

        if (revised.AgentContext.HasValue)
        {
            yield return "agent context";
        }

        if (revised.BlockedBy.HasValue)
        {
            yield return revised.BlockedBy.Value is { Count: > 0 } dependencies
                ? $"{dependencies.Count} dependency(ies)"
                : "dependencies cleared";
        }

        if (revised.StackedOnTaskId.HasValue)
        {
            yield return revised.StackedOnTaskId.Value is { } parentId
                ? $"stacked on {TaskListCommand.ShortId(parentId)}"
                : "stacked edge cleared";
        }

        if (revised.StackedOnPullRequestNumber.HasValue)
        {
            yield return revised.StackedOnPullRequestNumber.Value is { } parentNumber
                ? $"stacked on pull request #{parentNumber}"
                : "stacked edge cleared";
        }

        if (revised.Type.HasValue)
        {
            yield return $"type {revised.Type.Value?.Value}";
        }

        if (revised.Model.HasValue)
        {
            yield return revised.Model.Value == AgentModel.Unknown
                ? "model override cleared"
                : $"model {revised.Model.Value?.Value.EscapeMarkup()}";
        }

        if (revised.EpicId.HasValue)
        {
            yield return revised.EpicId.Value is { } epicId
                ? $"epic {TaskListCommand.ShortId(epicId)}"
                : "epic cleared";
        }

        if (revised.QueuePriority.HasValue)
        {
            yield return revised.QueuePriority.Value ? "marked queue-first" : "queue-first marker cleared";
        }

        if (revised.ReviewStageComposition.HasValue)
        {
            yield return revised.ReviewStageComposition.Value is { } composition
                ? $"review stage composition {composition}"
                : "review stage composition override cleared";
        }

        if (revised.ClearInteractiveMode)
        {
            yield return "interactive-mode flag cleared";
        }

        if (revised.CloseLinkedIssue.HasValue)
        {
            yield return revised.CloseLinkedIssue.Value is { } closeLinkedIssue
                ? $"close linked issue {closeLinkedIssue.CliSpelling.EscapeMarkup()}"
                : "close linked issue override cleared";
        }
    }

    /// <summary>
    /// True when <paramref name="epic"/> is exactly the full id of <paramref name="currentEpicId"/>,
    /// or exactly its rendered short form (<see cref="DomainId.Short"/>). The renderer always
    /// writes a member task's current epic into task.md using that exact short form, so a --file
    /// revision that changes nothing else round-trips that same value back in; re-running
    /// <see cref="EpicIdResolver.ResolveForMembershipAsync"/> on it would re-gate a no-op edit on
    /// the epic still being Open, refusing an unrelated edit to a task whose epic has since
    /// closed. Anything less than an exact match falls through to the resolver instead of
    /// guessing: a shorter fragment may equally (or unambiguously) name a *different* epic, and
    /// only the resolver, which sees every epic, can tell (adversarial review, cycle 1).
    /// </summary>
    internal static bool NamesCurrentEpic(string epic, Guid? currentEpicId)
    {
        if (currentEpicId is not { } id)
        {
            return false;
        }

        if (Guid.TryParse(epic, out Guid parsed))
        {
            return parsed == id;
        }

        return string.Equals(epic, DomainId.Short(id), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Guid[]> ResolveAsync(
        IQuerySession session, IReadOnlyList<string> blockedBy, CancellationToken cancellationToken)
    {
        List<Guid> dependencies = [];
        foreach (string reference in blockedBy.Where(value => value.IsNotBlank()))
        {
            dependencies.Add(await TaskIdResolver.ResolveAsync(session, reference, cancellationToken));
        }

        return [.. dependencies];
    }
}
