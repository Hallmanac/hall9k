using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
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
            + "adversarial-only, or conformance-only; refused as meaningless otherwise")]
        public bool AcceptReducedReview { get; init; }
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
            && !queuePriority.HasValue && settings.ReviewStageComposition is null)
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
            settings.AcceptReducedReview);

        session.Events.Append(taskId, revised);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);

        // The one revision TaskDecider.Revise lets through past Draft (task 45136b29): a call
        // that touched only the marker gets its own confirmation, since "Draft X revised" and
        // "Next: h9k task publish" are both wrong for a task that already left Draft.
        bool queuePriorityOnly = revised.QueuePriority.HasValue && !revised.Objective.HasValue
            && !revised.AcceptanceCriteria.HasValue && !revised.AgentContext.HasValue
            && !revised.BlockedBy.HasValue && !revised.Type.HasValue && !revised.Model.HasValue
            && !revised.EpicId.HasValue;
        if (queuePriorityOnly && task.State != TaskState.Draft)
        {
            AnsiConsole.MarkupLine(revised.QueuePriority.Value
                ? $"[blue]Task {shortId} marked queue-first[/] — it takes the next free dispatch slot regardless of assignment age."
                : $"[blue]Task {shortId}'s queue-first marker cleared[/].");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine($"[blue]Draft {shortId} revised[/]: {string.Join(", ", Changed(revised))}.");
        AnsiConsole.MarkupLine($"[dim]Next:[/] h9k task publish {shortId}");
        return ExitCodes.Ok;
    }

    /// <summary>What the revision actually touched, so the confirmation is a fact, not a shrug.</summary>
    private static IEnumerable<string> Changed(TaskRevised revised)
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

        if (revised.Type.HasValue)
        {
            yield return $"type {revised.Type.Value?.Value}";
        }

        if (revised.Model.HasValue)
        {
            yield return revised.Model.Value == AgentModel.Unknown
                ? "model override cleared"
                : $"model {revised.Model.Value?.Value}";
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
