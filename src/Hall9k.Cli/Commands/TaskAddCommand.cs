using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskAddCommand : Hall9kAsyncCommand<TaskAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Project the task belongs to: its name, an unambiguous fragment of it, or its full id "
            + "(h9k project list shows them all). A fragment matching more than one project is "
            + "rejected as ambiguous rather than guessed at.")]
        public string? Project { get; init; }

        [CommandOption("--objective <OBJECTIVE>")]
        [Description(
            "One sentence, outcome-phrased — what the draft is about. Together with --project it is "
            + "everything creation requires: creation is identity, not readiness (Decisions Log #34). "
            + "The readiness contract is enforced later, once, by h9k task publish")]
        public string? Objective { get; init; }

        [CommandOption("--criteria <CRITERION>")]
        [Description(
            "Checkable acceptance criterion; repeat the option for more. Optional here and required "
            + "by h9k task publish — a draft exists in order to gather them")]
        public string[] Criteria { get; init; } = [];

        [CommandOption("--blocked-by <TASK>")]
        [Description(
            "A task this one waits on: its id or an unambiguous fragment; repeat the option for more. "
            + "A dependency counts as met only at true closeout (the pull request merged and the "
            + "closeout monitor observed it), so a Done-but-unmerged dependency still blocks. "
            + "Revise the set later with h9k task revise --blocked-by")]
        public string[] BlockedBy { get; init; } = [];

        [CommandOption("--type <TYPE>")]
        [Description("feature | bugfix | refactor | chore | research")]
        public string? Type { get; init; }

        [CommandOption("--context <CONTEXT>")]
        [Description("Agent-facing context (pointers, constraints, boundaries)")]
        public string? AgentContext { get; init; }

        [CommandOption("--file <PATH>")]
        [Description(
            "Task file: frontmatter (project/type/objective/criteria/model/blocked-by) + markdown body "
            + "as agent context")]
        public string? File { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description(
            "Model this task's sessions run on, overriding every other level of the chain "
            + "(Decisions Log #33): a tier alias (fable, opus, sonnet, haiku) or an exact model id "
            + "(claude-opus-5, claude-sonnet-5, or a context variant like claude-opus-5[[1m]]); anything "
            + "'claude -p --model' accepts, except the word 'default'. "
            + "Omit it — or pass 'default', which states no override rather than naming a model — and "
            + "the node's per-role default, then the project default, then the platform default decide. "
            + "Reach for it when THIS task is unusual, not to express a standing preference")]
        public string? Model { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string? project = settings.Project;
        string? objective = settings.Objective;
        string? type = settings.Type;
        string? agentContext = settings.AgentContext;
        string? model = settings.Model;
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
            project ??= file.Project;
            objective ??= file.Objective;
            type ??= file.Type;
            agentContext ??= file.AgentContext;
            model ??= file.Model;
            criteria = criteria.Count > 0 ? criteria : file.Criteria;
            blockedBy = blockedBy.Count > 0 ? blockedBy : file.BlockedBy;
        }

        if (project.IsBlank())
        {
            throw new DomainValidationException("A task needs a project (--project or 'project:' in the file).");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails projectDetails = await ProjectResolver.ResolveAsync(session, project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        Guid[] dependencies = await ResolveDependenciesAsync(session, blockedBy, cancellationToken);

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId,
            projectDetails.Id,
            objective ?? string.Empty,
            criteria,
            TaskType.Parse(type),
            agentContext,
            constraints: null,
            externalReference: null,
            DateTimeOffset.UtcNow,
            context.OwnerId,
            AgentModel.FromInput(model),
            dependencies);
        session.Events.StartStream<TaskAggregate>(taskId, added);

        await session.SaveChangesAsync(cancellationToken);

        // No doorbell: a draft is invisible to the dispatcher by design, so there is nothing
        // for a daemon to wake up for until a human publishes and assigns it (log #34).
        string modelNote = added.Model is { } chosen && chosen != AgentModel.Unknown
            ? $" [dim]on {chosen.Value.EscapeMarkup()}[/]"
            : string.Empty;
        AnsiConsole.MarkupLine(
            $"[blue]Draft created[/] in '{projectDetails.Name.EscapeMarkup()}': " +
            $"{added.Objective.EscapeMarkup()}{modelNote} [dim]({taskId})[/]");
        if (dependencies.Length > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  blocked by {dependencies.Length} task(s): " +
                $"{string.Join(", ", dependencies.Select(TaskListCommand.ShortId))}[/]");
        }

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(added.AcceptanceCriteria.Count == 0
            ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]then[/] h9k task publish {shortId}"
            : $"[dim]Next:[/] h9k task publish {shortId} [dim](a draft never dispatches; publishing then assigning is what starts it)[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Dependency ids as typed: full ids or unambiguous fragments, resolved now so a typo is
    /// refused at creation rather than becoming an edge that names nothing.
    /// </summary>
    private static async Task<Guid[]> ResolveDependenciesAsync(
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
