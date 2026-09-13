namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// One task cut from an idea through the ordinary add door (<c>h9k task add --from-idea</c>,
/// backlog 31). Repeatable, not terminal: an idea fans out into any number of these, and
/// cutting one never ends the idea's own story, because discovery may keep producing more of
/// them (Brian, 2026-08-21). Provenance runs both ways: this names the task the cut produced,
/// and that task's own <c>TaskAdded</c> names this idea as its source.
/// <para>
/// Objective is recorded here, alongside the task's own copy, because it is what the cut
/// decided at the moment it happened — several tasks fanned out from one idea cannot share its
/// first sentence, so each cut supplies its own. A later revision of the task's objective does
/// not reach back to rewrite this: this is what was typed at cut time, the same way
/// <see cref="IdeaPromoted"/>'s own Objective always was.
/// </para>
/// </summary>
public sealed record IdeaTaskCut(
    Guid Id,
    Guid TaskId,
    string Objective,
    DateTimeOffset CutAt,
    Guid CutByOwnerId);
