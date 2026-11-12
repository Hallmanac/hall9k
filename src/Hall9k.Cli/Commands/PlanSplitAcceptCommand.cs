using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
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
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Accept the split a refinement session proposed for a draft of a plan: read the session's report now, so a person's edit to its
/// closing block counts, and in one save make the original draft's objective the first increment and cut one new draft per later
/// increment, each blocked by and stacked on the one before it. Each new draft is what <c>h9k task add --from-idea</c> would create
/// from the original's idea, with the original's type, epic, context, and human gate (and its scope, when that is private), and the
/// original's own context gains a note naming the drafts that now carry the rest of the feature. The plan carries each through
/// refinement and challenge like a draft newly cut into it. The plan stream's version is the fence that makes two accepts at once cut one set of drafts.
/// </summary>
public sealed class PlanSplitAcceptCommand : Hall9kAsyncCommand<PlanSplitAcceptCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLAN>")]
        [Description(
            "Plan id (full, or an unambiguous fragment). A draft of it must be parked on a proposed split (h9k status lists it under "
            + "Needs you, and h9k plan show lists the increments)")]
        public string Plan { get; init; } = string.Empty;

        [CommandOption("--draft <DRAFT>")]
        [Description(
            "The draft whose split to accept (full id, or an unambiguous fragment of one of the plan's drafts). Required only when "
            + "more than one draft of the plan is parked on a proposed split")]
        public string? Draft { get; init; }

        [CommandOption("--no-existing-item")]
        [Description(
            "The dedup attestation h9k task publish takes, given once for the new drafts, as on h9k plan pursue: you searched the "
            + "project's tracker and found no open item covering them. Required on a project whose backlog policy is jira, "
            + "github-issues, or linear unless --untracked is given or the plan already records an attestation. Recorded on the plan, "
            + "and what its publish passes on")]
        public bool NoExistingItem { get; init; }

        [CommandOption("--untracked")]
        [Description(
            "The sibling attestation: deliberately skip external tracking for the new drafts. Means what it means on h9k task "
            + "publish, so it is refused alongside --no-existing-item (or a plan that already records it) and on a project whose "
            + "backlog policy tracks nothing")]
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

        // Asked first: a plan with no split to accept has no report to read, and the refusal says where it stands.
        PlanDraftState parked = PlanDecider.SplitToAccept(plan, settings.Draft is { } draft ? DraftOf(plan, draft) : null);
        string shortPlan = TaskListCommand.ShortId(plan.Id);
        string shortDraft = TaskListCommand.ShortId(parked.DraftId);
        IReadOnlyList<string> increments = await ReadIncrementsAsync(parked, shortPlan, cancellationToken);

        StreamState originalFence = await session.Events.FetchStreamStateAsync(parked.DraftId, cancellationToken)
            ?? throw new DomainNotFoundException($"Draft {parked.DraftId} of plan {shortPlan} is not on this node.");
        TaskAggregate original = await session.Events.AggregateStreamAsync<TaskAggregate>(
                parked.DraftId, version: originalFence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"Draft {parked.DraftId} of plan {shortPlan} is not on this node.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Plan {plan.Id} names project {plan.ProjectId}, which is not registered here.");
        RefuseProject(project);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await TaskOwnerGuard.AssertMayActAsync(session, original, context, cancellationToken);

        // The idea is read at a stream version, and the cuts are appended expecting that version plus themselves, so a conclude,
        // archive, or move that commits after this read rolls the whole save back instead of being cut over.
        StreamState? ideaFence = null;
        IdeaAggregate? idea = null;
        if (original.SourceIdeaId is { } ideaId)
        {
            ideaFence = await session.Events.FetchStreamStateAsync(ideaId, cancellationToken)
                ?? throw new DomainNotFoundException($"Draft {shortDraft} names idea {ideaId}, which is not on this node.");
            idea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, version: ideaFence.Version, token: cancellationToken)
                ?? throw new DomainNotFoundException($"Draft {shortDraft} names idea {ideaId}, which is not on this node.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid[] taskIds = [.. increments.Skip(1).Select(_ => DomainId.New())];
        TaskRevised firstIncrement = TaskDecider.Revise(
            original,
            Optional<string>.Of(increments[0]),
            Optional<IReadOnlyList<string>>.None,
            Optional<string>.Of(FirstIncrementContextOf(original, increments.Count, taskIds)),
            Optional<IReadOnlyList<Guid>>.None,
            Optional<TaskType>.None,
            Optional<AgentModel>.None,
            now,
            context.OwnerId);

        // The publish passes the plan's one pair of flags to every draft, so each new draft is vetted against the pair the plan
        // will hold once this lands: what it already records, with what this command adds.
        bool noExistingItem = plan.NoExistingItemAttested || settings.NoExistingItem;
        bool untracked = plan.UntrackedAttested || settings.Untracked;
        string retry = $"h9k plan split accept {shortPlan}" + (settings.Draft is null ? string.Empty : $" --draft {shortDraft}");
        List<(TaskAdded Added, IdeaTaskCut? Cut)> drafts = [];
        bool recordsNoExistingItem = plan.NoExistingItemAttested;
        bool recordsUntracked = plan.UntrackedAttested;
        Guid previous = original.Id;
        for (int index = 1; index < increments.Count; index++)
        {
            Guid taskId = taskIds[index - 1];
            IdeaTaskCut? cut = idea is null
                ? null
                : IdeaDecider.CutTask(idea, taskId, increments[index], now, context.OwnerId);
            TaskAdded added = TaskDecider.Add(
                taskId, project.Id, cut?.Objective ?? increments[index], acceptanceCriteria: [], original.Type,
                ContextOf(original, index, increments.Count, previous), original.Constraints, externalReference: null, now, context.OwnerId,
                model: null, blockedBy: [previous], sourceIdeaId: original.SourceIdeaId, epicId: original.EpicId, stackedOnTaskId: previous,
                // The feature's hold travels with every increment, so no part of it opens a pull request the owner meant to review.
                humanGate: original.HumanGate,
                // The copy carries the original's words, so it must not travel further than they were allowed to: a private draft
                // splits into private drafts. Any other scope starts at fleet, the resting scope for a fresh draft, as an evolve does.
                initialScope: original.Scope == ReplicationScope.Private
                    ? ReplicationScope.Private
                    : null);

            // The attestation, asked of each draft the way its publish would ask it, so a combination the publish would refuse is
            // refused here, before anything is cut.
            TaskAggregate asCut = new();
            asCut.Apply(added);
            (bool noExistingRecorded, bool untrackedRecorded) = TaskDecider.VetTrackerAttestation(
                asCut, project.BacklogPolicy, noExistingItem, untracked, "Accept the split", retry);
            recordsNoExistingItem |= noExistingRecorded;
            recordsUntracked |= untrackedRecorded;
            drafts.Add((added, cut));
            previous = taskId;
        }

        PlanSplitAccepted accepted = PlanDecider.AcceptSplit(
            plan, parked.DraftId, [.. drafts.Select(entry => entry.Added.Id)], recordsNoExistingItem, recordsUntracked, context.OwnerId, now);
        session.Events.Append(original.Id, expectedVersion: originalFence.Version + 1, firstIncrement);
        foreach ((TaskAdded added, IdeaTaskCut? _) in drafts)
        {
            session.Events.StartStream<TaskAggregate>(added.Id, added);
        }

        if (idea is not null && ideaFence is not null)
        {
            IdeaTaskCut[] cuts = [.. drafts.Select(entry => entry.Cut).OfType<IdeaTaskCut>()];
            session.Events.Append(idea.Id, expectedVersion: ideaFence.Version + cuts.Length, cuts);
        }

        session.Events.Append(plan.Id, expectedVersion: fence.Version + 1, accepted);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // One save fences up to three streams, and the exception does not say which moved, so the plan is looked at again.
            StreamState? planNow = await session.Events.FetchStreamStateAsync(plan.Id, cancellationToken);
            throw planNow is not null && planNow.Version == fence.Version
                ? new DomainConflictException(
                    $"Draft {shortDraft} or its idea changed while this was being recorded, so nothing was cut. Read them back with "
                    + $"h9k plan show {shortPlan}, and accept again only if the draft is still parked on the split.")
                : new DomainConflictException(
                    $"Plan {plan.Id} changed while this was being recorded, so nothing was cut. Check h9k plan show {shortPlan}; if the "
                    + "draft is no longer parked on the split, another accept got there first.");
        }

        await Doorbell.RingAsync($"plan-split-accepted:{plan.Id}", cancellationToken);

        AnsiConsole.MarkupLine(
            $"[blue]Split accepted[/] on plan {shortPlan}: draft {shortDraft} now carries the first of {increments.Count} increments, and "
            + $"{drafts.Count} new {(drafts.Count == 1 ? "draft is" : "drafts are")} stacked after it, all in refinement");
        AnsiConsole.MarkupLine($"  {shortDraft}  {ExternalText.OneLineMarkup(increments[0])}");
        foreach ((TaskAdded added, IdeaTaskCut? _) in drafts)
        {
            AnsiConsole.MarkupLine(
                $"  {TaskListCommand.ShortId(added.Id)}  {ExternalText.OneLineMarkup(added.Objective)} "
                + $"[dim](stacked on {TaskListCommand.ShortId(added.StackedOnTaskId ?? Guid.Empty)})[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]  The daemon refines and challenges each one, and publishes them with the plan's other drafts once every one is ready. "
            + "Tasks blocked by the original draft still wait on it alone; point them at a later increment yourself if they need the "
            + "whole feature. None of these drafts splits again in this plan. Read where it stands with:[/] "
            + $"h9k plan show {shortPlan}");
        if (DaemonProcess.Probe() is null)
        {
            AnsiConsole.MarkupLine("[yellow]  The daemon is not running.[/] [dim]A plan waits for it: h9k daemon start[/]");
        }

        return ExitCodes.Ok;
    }

    /// <summary>The draft of the plan that <paramref name="idOrFragment"/> names: a full id, or an unambiguous fragment of either end of one.</summary>
    private static Guid DraftOf(PlanAggregate plan, string idOrFragment)
    {
        string fragment = idOrFragment.Trim().Replace("-", string.Empty);
        Guid[] matches = [.. plan.Drafts
            .Select(draft => draft.DraftId)
            .Where(id => fragment.Length > 0
                && (id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                    || id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase)))];
        return matches switch
        {
            [var only] => only,
            [] => throw new DomainNotFoundException(
                $"Plan {DomainId.Short(plan.Id)} carries no draft '{idOrFragment}'. h9k plan show {DomainId.Short(plan.Id)} lists its drafts."),
            _ => throw new DomainValidationException(
                $"'{idOrFragment}' matches {matches.Length} drafts of plan {DomainId.Short(plan.Id)} "
                + $"({string.Join(", ", matches.Select(DomainId.Short))}); pass more of the id."),
        };
    }

    /// <summary>
    /// A new draft's context: the original's, so the increment keeps every pointer the whole feature had, and a note naming the
    /// draft it was split from and the one it builds on. Only ids are written: a draft's context replicates to every node.
    /// </summary>
    private static string ContextOf(TaskAggregate original, int index, int count, Guid buildsOn)
    {
        string note = $"This task is increment {index + 1} of {count}, split from draft {DomainId.Short(original.Id)} by its refinement "
            + $"session, and builds on {DomainId.Short(buildsOn)}. The context above is the original draft's, written for the whole "
            + "feature; this task delivers only its own increment.";
        return original.AgentContext is { } own && own.IsNotBlank()
            ? $"{own.TrimEnd()}\n\n{note}"
            : note;
    }

    /// <summary>
    /// The original draft's context once it carries only the first increment: its own, and a note that the rest of the feature now
    /// belongs to the drafts named, so its next refinement narrows its criteria to this increment instead of keeping the whole
    /// feature or proposing the split again. Only ids are written: a draft's context replicates to every node.
    /// </summary>
    private static string FirstIncrementContextOf(TaskAggregate original, int count, IReadOnlyList<Guid> rest)
    {
        string note = $"This task is increment 1 of {count}, split by its refinement session; drafts "
            + $"{string.Join(", ", rest.Select(DomainId.Short))} now carry the rest of the feature, in that order. The acceptance criteria "
            + "and context above were written for the whole feature; this task delivers only its own increment, so narrow its criteria to "
            + "that increment and leave the rest to those drafts.";
        return original.AgentContext is { } own && own.IsNotBlank()
            ? $"{own.TrimEnd()}\n\n{note}"
            : note;
    }

    /// <summary>Reads the parked session's report at this moment, and refuses with the reason when it is missing or no longer a split.</summary>
    private static async Task<IReadOnlyList<string>> ReadIncrementsAsync(PlanDraftState parked, string shortPlan, CancellationToken cancellationToken)
    {
        Guid session = parked.LatestSessionId
            ?? throw new DomainConflictException(
                $"Plan {shortPlan} records no session for draft {DomainId.Short(parked.DraftId)}, so there is no report to read the split from. "
                + $"h9k plan resume {shortPlan} runs refinement again.");
        string reportPath = RunPaths.RefinementReportFile(session);
        RefinementSplitReading reading = await RefinementSplitReport.ReadFileAsync(reportPath, cancellationToken);
        if (reading.Missing)
        {
            throw new DomainBusinessRuleException(
                $"The report is not at {reportPath}, so there is no split to accept. Restore the file, or keep the draft as one task: "
                + $"record that decision in its journal.md, then h9k plan resume {shortPlan}.");
        }

        return reading.Increments
            ?? throw new DomainBusinessRuleException(
                $"The report at {reportPath} no longer ends with a split: its closing block must be one {RefinementSplitReport.OutcomeMarker} "
                + $"{RefinementOutcome.Split.Word} line followed directly by two or more {RefinementSplitReport.IncrementMarker} <objective> "
                + "lines, with no blank line between them. Fix the end of the file and run this again, or keep the draft as one task: record "
                + $"that decision in its journal.md, then h9k plan resume {shortPlan}.");
    }

    /// <summary>A project that takes no new task refuses the cut, as it refuses h9k plan pursue's.</summary>
    private static void RefuseProject(ProjectDetails project)
    {
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
}
