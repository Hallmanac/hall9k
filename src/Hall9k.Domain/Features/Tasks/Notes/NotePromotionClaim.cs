using System.Globalization;
using System.Text;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// The two checks a promotion claim has to pass before it can become a live lesson, beyond its length.
/// A claim is text a tool-less session wrote after reading input nobody vetted, and it goes live as an
/// instruction in every later prompt, so the daemon holds it to the note it cites rather than trusting
/// the citation. Pure, so both are provable without a store.
/// </summary>
public static class NotePromotionClaim
{
    /// <summary>
    /// Whether every word of <paramref name="claim"/> occurs in <paramref name="noteText"/>, compared
    /// without regard to case. A word is a run of anything that is not whitespace or punctuation, so
    /// punctuation and spacing are the session's to choose and the words are not. Only ASCII symbols and
    /// Unicode punctuation and separators split words: a letter outside the Basic Multilingual Plane, a
    /// letter-shaped symbol such as a circled letter, and any other character that is not plainly
    /// punctuation count as word content, so a look-alike spelling of a command cannot slip through as
    /// "free" punctuation. The claim may therefore cut, reorder and join what the note says but cannot
    /// introduce a command, a name or a path the note did not hold. A claim with no word at all is not
    /// grounded in anything.
    /// </summary>
    public static bool IsGroundedIn(string claim, string noteText)
    {
        HashSet<string> claimWords = Words(claim);
        return claimWords.Count > 0 && claimWords.IsSubsetOf(Words(noteText));
    }

    /// <summary>
    /// Whether <paramref name="claim"/> holds only characters a person reading it would see: no control,
    /// format (which includes the right-to-left overrides and the invisible tag block), private-use or
    /// unassigned character, no line or paragraph separator (the claim is one line, and a separator
    /// would become a line break when it is rendered), no variation selector (a non-spacing mark that
    /// draws nothing of its own, so a run of them can carry a payload after a visible word), and no
    /// malformed surrogate. The text a person retires a bad lesson on has to be the text later
    /// sessions read.
    /// </summary>
    public static bool IsPrintable(string claim)
    {
        foreach (Rune rune in claim.EnumerateRunes())
        {
            if (rune.Value == Rune.ReplacementChar.Value
                || IsVariationSelector(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVariationSelector(Rune rune) =>
        rune.Value is >= 0x180B and <= 0x180D
            or 0x180F
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xE0100 and <= 0xE01EF;

    private static bool IsWordContent(Rune rune) =>
        rune.IsAscii
            ? Rune.IsLetterOrDigit(rune)
            : !Rune.IsWhiteSpace(rune)
                && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.ConnectorPunctuation
                    or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OpenPunctuation
                    or UnicodeCategory.ClosePunctuation
                    or UnicodeCategory.InitialQuotePunctuation
                    or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.OtherPunctuation
                    or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator);

    private static HashSet<string> Words(string text)
    {
        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder word = new();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (IsWordContent(rune))
            {
                word.Append(rune.ToString());
                continue;
            }

            Flush();
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length == 0)
            {
                return;
            }

            words.Add(word.ToString());
            word.Clear();
        }
    }
}
