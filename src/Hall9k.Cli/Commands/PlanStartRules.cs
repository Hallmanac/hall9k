using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>One draft a plan is asked to start on: the stream's aggregate for the rules that read facts, and the document for the timestamps of a dispatch.</summary>
internal sealed record PlanStartDraft(TaskAggregate Task, TaskDetails Details);

/// <summary>
/// Every refusal <c>h9k plan start</c> gives, in one place and before any session starts. The checks that need a
/// roster or the disk (who may publish a draft, whether the checkout exists) are the command's, which passes their
/// answers in; the rest are pure over the facts handed in, so each refusal is testable with no database.
/// <para>
/// A plan refuses early for the same reason <c>h9k task publish</c> does: a refusal at the end of an hour of Opus
/// sessions costs the hour. The tracker attestation is the clearest case, because only a person can attest "I searched",
/// so it is asked here and carried to the publish, never guessed at the end.
/// </para>
/// </summary>
internal static class PlanStartRules
{
    public static PlanStarted Decide(
        IReadOnlyList<PlanStartDraft> drafts,
        ProjectDetails project,
        IReadOnlyList<PlanDetails> livePlans,
        PlanPhase startPhase,
        bool noExistingItem,
        bool untracked,
        Guid ownerId,
        Guid thisNodeId,
        DateTimeOffset now,
        TimeSpan foreignCeiling,
        string? missingCheckout)
    {
        if (drafts.Count == 0)
        {
            throw new DomainValidationException(
                "A plan needs at least one draft: h9k plan start <draft> [<draft>...]. Draft ids come from h9k task list --state draft.");
        }

        if (drafts.Select(draft => draft.Task.Id).Distinct().Count() != drafts.Count)
        {
            throw new DomainValidationException("A draft is named twice. Name each draft once.");
        }

        PlanPhase start = startPhase == PlanPhase.Unknown
            ? PlanPhase.Refinement
            : startPhase;

        // The project first: one plan is one project, and the rest of the checks quote it.
        Guid projectId = drafts[0].Task.ProjectId;
        if (drafts.FirstOrDefault(draft => draft.Task.ProjectId != projectId) is { } stranger)
        {
            throw new DomainBusinessRuleException(
                $"Draft {Short(stranger.Task)} belongs to a different project from {Short(drafts[0].Task)}. One plan is one project; "
                + "start a plan for each project's drafts.");
        }

        foreach (PlanStartDraft draft in drafts)
        {
            RefuseDraft(draft, livePlans, thisNodeId, now, foreignCeiling, start);
        }

        if (project.IsArchived)
        {
            throw new DomainConflictException(
                $"Project '{project.Name}' is archived, so no refinement session is started for its drafts. "
                + $"Reactivate it first: h9k project reactivate {project.Name}");
        }

        if (missingCheckout is not null)
        {
            throw new DomainBusinessRuleException(
                $"There is no working checkout of project '{project.Name}' at {missingCheckout}, and a refinement session "
                + "reads the code there to test the draft's claims. Create the home's repo/dev worktree with "
                + $"h9k project init {project.Name}, or point the project at an existing checkout with "
                + $"h9k project set {project.Name} --repo <path>, then start the plan again.");
        }

        if (!project.SkipPermissions)
        {
            throw new DomainBusinessRuleException(
                $"Project '{project.Name}' does not run agents with skip-permissions. A refinement session writes its report, "
                + "a revision file, and the draft's journal outside its checkout, which the plain settings refuse, so a plan "
                + $"needs it. Turn it on with h9k project set {project.Name} --skip-permissions true, or refine the drafts "
                + "with a person in the terminal (recipes/task-refinement.md).");
        }

        foreach (PlanStartDraft draft in drafts)
        {
            RefuseAdoptedText(draft.Task);
        }

        // The attestation, asked of each draft the way its publish would ask it, so a combination the publish would refuse is
        // refused here and a draft that is already linked needs none.
        string draftIds = string.Join(" ", drafts.Select(draft => Short(draft.Task)));
        bool recordsNoExistingItem = false;
        bool recordsUntracked = false;
        foreach (PlanStartDraft draft in drafts)
        {
            (bool noExistingRecorded, bool untrackedRecorded) = TaskDecider.VetTrackerAttestation(
                draft.Task, project.BacklogPolicy, noExistingItem, untracked, "Start the plan", $"h9k plan start {draftIds}");
            recordsNoExistingItem |= noExistingRecorded;
            recordsUntracked |= untrackedRecorded;
        }

        return new PlanStarted(
            Guid.Empty, projectId, [.. drafts.Select(draft => draft.Task.Id)], start,
            recordsNoExistingItem, recordsUntracked, ownerId, now);
    }

    /// <summary>
    /// Every refusal <c>h9k plan start --idea</c> gives, before any session starts, over facts the command read and handed in
    /// (who may act and whether the workspace and checkout are on this disk are the command's; the rest is pure). Records a
    /// plan in discovery with no drafts: the drafts exist only when a person pursues the report at the gate.
    /// <para>
    /// There is no override of who may start: the question is asked of the idea's holder, or of its creator when nobody holds
    /// it, and the refusal is this command's own words, because the shared owner guard's message offers a <c>--holder</c> that
    /// this command does not take.
    /// </para>
    /// </summary>
    public static PlanStarted DecideFromIdea(
        IdeaAggregate idea,
        bool mayAct,
        ProjectDetails? project,
        IReadOnlyList<PlanDetails> livePlans,
        bool workspaceHere,
        string? missingCheckout,
        Guid ownerId,
        DateTimeOffset now)
    {
        string id = TaskListCommand.ShortId(idea.Id);
        if (!mayAct)
        {
            throw new DomainBusinessRuleException(
                $"Only idea {id}'s assignee, or its creator when nobody holds it, may start a plan on it, because a plan spends "
                + "model tokens in their name; there is no override of this. Ask the person who holds the idea to run "
                + $"h9k plan start --idea {id}, or have them hand it over first (h9k idea assign {id}).");
        }

        IdeaDecider.RequireCaptured(idea, "start a plan on");

        if (idea.WorkItemReference is { } reference)
        {
            throw new DomainValidationException(
                $"Idea {id} carries the tracker link {reference}, and a plan does not start from such an idea: the first task cut "
                + "from it adopts the tracker item afresh, whatever the note now says, and the item's text is its author's, "
                + "which nobody would read once the plan ran unattended. Cut a draft by hand "
                + $"(h9k task add --from-idea {id} --objective \"<one outcome-phrased sentence>\"), put its context in your own words "
                + "(h9k task revise <draft> --context \"<what the work is>\"), and run h9k plan start <draft> on that draft.");
        }

        if (idea.ProjectId is null)
        {
            throw new DomainValidationException(
                $"Idea {id} belongs to no project yet, and a plan runs in a project's checkout. Move it to one first: "
                + $"h9k idea move {id} <project>");
        }

        if (project is null)
        {
            throw new DomainNotFoundException(
                $"Idea {id} belongs to project {idea.ProjectId}, which this node does not hold, so it cannot run a plan for it. "
                + "Start the plan on a node that holds the project.");
        }

        if (!workspaceHere)
        {
            throw new DomainBusinessRuleException(
                $"Idea {id} has no workspace directory on this node, and discovery writes its report there and reads what is "
                + "already in it. Start the plan on the node that captured the idea; a workspace does not travel between nodes.");
        }

        if (livePlans.FirstOrDefault(plan => plan.IsLive && plan.IdeaId == idea.Id) is { } existing)
        {
            throw new DomainConflictException(
                $"Idea {id} is already in plan {Short(existing.Id)}, which is still live on this node. h9k plan show {Short(existing.Id)} "
                + $"says where it stands; h9k plan cancel {Short(existing.Id)} --reason \"<why>\" ends it.");
        }

        if (project.IsArchived)
        {
            throw new DomainConflictException(
                $"Project '{project.Name}' is archived, so no discovery session is started for its idea. "
                + $"Reactivate it first: h9k project reactivate {project.Name}");
        }

        if (project.PurgeAt is { } purgeDeadline)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is scheduled for permanent deletion at {purgeDeadline.ToLocalTime():g}, so it cannot take "
                + $"a plan. Cancel the purge first: h9k project cancel-purge {project.Name}");
        }

        if (missingCheckout is not null)
        {
            throw new DomainBusinessRuleException(
                $"There is no working checkout of project '{project.Name}' at {missingCheckout}, and a discovery session reads "
                + "the code there to test the idea's claims. Create the home's repo/dev worktree with "
                + $"h9k project init {project.Name}, or point the project at an existing checkout with "
                + $"h9k project set {project.Name} --repo <path>, then start the plan again.");
        }

        if (!project.SkipPermissions)
        {
            throw new DomainBusinessRuleException(
                $"Project '{project.Name}' does not run agents with skip-permissions. A discovery session writes its report outside "
                + "its checkout, which the plain settings refuse, so a plan needs it. Turn it on with "
                + $"h9k project set {project.Name} --skip-permissions true, or discover the idea with a person in the terminal "
                + "(recipes/idea-discovery.md).");
        }

        return new PlanStarted(
            Guid.Empty, project.Id, [], PlanPhase.Discovery, NoExistingItemAttested: false, UntrackedAttested: false, ownerId, now,
            idea.Id);
    }

    private static void RefuseDraft(
        PlanStartDraft draft, IReadOnlyList<PlanDetails> livePlans, Guid thisNodeId, DateTimeOffset now,
        TimeSpan foreignCeiling, PlanPhase start)
    {
        TaskAggregate task = draft.Task;
        string id = Short(task);
        if (task.State != TaskState.Draft)
        {
            throw new DomainConflictException(
                $"Draft {id} is {task.State.Value}, not Draft: a plan carries drafts to Published, so there is nothing for it to do "
                + "with a task that has already published or run.");
        }

        if (task.Type == TaskType.PrReview || task.Type == TaskType.Spike)
        {
            throw new DomainBusinessRuleException(
                $"Draft {id} is a {task.Type.Value} task. A pr-review task has no author to refine and a spike is bounded by its "
                + "own exit criterion and is a run rather than a walk, so neither goes through a plan.");
        }

        if (livePlans.FirstOrDefault(plan => plan.Includes(task.Id)) is { } plan)
        {
            throw new DomainConflictException(
                $"Draft {id} is already in plan {Short(plan.Id)}, which is still live on this node. h9k plan show {Short(plan.Id)} says "
                + $"where it stands; h9k plan cancel {Short(plan.Id)} --reason \"<why>\" frees its drafts.");
        }

        TaskDetails details = draft.Details;
        if (task.ChallengeRequested
            || (task.RunningChallengeSessionId is not null
                && ForeignSessionCeiling.CountsAsRunning(
                    details.RunningChallengeNodeId, details.RunningChallengeDispatchedAt, thisNodeId, now, foreignCeiling)))
        {
            string standing = task.ChallengeRequested
                ? "requested"
                : "running";
            throw new DomainConflictException(
                $"Draft {id} already has a challenge {standing}. Let it finish (h9k task show says when its verdict lands) and start the plan then.");
        }

        if (task.RunningRefinementSessionId is not null
            && ForeignSessionCeiling.CountsAsRunning(
                details.RunningRefinementNodeId, details.RunningRefinementDispatchedAt, thisNodeId, now, foreignCeiling))
        {
            throw new DomainConflictException($"Draft {id} already has a refinement session running. Start the plan when it ends.");
        }

        if (start == PlanPhase.Challenge && task.AcceptanceCriteria.Count == 0)
        {
            throw new DomainValidationException(
                $"Draft {id} has no acceptance criteria, and a challenge tests the draft's claims against them, so it cannot start at "
                + $"challenge. Start it at refinement (leave out --at), or add criteria: h9k task revise {id} --criteria <criterion>");
        }
    }

    /// <summary>
    /// Refuses a draft whose context still carries a stranger's text: an adopted item's quoted description, or the notice
    /// that its idea's workspace holds a tracker item. Refinement rewrites such a quote into the task's own context, the
    /// build prompt's data-only rule then reads false, and a build would take a stranger's words as the owner's
    /// instruction with nobody having read them.
    /// </summary>
    private static void RefuseAdoptedText(TaskAggregate task)
    {
        string id = Short(task);
        if (WorkItemContext.CarriesQuotedDescription(task.AgentContext))
        {
            throw new DomainBusinessRuleException(
                $"Draft {id}'s context still carries the quoted description of an adopted tracker item, written by whoever filed it. "
                + "Refinement rewrites that quote into the task's own words, after which nothing marks it as a stranger's, so a "
                + "person has to read it into the task first. Replace the context in your own words with "
                + $"h9k task revise {id} --context \"<what the work is>\", read the objective as well (an adopted objective keeps "
                + "the item's title), and start the plan again.");
        }

        if (WorkItemContext.CarriesWorkspaceItemNotice(task.AgentContext))
        {
            throw new DomainBusinessRuleException(
                $"Draft {id}'s context says its idea's workspace holds a tracker item written by whoever filed it. Refinement could "
                + "carry that item's words into the task as if they were the owner's, so a person has to read the item into the "
                + $"task first. Replace the context in your own words with h9k task revise {id} --context \"<what the work is>\", "
                + "read the objective as well (a task cut from a linked idea keeps the item's title), and start the plan again.");
        }
    }

    private static string Short(TaskAggregate task) => TaskListCommand.ShortId(task.Id);

    private static string Short(Guid id) => TaskListCommand.ShortId(id);
}
