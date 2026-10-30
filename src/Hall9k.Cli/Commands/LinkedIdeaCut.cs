using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The rules for cutting a task from an idea that carries a tracker link (<c>h9k task add --from-idea</c>).
/// <para>
/// A cut has three outcomes. It adopts the item, reading it afresh through the importer and never from
/// the workspace file, under every refusal <c>h9k task add --from-linear</c> has plus the idea-side state
/// rule that lets Linear's triage through. It is created unlinked at the human's word
/// (<c>--unlinked</c>), reading no tracker. Or it is created unlinked because a task cut from the same
/// idea already holds the item, which is checked on the idea's stored reference before any tracker read so
/// that it works after the item has closed.
/// </para>
/// <para>
/// Every refusal of the first outcome names its cause and the two exits that stay open: a task cut with no
/// link, and finishing with the idea. The importer is a seam (a factory, called only when a tracker is
/// actually read) so the rules are pinned without any tracker call.
/// </para>
/// </summary>
internal static class LinkedIdeaCut
{
    /// <summary>
    /// What a cut came to: <see cref="Item"/> when it adopted the item (the reference to record and the
    /// observation to quote), <see cref="Sibling"/> when it is unlinked because a task cut from the same idea
    /// holds the item, and neither when the human asked for <c>--unlinked</c>.
    /// </summary>
    internal sealed record Resolution(ImportedWorkItem? Item, TaskListItem? Sibling)
    {
        public bool Unlinked => Item is null;
    }

    internal static async Task<Resolution> ResolveAsync(
        IDocumentSession session, ILedger ledger, Func<CancellationToken, Task<WorkItemImporter>> importerFactory,
        IdeaAggregate idea, ProjectDetails project, Guid? epicId, Guid nodeId, string? ownerRootFingerprint,
        bool unlinked, CancellationToken cancellationToken)
    {
        ExternalReference stored = idea.WorkItemReference
            ?? throw new DomainValidationException($"Idea {idea.Id} carries no tracker link, so there is nothing to resolve.");
        if (unlinked)
        {
            return new Resolution(null, null);
        }

        // A task cut from this idea that still holds the item is asked about on the stored reference,
        // before any tracker read, so a cut works after the item has closed or moved. "Holds" is the
        // adoption guard's own reading (any state but Abandoned, and the solution-epic exception), so a
        // cut that exception admits is not one that gets here and adopts as usual below.
        SharedIssueBar? bar = await TaskAddCommand.FindBarAsync(
            session, stored, new TaskAddCommand.AdoptionSite(project.Id, epicId), ownerRootFingerprint, cancellationToken);
        if (bar is not null && idea.CutTaskIds.Contains(bar.Holder.Task.Id))
        {
            return new Resolution(null, bar.Holder.Task);
        }

        string ideaShortId = TaskListCommand.ShortId(idea.Id);
        try
        {
            ProjectTracker.RefuseLinkAgainstTracker(
                stored.Provider, project.LinearTeam, project.Name, "h9k task add --from-idea");
        }
        catch (DomainException refusal)
        {
            throw WithExits(refusal, ideaShortId, notFoundFor: null);
        }

        WorkItemImporter importer = await importerFactory(cancellationToken);
        if (importer.Unusable.TryGetValue(stored.Provider, out Func<DomainException>? unusable))
        {
            throw WithExits(unusable(), ideaShortId, notFoundFor: null);
        }

        ImportedWorkItem item;
        try
        {
            item = await importer.ImportForIdeaAsync(
                new WorkItemImportRequest(stored.Provider, stored.Reference, project.RepositoryPath),
                remedy: null, cancellationToken);
        }
        catch (DomainNotFoundException missing)
        {
            throw WithExits(missing, ideaShortId, notFoundFor: stored.Provider);
        }
        catch (DomainException refusal)
        {
            throw WithExits(refusal, ideaShortId, notFoundFor: null);
        }

        try
        {
            await TaskAddCommand.RefuseHeldItemAsync(
                session, ledger, project, epicId, nodeId, ownerRootFingerprint, item, checkLedgerRecord: true,
                cancellationToken);
        }
        catch (DomainException refusal)
        {
            throw WithExits(refusal, ideaShortId, notFoundFor: null);
        }

        return new Resolution(item, null);
    }

    /// <summary>
    /// The refusal again, as the same kind of exception, with the two exits that stay open appended: the
    /// cause is the caller's own words and is kept whole. <paramref name="notFoundFor"/> is set for a read
    /// that found nothing, which also gets the step that follows <c>--unlinked</c> there.
    /// </summary>
    private static DomainException WithExits(DomainException cause, string ideaShortId, WorkItemProvider? notFoundFor)
    {
        string message = $"{cause.Message} This idea carries a tracker link, so a cut from it is refused here. Two ways forward: "
            + $"cut the task with no link and no tracker read, h9k task add --from-idea {ideaShortId} --objective \"…\" "
            + $"--unlinked; or finish with the idea, h9k idea conclude {ideaShortId} --reason \"…\" or "
            + $"h9k idea archive {ideaShortId} --reason \"…\"."
            + (notFoundFor is { } provider
                ? $" After --unlinked, if the item has a new name, record it on the new task with "
                    + $"{LinkCommand(provider)} <new task> <new identifier>."
                : string.Empty);

        return cause switch
        {
            DomainNotFoundException => new DomainNotFoundException(message),
            DomainConflictException => new DomainConflictException(message),
            DomainBusinessRuleException => new DomainBusinessRuleException(message),
            _ => new DomainValidationException(message),
        };
    }

    private static string LinkCommand(WorkItemProvider provider) =>
        provider == WorkItemProvider.Jira
            ? "h9k task link-jira"
            : provider == WorkItemProvider.GitHub
                ? "h9k task link-issue"
                : "h9k task link-linear";

    /// <summary>
    /// The idea's note as agent context, or null when the note is still the title seed. A seed is the
    /// item's own title folded and defused (<see cref="TaskAddCommand.ObjectiveSeed"/>), and text the
    /// item's author wrote appears in a cut's context only inside the fenced quote
    /// (<see cref="WorkItemContext.Compose"/>), so a note nobody has revised since capture is left out of
    /// every cut from a linked idea. A note a human wrote or revised rides along as it always has.
    /// </summary>
    internal static string? NoteForContext(IdeaAggregate idea)
    {
        string note = idea.Text.Trim();
        return idea.WorkItemObservedTitle is { } title && note == TaskAddCommand.ObjectiveSeed(title)
            ? null
            : note;
    }

    /// <summary>
    /// The context of a cut from a linked idea: the note when it is a human's, any <c>--context</c>, and
    /// the discovery workspace pointer, the same composition a plain cut has. When the item was adopted the
    /// whole of that rides after <see cref="WorkItemContext.Compose"/>'s output for the fresh read, which
    /// therefore leads; when it was not, the sibling task that holds the item is named, with the route that
    /// carries the item to this task anyway.
    /// </summary>
    internal static string ComposeContext(
        IdeaAggregate idea, Resolution resolution, string? additionalContext)
    {
        string? human = NoteForContext(idea);
        string? note = human is not null && additionalContext.IsNotBlank()
            ? $"{human}\n\n{additionalContext.Trim()}"
            : human ?? additionalContext?.Trim();
        string withWorkspace = IdeaPromoteCommand.AgentContext(idea, note);
        if (resolution.Item is { } item)
        {
            return WorkItemContext.Compose(item, withWorkspace);
        }

        return resolution.Sibling is { } sibling
            ? $"{withWorkspace}\n\n{SiblingNotice(idea, sibling)}"
            : withWorkspace;
    }

    /// <summary>
    /// Why this cut carries no link and what to do about it, said both in the command's output and in the
    /// new task's agent context: the holding task, and the route that gets the item to this task without a
    /// second task holding it. Written in Hall9k's own words and the human's, never the item author's.
    /// </summary>
    internal static string SiblingNotice(IdeaAggregate idea, TaskListItem sibling)
    {
        string shortId = TaskListCommand.ShortId(sibling.Id);
        return $"This task carries no tracker link. {idea.WorkItemReference} is held by task {shortId} "
            + $"({ExternalText.OneLine(sibling.Objective)}), which was cut from the same idea, and one item is one live task. "
            + $"To get the item to this task: carry it in task {shortId}'s context, publish this task with --untracked "
            + "(h9k task publish <this task> --untracked), and group the tasks under an epic with h9k epic add.";
    }

    /// <summary>The one line the output adds when the adopted issue is still in Triage, without any promise about what moves it.</summary>
    internal static string TriageNotice(ImportedWorkItem item) =>
        $"{item.Reference} is still in Triage in Linear ({item.ObservedStamp}). Hall9k adopted it as it stands and "
        + "does not move it out of Triage; that stays a decision in Linear.";
}
