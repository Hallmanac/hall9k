namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// Every bound on task notes, in one place and as constants. They are not <c>h9k config</c>
/// settings the way the lesson caps are: a project's lesson inventory grows with its age, and a
/// task's notes do not. The prompt numbers match the lesson defaults because that bound is already
/// paid for in every dispatched prompt.
/// </summary>
public static class TaskNoteLimits
{
    /// <summary>The longest note text <c>h9k task note</c> accepts, and the length a closing-block bullet is cut to.</summary>
    public const int MaxTextLength = 1000;

    /// <summary>The most notes one closing block records; later bullets are ignored.</summary>
    public const int MaxClosingBlockNotes = 5;

    /// <summary>The most notes one prompt's section carries.</summary>
    public const int MaxPromptNotes = 15;

    /// <summary>The most characters of note text one prompt's section carries.</summary>
    public const int MaxPromptCharacters = 4000;

    /// <summary>What a cut closing-block bullet ends with, so a reader knows it was cut.</summary>
    public const string CutSuffix = " [cut: over 1,000 characters]";
}
