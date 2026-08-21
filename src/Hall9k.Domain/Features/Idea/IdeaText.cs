namespace Hall9k.Domain.Features.Idea;

/// <summary>What promotion seeds the draft with: an objective, and whatever else the note said.</summary>
public sealed record IdeaSeed(string Objective, string? Context);

/// <summary>
/// Turns an idea's note into a draft's opening text. The split is <b>mechanical and visible</b>
/// (Decisions Log #35): the first sentence becomes the objective and the rest becomes context,
/// with no attempt to understand either. Promotion prints exactly what it took, and
/// <c>--objective</c> overrides it — the platform never infers what an idea was "really" about.
/// </summary>
public static class IdeaText
{
    /// <summary>
    /// The first sentence is whatever comes before the first sentence terminator followed by
    /// whitespace or end-of-text, or before the first line break — whichever lands first. The
    /// terminator is kept, because the sentence is quoted back as it was written.
    /// <para>
    /// Known and accepted: a note opening with "e.g." or "v1.5 of the CLI" splits early. Making
    /// that smarter would mean guessing, and the human is right there with --objective.
    /// </para>
    /// </summary>
    public static IdeaSeed Seed(string text)
    {
        string note = (text ?? string.Empty).ReplaceLineEndings("\n").Trim();
        int boundary = SentenceEnd(note);

        return boundary < 0
            ? new IdeaSeed(note, null)
            : new IdeaSeed(note[..boundary].Trim(), Remainder(note[boundary..]));
    }

    /// <summary>The index one past the first sentence, or -1 when the note is a single sentence.</summary>
    private static int SentenceEnd(string note)
    {
        for (int index = 0; index < note.Length; index++)
        {
            bool terminated = note[index] is '.' or '!' or '?'
                && (index + 1 == note.Length || char.IsWhiteSpace(note[index + 1]));
            if (terminated)
            {
                return index + 1;
            }

            if (note[index] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static string? Remainder(string rest) => rest.Trim() is { Length: > 0 } remainder ? remainder : null;
}
