using System.ComponentModel;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The hinge between the two phases (Decisions Log #35): discovery asked "what is this?" and
/// answered it, so the idea becomes a draft task, where refinement asks "how does this become
/// executable?". Promotion composes with the existing lifecycle rather than duplicating it —
/// what it produces is an ordinary draft, entering the ordinary draft ceremony.
/// </summary>
public sealed class IdeaPromoteCommand : Hall9kAsyncCommand<IdeaPromoteCommand.Settings>
{
    /// <summary>An objective past this length is quoted back with a nudge rather than refused.</summary>
    private const int LongObjective = 120;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Idea id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project the draft belongs to: its name, an unambiguous fragment of it, or its id. "
            + "Required unless the idea is already assigned to one — a task belongs to a project, so "
            + "this is the one thing promotion cannot leave open")]
        public string? Project { get; init; }

        [CommandOption("--objective <OBJECTIVE>")]
        [Description(
            "The draft's objective, in your words. Without it the idea's first sentence is taken "
            + "mechanically — never interpreted — and the whole note still rides along as agent context")]
        public string? Objective { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        // Fence before aggregating: promotion is a one-time transition that mints a second
        // stream, so a promote racing another promote (or a revise, or an assign) must not
        // land on an idea that has already moved. The task stream is only started if this
        // append wins, so the two halves of the provenance trail cannot come apart.
        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");

        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : idea.ProjectId is { } assigned
                ? await session.LoadAsync<ProjectDetails>(assigned, cancellationToken)
                    ?? throw new DomainNotFoundException(
                        $"Idea {idea.Id} is assigned to project {assigned}, which is not registered here. "
                        + $"Name the project to promote into: h9k idea promote {settings.Id} --project <name>")
                : null;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IdeaSeed seed = Seed(idea.Text, settings.Objective);
        Guid taskId = DomainId.New();

        // The idea decides first: its refusals (already promoted, discarded, no project) teach
        // better than the task decider's would, and nothing is appended when one fires.
        IdeaPromoted promoted = IdeaDecider.Promote(
            idea, taskId, project?.Id, seed.Objective, DateTimeOffset.UtcNow, context.OwnerId);

        TaskAdded added = TaskDecider.Add(
            taskId,
            promoted.ProjectId,
            promoted.Objective,
            acceptanceCriteria: [],
            TaskType.Parse(null),
            AgentContext(idea.Id, seed.Context),
            constraints: null,
            externalReference: null,
            promoted.PromotedAt,
            context.OwnerId,
            model: null,
            blockedBy: null,
            sourceIdeaId: idea.Id);

        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(idea.Id, expectedVersion: fence.Version + 1, promoted);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} changed while promoting — read it back with h9k idea show {settings.Id}, "
                + "and re-run this command only if it is still in discovery.");
        }

        // No doorbell: what promotion produces is a draft, and a draft is invisible to the
        // dispatcher until a human publishes and assigns it (Decisions Log #34).
        Announce(idea, promoted, project, seed, settings.Objective.IsNotBlank());
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What the draft opens with. An explicit --objective consumes nothing, so the whole note
    /// becomes context; otherwise the first sentence is taken mechanically and the remainder
    /// becomes context (Decisions Log #35).
    /// </summary>
    internal static IdeaSeed Seed(string text, string? objective) =>
        objective.IsNotBlank()
            ? new IdeaSeed(objective.Trim(), text.Trim() is { Length: > 0 } note ? note : null)
            : IdeaText.Seed(text);

    /// <summary>
    /// The agent-facing context the draft carries: what the note said, and the pointer to the
    /// discovery workspace so the research that produced this task is reachable from it. The
    /// pointer is a path, not the files — the bytes stay on disk (Decisions Log #35).
    /// </summary>
    internal static string AgentContext(Guid ideaId, string? context)
    {
        StringBuilder agentContext = new();
        if (context.IsNotBlank())
        {
            agentContext.AppendLine(context).AppendLine();
        }

        agentContext.AppendLine(
            "Discovery workspace (research notes, gathered files, and prototypes from before this "
            + "was a task; may be empty):");
        agentContext.Append(IdeaPaths.WorkspaceDirectory(ideaId));
        return agentContext.ToString();
    }

    /// <summary>
    /// Why an objective came out long, in the words of what the split actually did — a note that
    /// never broke into a second sentence, or one whose first sentence simply runs long. Null when
    /// there is nothing to say: the mechanical split is described, never diagnosed.
    /// </summary>
    internal static string? SharpenNudge(string objective, string? context, bool objectiveGiven) =>
        objectiveGiven || objective.Length <= LongObjective
            ? null
            : context.IsNotBlank()
                ? "The note's first sentence runs long, so the objective it seeded does too."
                : "The note is a single sentence, so the whole of it became the objective.";

    /// <summary>
    /// Says exactly what was taken and from where — the split is mechanical, so it is shown
    /// rather than trusted — then hands off to the draft ceremony refinement happens in.
    /// </summary>
    private static void Announce(
        IdeaAggregate idea, IdeaPromoted promoted, ProjectDetails? project, IdeaSeed seed, bool objectiveGiven)
    {
        string ideaShortId = TaskListCommand.ShortId(idea.Id);
        string taskShortId = TaskListCommand.ShortId(promoted.TaskId);
        string projectName = project?.Name ?? promoted.ProjectId.ToString();

        AnsiConsole.MarkupLine(
            $"[green]Idea {ideaShortId} promoted[/] into draft [dim]{taskShortId}[/] in '{projectName.EscapeMarkup()}'");
        AnsiConsole.MarkupLine(
            $"[dim]  objective ({(objectiveGiven ? "yours" : "the note's first sentence, taken as written")}):[/] "
            + ExternalText.OneLineMarkup(promoted.Objective));
        AnsiConsole.MarkupLine(seed.Context.IsNotBlank()
            ? "[dim]  context:[/] the rest of the note, plus the discovery workspace path"
            : "[dim]  context:[/] the discovery workspace path");
        AnsiConsole.MarkupLine($"[dim]  workspace:[/] {IdeaPaths.WorkspaceDirectory(idea.Id).EscapeMarkup()}");

        if (SharpenNudge(promoted.Objective, seed.Context, objectiveGiven) is { } nudge)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  {nudge.EscapeMarkup()}[/] "
                + $"[dim]Sharpen it:[/] h9k task revise {taskShortId} --objective \"…\"");
        }

        AnsiConsole.MarkupLine(
            "\n[dim]Discovery is over; refinement starts. A draft dispatches nothing until you walk it out:[/]");
        AnsiConsole.MarkupLine(
            $"  h9k task revise {taskShortId} --criteria \"…\"   [dim]— publishing needs at least one[/]");
        AnsiConsole.MarkupLine($"  h9k task publish {taskShortId}              [dim]— the readiness gate[/]");
        AnsiConsole.MarkupLine($"  h9k task assign {taskShortId}               [dim]— the go signal[/]");
    }
}
