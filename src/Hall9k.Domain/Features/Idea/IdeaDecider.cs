using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Every decision an idea makes, in one place (TASK-MODEL.md §7). The rules are deliberately
/// few: capture asks for a thought and nothing else, and the only hard edges are the two
/// endings — a promoted idea's story continues on its task, a discarded one's is over
/// (Decisions Log #35).
/// </summary>
public static class IdeaDecider
{
    /// <summary>
    /// The sacred path: text, and optionally a project when it already knows one. Anything
    /// else demanded here would turn capture into a commitment to think, which is the thing
    /// capture exists to avoid.
    /// </summary>
    public static IdeaCaptured Capture(
        Guid id, Guid ownerId, string text, Guid? projectId, DateTimeOffset capturedAt)
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

        return new IdeaCaptured(id, ownerId, text.Trim(), Vet(projectId), capturedAt);
    }

    /// <summary>
    /// Rewriting the note as discovery sharpens it. Captured-only: after promotion the draft
    /// task is the thing being worked on, and h9k task revise is where wording changes.
    /// </summary>
    public static IdeaRevised Revise(IdeaAggregate idea, string text, DateTimeOffset revisedAt, Guid revisedByOwnerId)
    {
        RequireCaptured(idea, "revise");

        if (text.IsBlank())
        {
            throw new DomainValidationException(
                $"A revision replaces the whole note, so it needs text: h9k idea revise {idea.Id} \"<the sharper version>\". "
                + $"To close the idea instead: h9k idea discard {idea.Id} --reason \"<why>\"");
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
    /// Discovery is over and the idea has intent: it becomes a draft task, which is where
    /// REFINEMENT happens. Promotion needs a project — supplied now or already assigned — and
    /// an objective taken from the note or typed by the human, never inferred.
    /// </summary>
    public static IdeaPromoted Promote(
        IdeaAggregate idea,
        Guid taskId,
        Guid? projectId,
        string objective,
        DateTimeOffset promotedAt,
        Guid promotedByOwnerId)
    {
        if (idea.State == IdeaState.Promoted)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} was already promoted — it became task {idea.PromotedTaskId}. "
                + $"Work on that draft: h9k task show {idea.PromotedTaskId}. "
                + "An idea promotes once; a second thought about it is a second idea.");
        }

        RequireCaptured(idea, "promote");

        Guid destination = projectId ?? idea.ProjectId ?? Guid.Empty;
        if (destination == Guid.Empty)
        {
            throw new DomainValidationException(
                "Promotion needs a project, because a task belongs to one: h9k idea promote "
                + $"{idea.Id} --project <name>. If this idea IS a new project, register it first "
                + "(h9k project add --name <name> --repo <path>) and then promote into it — the platform "
                + "will not invent a repository for you (Decisions Log #35).");
        }

        if (objective.IsBlank())
        {
            throw new DomainValidationException(
                "The draft needs an objective and the note gave nothing to take one from. "
                + $"Say it outright: h9k idea promote {idea.Id} --objective \"<one outcome-phrased sentence>\"");
        }

        return new IdeaPromoted(idea.Id, taskId, destination, objective.Trim(), promotedAt, promotedByOwnerId);
    }

    /// <summary>
    /// Closing an idea honestly. The reason is required: an idea dropped without one leaves
    /// the next reader (or the same human in six months) guessing at why, which is exactly the
    /// provenance the never-guess rule exists to protect. Nothing is deleted.
    /// </summary>
    public static IdeaDiscarded Discard(
        IdeaAggregate idea, string reason, DateTimeOffset discardedAt, Guid discardedByOwnerId)
    {
        if (idea.State == IdeaState.Promoted)
        {
            throw new DomainConflictException(
                $"Idea {idea.Id} became task {idea.PromotedTaskId}, so discarding the idea would close "
                + $"nothing. Walk away from the work instead: h9k task abandon {idea.PromotedTaskId} --reason \"<why>\"");
        }

        RequireCaptured(idea, "discard");

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                $"Discarding records why: h9k idea discard {idea.Id} --reason \"<why this is not worth pursuing>\". "
                + "The idea is kept either way — a discarded idea that keeps coming back is a signal, and "
                + "a discard with no reason throws that signal away.");
        }

        return new IdeaDiscarded(idea.Id, reason.Trim(), discardedAt, discardedByOwnerId);
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
            var state when state == IdeaState.Promoted => new DomainConflictException(
                $"Idea {idea.Id} is promoted — it became task {idea.PromotedTaskId}, and the draft is what "
                + $"moves now: h9k task show {idea.PromotedTaskId}."),
            var state when state == IdeaState.Discarded => new DomainConflictException(
                $"Idea {idea.Id} was discarded ({idea.DiscardReason}), so there is nothing to {verb}. "
                + "It stays on the record; capture a fresh idea if the thought has come back: h9k idea add \"…\""),
            _ => new DomainNotFoundException($"Idea {idea.Id} has no captured state to {verb}."),
        };
    }
}
