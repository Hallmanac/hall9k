using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Every decision an idea makes, in one place (TASK-MODEL.md §7). The rules are deliberately
/// few: capture asks for a thought and nothing else, cutting a task is repeatable and never
/// ends the idea on its own, and the only hard edges are the two endings — concluded, discovery
/// produced something, or archived, it did not — both explicit human acts (Brian, 2026-08-21).
/// </summary>
public static class IdeaDecider
{
    /// <summary>
    /// The sacred path: text, and optionally a project when it already knows one. Anything
    /// else demanded here would turn capture into a commitment to think, which is the thing
    /// capture exists to avoid.
    /// <para>
    /// <paramref name="workspaceHome"/> is passed through verbatim rather than derived here: it
    /// is an observation about the filesystem (did the project's home already exist on this
    /// machine at this instant), and a decider has no filesystem to observe it from — the
    /// caller checks and hands the answer in, exactly as it would any other externally-observed
    /// fact.
    /// </para>
    /// </summary>
    public static IdeaCaptured Capture(
        Guid id, Guid ownerId, string text, Guid? projectId, DateTimeOffset capturedAt, ProjectHome workspaceHome)
    {
        if (ownerId == Guid.Empty)
        {
            throw new DomainValidationException("An idea belongs to the human who had it — no owner, no idea.");
        }

        if (text.IsBlank())
        {
            throw new DomainValidationException(
                "An idea is the thought itself, so there has to be one: h9k idea add \"<what you were thinking>\". "
                + "Nothing else is required — a project is optional, and everything else is discovery's job.");
        }

        return new IdeaCaptured(
            id, ownerId, text.Trim(), Vet(projectId), capturedAt, workspaceHome.Value, InitialScope: ReplicationScope.Fleet);
    }

    /// <summary>
    /// Rewriting the note as discovery sharpens it. Captured-only: once concluded or archived
    /// the ending is the record, and once a draft exists h9k task revise is where its own
    /// wording changes.
    /// </summary>
    public static IdeaRevised Revise(IdeaAggregate idea, string text, DateTimeOffset revisedAt, Guid revisedByOwnerId)
    {
        RequireCaptured(idea, "revise");

        if (text.IsBlank())
        {
            throw new DomainValidationException(
                $"A revision replaces the whole note, so it needs text: h9k idea revise {idea.Id} \"<the sharper version>\". "
                + $"To close the idea instead: h9k idea archive {idea.Id} --reason \"<why>\"");
        }

        if (text.Trim() == idea.Text)
        {
            throw new DomainValidationException(
                "The note already reads exactly that, so there is nothing to record — the stream keeps "
                + "revisions, not repetitions.");
        }

        return new IdeaRevised(idea.Id, text.Trim(), revisedAt, revisedByOwnerId);
    }

    /// <summary>
    /// Where the idea turned out to belong — set when capture did not know, or changed when it
    /// guessed wrong. Nothing about the idea's text or workspace moves; only the binding.
    /// </summary>
    public static IdeaAssignedToProject AssignToProject(
        IdeaAggregate idea, Guid projectId, DateTimeOffset assignedAt, Guid assignedByOwnerId)
    {
        RequireCaptured(idea, "assign to a project");

        if (projectId == Guid.Empty)
        {
            throw new DomainValidationException("Assigning an idea to a project needs the project.");
        }

        if (idea.ProjectId == projectId)
        {
            throw new DomainConflictException(
                "The idea is already assigned to that project — nothing to change.");
        }

        return new IdeaAssignedToProject(idea.Id, projectId, idea.ProjectId, assignedAt, assignedByOwnerId);
    }

    /// <summary>
    /// One task cut from an idea, through the ordinary add door (<c>h9k task add --from-idea</c>,
    /// backlog 31). Repeatable: cutting a task never ends the idea's own story, because
    /// discovery may keep producing more of them. The objective is required and never taken
    /// mechanically from the note — several tasks fanned out from one idea cannot share its
    /// first sentence, so each cut supplies its own in the human's own words.
    /// </summary>
    public static IdeaTaskCut CutTask(
        IdeaAggregate idea, Guid taskId, string objective, DateTimeOffset cutAt, Guid cutByOwnerId)
    {
        RequireCaptured(idea, "cut a task from");

        if (objective.IsBlank())
        {
            throw new DomainValidationException(
                "Cutting a task from an idea needs its own objective — several tasks fanned out "
                + $"from one idea cannot share its first sentence: h9k task add --from-idea {idea.Id} "
                + "--objective \"<one outcome-phrased sentence>\".");
        }

        return new IdeaTaskCut(idea.Id, taskId, objective.Trim(), cutAt, cutByOwnerId);
    }

    /// <summary>
    /// The idea-side half of a spike's <see cref="Tasks.Events.SpikeConcluded"/> (task: a spike is
    /// a run, not a walk): recordable whatever the idea's own state — a spike cut before the idea
    /// concluded or was archived can still reach its verdict after, and the provenance trail is
    /// honest either way. <paramref name="taskId"/> is trusted rather than re-checked against
    /// <see cref="IdeaAggregate.CutTaskIds"/> here: the caller (the daemon's spike finalize step)
    /// already knows this task's own <c>SourceIdeaId</c> names this idea, which is the fact that
    /// matters — a stream rewritten to drop an old cut from the list should not un-happen a
    /// verdict that already landed.
    /// </summary>
    public static IdeaSpikeConcluded RecordSpikeConcluded(
        IdeaAggregate idea, Guid taskId, SpikeVerdict verdict, string reason, DateTimeOffset concludedAt) =>
        new(idea.Id, taskId, verdict, reason, concludedAt);

    /// <summary>
    /// Discovery happened and something came of it: tasks were cut, or an outcome was acted on
    /// some other way. Always an explicit human act (Brian, 2026-08-21) — cutting a task never
    /// appends this on its own, because discovery may keep producing.
    /// </summary>
    public static IdeaConcluded Conclude(
        IdeaAggregate idea, string reason, DateTimeOffset concludedAt, Guid concludedByOwnerId)
    {
        RequireCaptured(idea, "conclude");

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                $"Concluding an idea records why: h9k idea conclude {idea.Id} --reason \"<what came of it>\". "
                + "Tasks cut, or an outcome acted on some other way — either way, say what happened.");
        }

        return new IdeaConcluded(idea.Id, reason.Trim(), concludedAt, concludedByOwnerId);
    }

    /// <summary>
    /// Discovery happened and nothing came of it. The reason is required: an idea set aside
    /// without one leaves the next reader (or the same human in six months) guessing at why,
    /// which is exactly the provenance the never-guess rule exists to protect. Nothing is
    /// deleted.
    /// </summary>
    public static IdeaArchived Archive(
        IdeaAggregate idea, string reason, DateTimeOffset archivedAt, Guid archivedByOwnerId)
    {
        RequireCaptured(idea, "archive");

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                $"Archiving an idea records why: h9k idea archive {idea.Id} --reason \"<why this is not worth pursuing>\". "
                + "The idea is kept either way — an idea that keeps coming back is a signal, and "
                + "an archive with no reason throws that signal away.");
        }

        return new IdeaArchived(idea.Id, reason.Trim(), archivedAt, archivedByOwnerId);
    }

    private static Guid? Vet(Guid? projectId) => projectId == Guid.Empty ? null : projectId;

    /// <summary>
    /// Every decider method below that requires a captured idea calls this first, so it is also
    /// the refusal to lead with in a command that resolves other things (a project, a task) before
    /// deciding: an idea's own terminal state is the one refusal every path through it shares, and
    /// checking it after everything else means an already-archived idea earns whatever unrelated
    /// validation runs first instead (independent pre-PR review).
    /// </summary>
    public static void RequireCaptured(IdeaAggregate idea, string verb)
    {
        if (idea.State == IdeaState.Captured)
        {
            return;
        }

        throw idea.State switch
        {
            var state when state == IdeaState.Concluded => new DomainConflictException(
                $"Idea {idea.Id} was concluded"
                + (idea.ConcludeReason.IsNotBlank() ? $" ({idea.ConcludeReason})" : string.Empty)
                + $", so there is nothing to {verb}. See what it fanned out to: h9k idea show {idea.Id}"),
            var state when state == IdeaState.Archived => new DomainConflictException(
                $"Idea {idea.Id} was archived ({idea.ArchiveReason}), so there is nothing to {verb}. "
                + "It stays on the record; capture a fresh idea if the thought has come back: h9k idea add \"…\""),
            _ => new DomainNotFoundException($"Idea {idea.Id} has no captured state to {verb}."),
        };
    }

    /// <summary>
    /// idea 8c5993c5: sets this idea's own replication scope — gates outbound replication, not its
    /// lifecycle, so an about-to-be-archived idea can still be scoped on its way out. Refused when
    /// the idea is already at <paramref name="scope"/> (nothing to change) or already
    /// <see cref="ReplicationScope.Team"/> and <paramref name="scope"/> asks for anything narrower:
    /// team is one-way, because other project members may already hold a copy once an idea reaches
    /// it, and there is no message that un-sends what they already have.
    /// </summary>
    public static IdeaScopeSet SetScope(IdeaAggregate idea, ReplicationScope scope, DateTimeOffset setAt, Guid setByOwnerId)
    {
        if (idea.Scope == scope)
        {
            throw new DomainConflictException($"Idea {idea.Id} is already at {scope.Value} scope — nothing to change.");
        }

        if (idea.Scope.IsAtLeast(ReplicationScope.Team))
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} is already shared with the team — team scope is one-way, since other members "
                + "may already hold a copy and there is no message that un-sends what they already have.");
        }

        return new IdeaScopeSet(idea.Id, scope, setAt, setByOwnerId);
    }

    /// <summary>
    /// idea 8c5993c5: <c>h9k idea share</c> — sugar for <see cref="SetScope"/> at
    /// <see cref="ReplicationScope.Team"/>. Unlike a task, which reaches team automatically on
    /// publish, an idea has no such automatic door: it reaches the team only on this explicit word.
    /// Works on a captured idea in any state, exactly as <see cref="SetScope"/> does.
    /// </summary>
    public static IdeaScopeSet Share(IdeaAggregate idea, DateTimeOffset setAt, Guid setByOwnerId) =>
        SetScope(idea, ReplicationScope.Team, setAt, setByOwnerId);

    /// <summary>
    /// idea 8c5993c5: the pre-8c5993c5 <c>h9k idea set-private</c> alias, kept as sugar over
    /// <see cref="SetScope"/> — <paramref name="isPrivate"/> true means
    /// <see cref="ReplicationScope.Private"/>, false means <see cref="ReplicationScope.Fleet"/> (the
    /// ordinary "not private" resting scope for an idea that has not been explicitly shared).
    /// </summary>
    public static IdeaScopeSet SetPrivate(IdeaAggregate idea, bool isPrivate, DateTimeOffset setAt, Guid setByOwnerId) =>
        SetScope(idea, isPrivate ? ReplicationScope.Private : ReplicationScope.Fleet, setAt, setByOwnerId);
}
