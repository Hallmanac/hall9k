using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
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
        [Description("One sentence, outcome-phrased (readiness contract, PLAN.md §4)")]
        public string? Objective { get; init; }

        [CommandOption("--criteria <CRITERION>")]
        [Description("Checkable acceptance criterion; repeat the option for more")]
        public string[] Criteria { get; init; } = [];

        [CommandOption("--type <TYPE>")]
        [Description("feature | bugfix | refactor | chore | research")]
        public string? Type { get; init; }

        [CommandOption("--context <CONTEXT>")]
        [Description("Agent-facing context (pointers, constraints, boundaries)")]
        public string? AgentContext { get; init; }

        [CommandOption("--file <PATH>")]
        [Description("Task file: frontmatter (project/type/objective/criteria) + markdown body as agent context")]
        public string? File { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string? project = settings.Project;
        string? objective = settings.Objective;
        string? type = settings.Type;
        string? agentContext = settings.AgentContext;
        IReadOnlyList<string> criteria = settings.Criteria;

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
            criteria = criteria.Count > 0 ? criteria : file.Criteria;
        }

        if (project.IsBlank())
        {
            throw new DomainValidationException("A task needs a project (--project or 'project:' in the file).");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails projectDetails = await ProjectResolver.ResolveAsync(session, project, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId,
            projectDetails.Id,
            objective ?? string.Empty,
            criteria,
            TaskTypeFrom(type),
            agentContext,
            constraints: null,
            externalReference: null,
            DateTimeOffset.UtcNow,
            context.OwnerId);
        session.Events.StartStream<TaskAggregate>(taskId, added);

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-added:{taskId}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Task queued[/] in '{projectDetails.Name.EscapeMarkup()}': " +
            $"{added.Objective.EscapeMarkup()} [dim]({taskId})[/]");
        return ExitCodes.Ok;
    }

    private static TaskType TaskTypeFrom(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" or "feature" => TaskType.Feature,
        "bugfix" or "bug" => TaskType.Bugfix,
        "refactor" => TaskType.Refactor,
        "chore" => TaskType.Chore,
        "research" => TaskType.Research,
        _ => throw new DomainValidationException(
            $"Unknown task type '{type}'. Use feature, bugfix, refactor, chore, or research."),
    };
}
