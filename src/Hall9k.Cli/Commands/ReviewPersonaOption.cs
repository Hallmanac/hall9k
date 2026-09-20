using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The <c>--persona</c> / <c>--clear-personas</c> pair's own option shape for
/// <c>h9k owner set</c>, kept beside <see cref="VoiceSkillOption"/> and out of the command's own
/// <c>ExecuteAsync</c> so the refusals are readable, and testable, without a database.
/// <para>
/// <c>--persona</c> is repeatable rather than comma-separated: a member holds a set, and a
/// repeated option is the shape a shell quotes correctly without anyone having to think about it.
/// The clearing switch is its own option for the reason <see cref="VoiceSkillOption"/> states —
/// a "none" word inside the value vocabulary is a word the vocabulary could later want.
/// </para>
/// </summary>
internal static class ReviewPersonaOption
{
    /// <summary>
    /// What the command records: <see cref="Optional{T}.None"/> when neither option was passed
    /// (leave the declaration alone), an empty list for <c>--clear-personas</c> (an explicit
    /// "declare none", which reads as the engineer's review), and the parsed set otherwise.
    /// </summary>
    public static Optional<IReadOnlyList<ReviewPersona>> Resolve(string[]? personas, bool clear)
    {
        bool named = personas is { Length: > 0 };
        if (named && clear)
        {
            throw new DomainValidationException(
                "--persona and --clear-personas ask for opposite things. Pass one: the persona (or "
                + "personas) to declare them, --clear-personas to declare none, which reads as the "
                + "engineer's review.");
        }

        if (!named)
        {
            return clear
                ? Optional<IReadOnlyList<ReviewPersona>>.Of([])
                : Optional<IReadOnlyList<ReviewPersona>>.None;
        }

        // Parse, not the tolerant Read: a persona nobody could read is refused here with the word
        // the human actually typed, rather than dropped into a set that silently holds one fewer
        // review than they asked for.
        return Optional<IReadOnlyList<ReviewPersona>>.Of(
            ReviewPersona.Declared([.. personas!.Select(ReviewPersona.Parse)]));
    }

    /// <summary>How the declaration reads in <c>h9k owner show</c>'s pane.</summary>
    public static string Describe(IReadOnlyList<ReviewPersona>? personas)
    {
        IReadOnlyList<ReviewPersona> declared = ReviewPersona.Declared(personas);
        return declared.Count == 0
            ? "[dim]none declared — a pull request assigned here gets the engineer's review, the same "
              + "one it has always got[/]"
            : string.Join(", ", declared.Select(persona => persona.Value.EscapeMarkup()))
              + " [dim]— one review session per persona on the pull request's own pr-review task[/]";
    }
}
