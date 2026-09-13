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
/// Sugar over the ordinary fan-out door (backlog 31): cuts exactly one task from the idea, with
/// the note's first sentence taken mechanically as the objective unless --objective overrides
/// it, then concludes the idea in the same breath — for the common case where a single idea
/// deserved a single task and nothing more is coming. Reconciles the shipped 1:1 promote: it
/// used to be its own ceremony with its own event and its own "promotes once" rule; now it
/// composes the same two acts every other path to fan-out and conclusion uses
/// (<c>h9k task add --from-idea</c> and <c>h9k idea conclude</c>), so there are never two doors
/// with diverging semantics. When discovery is going to keep producing, cut tasks one at a time
/// with <c>h9k task add --from-idea</c> instead and conclude by hand once it stops.
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

        // Fence before aggregating: this appends two events on the idea's own stream (the cut,
        // then the conclusion) plus a new task stream, so a promote racing another promote (or a
        // revise, or an assign) must not land on an idea that has already moved. The task stream
        // is only started if the fenced appends win, so the two halves of the provenance trail
        // cannot come apart.
        Guid ideaId = await IdeaIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");

        // Checked here, before project resolution below, so an idea already concluded or archived
        // earns its own refusal rather than "promotion needs a project" when it happens to have
        // none (independent pre-PR review, conformance lens).
        IdeaDecider.RequireCaptured(idea, "promote");

        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : idea.ProjectId is { } assigned
                ? await session.LoadAsync<ProjectDetails>(assigned, cancellationToken)
                    ?? throw new DomainNotFoundException(
                        $"Idea {idea.Id} is assigned to project {assigned}, which is not registered here. "
                        + $"Name the project to promote into: h9k idea promote {settings.Id} --project <name>")
                : null;
        if (project?.PurgeAt is { } purgeDeadline)
        {
            // Same reasoning as h9k task add's own refusal: the sweep re-queries ownership at
            // fire time, so a task minted here would be destroyed along with everything else at
            // the deadline rather than orphaned — refused here so a promotion does not silently
            // hand a fresh task (and this idea, once concluded, binds to it) to a project already
            // scheduled for destruction (independent pre-PR review, cycle 1, both lenses, medium).
            throw new DomainValidationException(
                $"Project '{project.Name}' is scheduled for permanent deletion at "
                + $"{purgeDeadline.ToLocalTime():g}, so it cannot take a new task. Cancel the purge "
                + $"first: h9k project cancel-purge {project.Name}");
        }

        // A task belongs to a project, and this door still needs one up front — CutTask itself
        // does not ask, since an ordinary --from-idea cut always has one from --project. Kept as
        // its own check, worded the way promotion always worded it, rather than folded into
        // TaskDecider.Add's generic "a task belongs to a project" (independent pre-PR review).
        Guid destinationProjectId = project?.Id ?? idea.ProjectId ?? Guid.Empty;
        if (destinationProjectId == Guid.Empty)
        {
            throw new DomainValidationException(
                "Promotion needs a project, because a task belongs to one: h9k idea promote "
                + $"{idea.Id} --project <name>. If this idea IS a new project, register it first "
                + "(h9k project add --name <name> --repo <path>) and then promote into it — the platform "
                + "will not invent a repository for you (Decisions Log #35).");
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IdeaSeed seed = Seed(idea.Text, settings.Objective);
        Guid taskId = DomainId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // The idea decides first: its refusals (already concluded, archived) teach better than
        // the task decider's would, and nothing is appended when one fires. The binding to
        // destinationProjectId travels in the same batch as the cut and the conclusion — the
        // retired IdeaPromoted event used to set ProjectId unconditionally on Apply, and a
        // concluded idea is terminal, so an idea left unassigned here could never be assigned
        // afterward (independent pre-PR review).
        IdeaAssignedToProject? binding = destinationProjectId != idea.ProjectId
            ? IdeaDecider.AssignToProject(idea, destinationProjectId, now, context.OwnerId)
            : null;
        IdeaTaskCut cut = IdeaDecider.CutTask(idea, taskId, seed.Objective, now, context.OwnerId);
        IdeaConcluded concluded = IdeaDecider.Conclude(
            idea, $"Promoted into a single task: {seed.Objective}", now, context.OwnerId);

        TaskAdded added = TaskDecider.Add(
            taskId,
            destinationProjectId,
            cut.Objective,
            acceptanceCriteria: [],
            TaskType.Parse(null),
            AgentContext(idea, seed.Context),
            constraints: null,
            externalReference: null,
            now,
            context.OwnerId,
            model: null,
            blockedBy: null,
            sourceIdeaId: idea.Id);

        object[] ideaEvents = binding is null ? [cut, concluded] : [binding, cut, concluded];
        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(idea.Id, expectedVersion: fence.Version + ideaEvents.Length, ideaEvents);
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
        Announce(idea, taskId, destinationProjectId, project, seed, settings.Objective.IsNotBlank());
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
    internal static string AgentContext(IdeaAggregate idea, string? context)
    {
        StringBuilder agentContext = new();
        if (context.IsNotBlank())
        {
            agentContext.AppendLine(context).AppendLine();
        }

        string ideaDirectory = IdeaPaths.ResolveDirectory(
            idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id);
        agentContext.AppendLine(
            "Discovery workspace (research notes, gathered files, and prototypes from before this "
            + "was a task; may be empty):");
        agentContext.Append(IdeaPaths.WorkspaceDirectory(ideaDirectory));
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
        IdeaAggregate idea, Guid taskId, Guid projectId, ProjectDetails? project, IdeaSeed seed, bool objectiveGiven)
    {
        string ideaShortId = TaskListCommand.ShortId(idea.Id);
        string taskShortId = TaskListCommand.ShortId(taskId);
        string projectName = project?.Name ?? projectId.ToString();

        AnsiConsole.MarkupLine(
            $"[green]Idea {ideaShortId} promoted[/] into draft [dim]{taskShortId}[/] in '{projectName.EscapeMarkup()}', "
            + "and concluded");
        AnsiConsole.MarkupLine(
            $"[dim]  objective ({(objectiveGiven ? "yours" : "the note's first sentence, taken as written")}):[/] "
            + ExternalText.OneLineMarkup(seed.Objective));
        AnsiConsole.MarkupLine(seed.Context.IsNotBlank()
            ? "[dim]  context:[/] the rest of the note, plus the discovery workspace path"
            : "[dim]  context:[/] the discovery workspace path");
        string ideaDirectory = IdeaPaths.ResolveDirectory(
            idea.WorkspaceHome, ProjectHomePaths.EntryDirectoryName(idea.Id, idea.Text), idea.Id);
        AnsiConsole.MarkupLine(
            $"[dim]  workspace:[/] {IdeaPaths.WorkspaceDirectory(ideaDirectory).EscapeMarkup()}");

        if (SharpenNudge(seed.Objective, seed.Context, objectiveGiven) is { } nudge)
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
