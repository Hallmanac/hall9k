namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// The two numbers that bound how much of a project's recorded lessons reach a dispatched
/// session's prompt (idea d805fd8b, piece 5; backlog 55): how many lessons, and how many
/// characters of lesson text. Both, not one, because they fail differently: thirty short lessons
/// and three essays are each a prompt nobody reads, and a count alone lets a single lesson grow
/// without limit while a character budget alone lets forty one-liners crowd out the task.
/// <para>
/// Settings rather than constants because the right answer is per install: a project six months
/// into a learnings loop has a different lesson inventory than one that started yesterday, and
/// the operator paying for the tokens is the one who should decide. <c>h9k config set
/// --lesson-prompt-max-lessons/--lesson-prompt-max-characters</c> writes them, <c>h9k config
/// show</c> reports them, and every prompt composition reads the file fresh so a change takes
/// effect on the next dispatch rather than at the next daemon restart.
/// </para>
/// </summary>
public sealed record LessonInjectionCaps(int MaxLessons, int MaxCharacters)
{
    /// <summary>
    /// Fifteen lessons, which is what the section is worth reading at: past roughly this many a
    /// reader skims, and the whole design leans on retirement and distillation to keep the live
    /// set small rather than on a generous cap to accommodate a large one.
    /// </summary>
    public const int DefaultMaxLessons = 15;

    /// <summary>
    /// Four thousand characters, roughly a thousand tokens: a real cost on every single
    /// dispatched session, and still room for fifteen substantial lessons at the length
    /// <c>LearningDecider</c>'s own "one claim" guidance produces.
    /// </summary>
    public const int DefaultMaxCharacters = 4000;

    /// <summary>
    /// The count ceiling <see cref="Resolve"/> clamps a configured value down to. The CLI's own
    /// write path validates before writing, but a hand-edited config file skips that gate
    /// entirely, and an unbounded count here is an unbounded prompt, the one thing this type
    /// exists to prevent.
    /// </summary>
    public const int MaxConfigurableLessons = 200;

    /// <summary>The character ceiling, on the same reasoning as <see cref="MaxConfigurableLessons"/>.</summary>
    public const int MaxConfigurableCharacters = 100_000;

    /// <summary>The caps a node that has configured neither gets.</summary>
    public static readonly LessonInjectionCaps Default = new(DefaultMaxLessons, DefaultMaxCharacters);

    /// <summary>
    /// The caps as they actually apply, from whatever the config file carried. Null defers to the
    /// shipped default; anything outside the range is clamped rather than refused, because this
    /// runs on the dispatch path and a nonsense number in a config file must not be able to stop
    /// a run from starting. <c>h9k config show</c> is where an operator sees a clamp happened.
    /// </summary>
    public static LessonInjectionCaps Resolve(int? maxLessons, int? maxCharacters) =>
        new(Math.Clamp(maxLessons ?? DefaultMaxLessons, 1, MaxConfigurableLessons),
            Math.Clamp(maxCharacters ?? DefaultMaxCharacters, 1, MaxConfigurableCharacters));
}
