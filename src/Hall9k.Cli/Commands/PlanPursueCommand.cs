using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Answer discovery's gate by pursuing the idea: read the report from the workspace now, so a person's edit to it counts, and in
/// one save cut one draft per <c>TASK:</c> line and move the plan to refinement. It cuts in process, as
/// <see cref="IdeaPromoteCommand"/> does, and needs no daemon child. Each draft is what
/// <c>h9k task add --from-idea &lt;idea&gt; --objective "&lt;objective&gt;"</c> would create on the plan's project, with a context
/// that also names the idea. The plan stream's version is the fence that makes two pursues at once cut one set of drafts.
/// </summary>
public sealed class PlanPursueCommand : Hall9kAsyncCommand<PlanPursueCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLAN>")]
        [Description("Plan id (full, or an unambiguous fragment). It must be waiting at discovery's gate (h9k status lists it under Needs you)")]
        public string Plan { get; init; } = string.Empty;

        [CommandOption("--epic-title <TITLE>")]
        [Description(
            "Create one epic with this title and put every cut draft in it. Refused unless the report proposes two or more tasks. "
            + "In a project that belongs to an open solution it is a solution epic, as h9k epic add makes: one copy in each member "
            + "project this node holds, sharing one key, with the drafts in this project's copy. Elsewhere it is a plain project epic")]
        public string? EpicTitle { get; init; }

        [CommandOption("--no-existing-item")]
        [Description(
            "The dedup attestation h9k task publish takes, given once for the plan, as on h9k plan start: you searched the "
            + "project's tracker and found no open item covering these drafts. Required on a project whose backlog policy is jira, "
            + "github-issues, or linear unless --untracked is given. Recorded on the plan, and what its publish passes on")]
        public bool NoExistingItem { get; init; }

        [CommandOption("--untracked")]
        [Description(
            "The sibling attestation: deliberately skip external tracking for these drafts. Means what it means on h9k task "
            + "publish, so it is refused alongside --no-existing-item and on a project whose backlog policy tracks nothing")]
        public bool Untracked { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using DocumentStore store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        PlanDetails found = await PlanIdResolver.ResolveAsync(session, settings.Plan, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(found.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No plan {found.Id}.");
        PlanAggregate plan = await session.Events.AggregateStreamAsync<PlanAggregate>(found.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No plan {found.Id}.");

        // Asked first: a plan that is not at the gate has no report to read, and the refusal says where it is.
        PlanDecider.RequireAtGate(plan, "pursued");
        Guid ideaId = plan.IdeaId ?? throw new DomainConflictException($"Plan {plan.Id} was not started from an idea.");
        IdeaAggregate idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Plan {plan.Id} names project {plan.ProjectId}, which is not registered here.");
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        RefuseIdea(idea, plan, project);
        string shortPlan = TaskListCommand.ShortId(plan.Id);
        string reportPath = DiscoveryWorkspace.ReportPathOf(idea, plan.Id)
            ?? throw new DomainBusinessRuleException(
                $"Idea {TaskListCommand.ShortId(idea.Id)}'s workspace is recorded on another node's operating system, so this node has no "
                + "report to read for it.");
        DiscoveryReadout readout = await ReadReportAsync(reportPath, shortPlan, cancellationToken);
        if (readout.Decision != DiscoveryDecision.Pursue)
        {
            throw new DomainBusinessRuleException(
                $"The report at {reportPath} ends with {DiscoveryReport.DecisionMarker} stop, so there is nothing to cut. Stop the plan "
                + $"with h9k plan stop {shortPlan} --reason \"<why>\", or, if you disagree, edit the report's closing block and pursue again.");
        }

        if (settings.EpicTitle.IsNotBlank() && readout.Objectives.Count < 2)
        {
            throw new DomainValidationException(
                "--epic-title groups two or more drafts under one epic, and the report proposes "
                + $"{readout.Objectives.Count}. Leave it out, or edit the report's closing block to propose the tasks the epic is for.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Each draft is what `h9k task add --from-idea <idea> --objective "<objective>"` creates: the whole note as context, the
        // discovery workspace beneath it, the idea's provenance, and no criteria (refinement writes them). The context names the
        // idea and the report's file name, which sits in the workspace directory the context already names. Only the file name is
        // written, never a path: a draft's context replicates to every node, and the name is the same on all of them while the
        // directory is this node's. The refinement prompt tells the session the full path from the plan's own record.
        string context1 = IdeaPromoteCommand.AgentContext(idea, idea.Text.Trim()).TrimEnd();
        string draftContext = $"{context1}\n\nThis task was proposed by a discovery of idea {TaskListCommand.ShortId(idea.Id)}, "
            + $"whose report is {DiscoveryWorkspace.ReportFileName(plan.Id)} in the workspace above.";
        List<(TaskAdded Added, IdeaTaskCut Cut)> drafts = [];
        bool recordsNoExistingItem = false;
        bool recordsUntracked = false;
        string retry = $"h9k plan pursue {shortPlan}";
        foreach (string objective in readout.Objectives)
        {
            Guid taskId = DomainId.New();
            IdeaTaskCut cut = IdeaDecider.CutTask(idea, taskId, objective, now, context.OwnerId);
            TaskAdded added = TaskDecider.Add(
                taskId, project.Id, cut.Objective, acceptanceCriteria: [], TaskType.Parse(null), draftContext, constraints: null,
                externalReference: null, now, context.OwnerId, model: null, blockedBy: null, sourceIdeaId: idea.Id, epicId: null);

            // The attestation, asked of each draft the way its publish would ask it, so a combination the publish would refuse is
            // refused here, before anything is cut.
            TaskAggregate asCut = new();
            asCut.Apply(added);
            (bool noExistingRecorded, bool untrackedRecorded) = TaskDecider.VetTrackerAttestation(
                asCut, project.BacklogPolicy, settings.NoExistingItem, settings.Untracked, "Pursue the plan", retry);
            recordsNoExistingItem |= noExistingRecorded;
            recordsUntracked |= untrackedRecorded;
            drafts.Add((added, cut));
        }

        // The things that can move while the report was read and the drafts built are read fresh, immediately before the save. Only
        // the plan and idea streams are fenced: the idea is read at a stream version, and the cuts are appended expecting that
        // version plus themselves, so a conclude, archive, or move that commits after this read rolls the whole save back instead of
        // being cut over. The project and solution records are read fresh but not fenced, so a purge or a solution close that lands
        // between that read and the save is not caught (h9k epic add has the same window).
        StreamState ideaFence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        IdeaAggregate freshIdea = await session.Events.AggregateStreamAsync<IdeaAggregate>(
                ideaId, version: ideaFence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No idea {ideaId}.");
        ProjectDetails freshProject = await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Plan {plan.Id} names project {plan.ProjectId}, which is not registered here.");
        RefuseIdea(freshIdea, plan, freshProject);

        // The epic is planned against the project and solution records as just read, and only now: a solution epic is one copy per
        // held member, which the closed-solution refusal and the purge check read fresh (not fenced, as noted above), and a refusal
        // here has staged nothing. The drafts join this project's copy, so the epic id reaches them after they were built and vetted.
        IReadOnlyList<EpicAdded> epicCopies = [];
        SolutionEpicAddition? addition = null;
        EpicAdded? epic = null;
        if (settings.EpicTitle.IsNotBlank())
        {
            addition = await SolutionEpicAddition.PlanAsync(session, freshProject, settings.EpicTitle, context.OwnerId, cancellationToken);
            epicCopies = addition?.Copies
                ?? [EpicDecider.Add(DomainId.New(), freshProject.Id, settings.EpicTitle, now, context.OwnerId)];
            epic = epicCopies.Single(copy => copy.ProjectId == freshProject.Id);
            drafts = [.. drafts.Select(draft => (draft.Added with { EpicId = epic.Id }, draft.Cut))];
        }

        PlanDraftsCut pursued = PlanDecider.Pursue(
            plan, [.. drafts.Select(draft => draft.Added.Id)], epic?.Id, recordsNoExistingItem, recordsUntracked, context.OwnerId, now);
        foreach (EpicAdded copy in epicCopies)
        {
            session.Events.StartStream<EpicAggregate>(copy.Id, copy);
        }

        foreach ((TaskAdded added, IdeaTaskCut _) in drafts)
        {
            session.Events.StartStream<TaskAggregate>(added.Id, added);
        }

        session.Events.Append(idea.Id, expectedVersion: ideaFence.Version + drafts.Count, [.. drafts.Select(draft => draft.Cut)]);

        session.Events.Append(plan.Id, expectedVersion: fence.Version + 1, pursued);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // One save fences two streams, and the exception does not say which moved, so the plan is looked at again: if it has
            // not moved, the idea did.
            StreamState? planNow = await session.Events.FetchStreamStateAsync(plan.Id, cancellationToken);
            throw planNow is not null && planNow.Version == fence.Version
                ? new DomainConflictException(
                    $"Idea {TaskListCommand.ShortId(idea.Id)} changed while this was being recorded, so nothing was cut. Read it back with "
                    + $"h9k idea show {TaskListCommand.ShortId(idea.Id)}, and pursue again only if it is still Captured in the plan's project.")
                : new DomainConflictException(
                    $"Plan {plan.Id} changed while this was being recorded, so nothing was cut. Check h9k plan show {shortPlan}; if it has "
                    + "drafts, another pursue got there first.");
        }

        await Doorbell.RingAsync($"plan-pursued:{plan.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[blue]Plan {shortPlan} pursued[/]: {drafts.Count} {(drafts.Count == 1 ? "draft" : "drafts")} cut from idea {TaskListCommand.ShortId(idea.Id)} "
            + $"in project {project.Name.EscapeMarkup()}, now in refinement");
        foreach ((TaskAdded added, IdeaTaskCut _) in drafts)
        {
            AnsiConsole.MarkupLine($"  {TaskListCommand.ShortId(added.Id)}  {ExternalText.OneLineMarkup(added.Objective)}");
        }

        if (addition is not null && epic is not null)
        {
            PrintSolutionEpic(addition, epic);
        }
        else if (epic is not null)
        {
            AnsiConsole.MarkupLine($"[dim]  in epic {TaskListCommand.ShortId(epic.Id)}: {ExternalText.OneLineMarkup(epic.Title)}[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]  The plan's drafts are fixed from here. The daemon refines each, has it challenged, and publishes them when every one "
            + "is ready; the idea stays Captured, and concluding it stays yours. Read where it stands with:[/] "
            + $"h9k plan show {shortPlan}");
        if (DaemonProcess.Probe() is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]  The daemon is not running.[/] [dim]A plan waits for it: h9k daemon start[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>What <c>h9k epic add</c> prints for a solution epic: the reference the drafts joined by, the copies, and any member left without one.</summary>
    private static void PrintSolutionEpic(SolutionEpicAddition addition, EpicAdded own)
    {
        string key = own.SolutionEpicKey ?? string.Empty;
        string reference = EpicIdResolver.ReferenceOf(own.Id, key);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]  in solution epic {reference}: {ExternalText.OneLine(own.Title)} (this project's copy)[/]");
        addition.PrintMemberNotes("  ");
    }

    /// <summary>The refusals that depend on the idea and its project, asked twice: once up front, and again fresh just before the save.</summary>
    private static void RefuseIdea(IdeaAggregate idea, PlanAggregate plan, ProjectDetails project)
    {
        string id = TaskListCommand.ShortId(idea.Id);
        IdeaDecider.RequireCaptured(idea, "cut tasks from");
        if (idea.ProjectId != plan.ProjectId)
        {
            throw new DomainConflictException(
                $"Idea {id} is no longer in the plan's project: it was started for project {plan.ProjectId} and the idea now belongs "
                + $"to {idea.ProjectId?.ToString() ?? "none"}. Tasks are cut into the plan's project only; move the idea back "
                + $"(h9k idea move {id} <project>) or stop the plan.");
        }

        if (idea.WorkItemReference is { } reference)
        {
            throw new DomainValidationException(
                $"Idea {id} carries the tracker link {reference}, and a plan does not cut from such an idea (the cut would adopt the item "
                + $"afresh): cut the drafts by hand with h9k task add --from-idea {id} --objective \"…\".");
        }

        if (project.IsArchived)
        {
            throw new DomainConflictException(
                $"Project '{project.Name}' is archived, so it takes no new task. Reactivate it first: h9k project reactivate {project.Name}");
        }

        if (project.PurgeAt is { } purgeDeadline)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is scheduled for permanent deletion at {purgeDeadline.ToLocalTime():g}, so it cannot take a new "
                + $"task. Cancel the purge first: h9k project cancel-purge {project.Name}");
        }
    }

    /// <summary>Reads the report from the workspace at this moment, and refuses with the reason when it is missing or unreadable.</summary>
    private static async Task<DiscoveryReadout> ReadReportAsync(string reportPath, string shortPlan, CancellationToken cancellationToken)
    {
        DiscoveryReading reading = await DiscoveryReport.ReadFileAsync(reportPath, cancellationToken);
        if (reading.Missing)
        {
            throw new DomainBusinessRuleException(
                $"The report is not at {reportPath}, so there is nothing to cut. h9k plan show {shortPlan} says what the daemon recorded; "
                + $"restore the file, or stop the plan with h9k plan stop {shortPlan} --reason \"<why>\".");
        }

        return reading.Readout
            ?? throw new DomainBusinessRuleException(
                $"The report at {reportPath} is not readable: its closing block must hold exactly one {DiscoveryReport.DecisionMarker} line "
                + $"naming pursue or stop, and for a pursue one or more {DiscoveryReport.TaskMarker} <objective> lines (a stop has none). "
                + "Fix the end of the file and run this again, or stop the plan.");
    }
}
