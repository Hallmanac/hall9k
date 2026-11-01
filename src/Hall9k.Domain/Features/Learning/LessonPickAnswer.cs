using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// What the daemon made of a picker session's answer, which it reads as lesson ids and nothing else.
/// </summary>
/// <param name="Usable">False when the answer held anything other than ids, or no id at all.</param>
/// <param name="LessonIds">The offered lessons the answer named, in the answer's order, each once.</param>
/// <param name="DroppedIdCount">How many ids named no offered lesson, named more than one, or repeated one already read.</param>
public sealed record LessonPickAnswerReading(bool Usable, IReadOnlyList<Guid> LessonIds, int DroppedIdCount)
{
    public static readonly LessonPickAnswerReading NotUsable = new(false, [], 0);
}

/// <summary>
/// Reads a picker session's answer. Strict on purpose: the answer is split on whitespace and
/// commas, and every piece must be an eight-character short id as <see cref="InjectedLesson.ShortId"/>
/// prints it. A piece that is anything else (a sentence, a bracketed id, a heading, a reason) makes
/// the whole answer unusable, because a chatty answer is one the session did not follow the prompt
/// for, and shadow is where that rate becomes visible. The answer text is parsed and then dropped:
/// nothing the session wrote is kept or relayed.
/// </summary>
public static partial class LessonPickAnswer
{
    [GeneratedRegex("^[0-9a-fA-F]{8}$")]
    private static partial Regex ShortIdShape();

    /// <summary>
    /// Reads <paramref name="answer"/> against <paramref name="offered"/>, the lessons the picker was
    /// shown. An id is dropped and counted when no offered lesson ends in it, when two do (the short id
    /// then names no single lesson), or when it repeats one already read.
    /// </summary>
    public static LessonPickAnswerReading Read(string? answer, IReadOnlyList<InjectedLesson> offered)
    {
        string[] pieces = (answer ?? string.Empty).Split(
            [' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0 || pieces.Any(piece => !ShortIdShape().IsMatch(piece)))
        {
            return LessonPickAnswerReading.NotUsable;
        }

        ILookup<string, InjectedLesson> byShortId = offered.ToLookup(lesson => lesson.ShortId, StringComparer.OrdinalIgnoreCase);
        List<Guid> read = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int dropped = 0;
        foreach (string piece in pieces)
        {
            InjectedLesson[] matches = [.. byShortId[piece]];
            if (!seen.Add(piece) || matches.Length != 1)
            {
                dropped++;
                continue;
            }

            read.Add(matches[0].Id);
        }

        return new LessonPickAnswerReading(true, read, dropped);
    }
}
