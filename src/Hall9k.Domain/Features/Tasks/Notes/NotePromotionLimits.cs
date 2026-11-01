namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// Every bound on the closeout step that promotes a task's notes to lessons, as constants. They are
/// not <c>h9k config</c> settings: the brief makes a lesson live at once, so the ceiling is what
/// bounds the damage a bad promotion can do, and a knob that raised it would raise that damage with
/// it. An off switch or a higher ceiling stays additive.
/// </summary>
public static class NotePromotionLimits
{
    /// <summary>The most notes the step is shown, newest first; each note past it is dropped as not offered.</summary>
    public const int MaxOffered = 30;

    /// <summary>
    /// The most lessons one task promotes. One, because a promoted lesson reaches every later prompt
    /// and about six lessons fit one; a note not kept stays recoverable on the task. It is what the
    /// prompt tells the session, but the ceiling itself is the shape of <see cref="NotePromotionVerdict"/>,
    /// which carries one kept note: raising this means widening the verdict to carry a list.
    /// </summary>
    public const int MaxKept = 1;

    /// <summary>
    /// The longest claim the step accepts, near the 75th percentile of the project's lessons. A claim
    /// over it is dropped rather than cut, because a cut claim is unreadable forever.
    /// </summary>
    public const int MaxClaimLength = 800;
}
