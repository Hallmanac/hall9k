using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;

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

        return new IdeaCaptured(id, ownerId, text.Trim(), Vet(projectId), capturedAt, workspaceHome.Value);
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

    private static void RequireCaptured(IdeaAggregate idea, string verb)
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
}
