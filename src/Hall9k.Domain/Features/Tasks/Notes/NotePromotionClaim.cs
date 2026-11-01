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
    /// without regard to case. A word is a run of letters and digits, so punctuation and spacing are the
    /// session's to choose and the words are not. The claim may therefore cut, reorder and join what the
    /// note says but cannot introduce a command, a name or a path the note did not hold. A claim with no
    /// word at all is not grounded in anything.
    /// </summary>
    public static bool IsGroundedIn(string claim, string noteText)
    {
        HashSet<string> claimWords = Words(claim);
        return claimWords.Count > 0 && claimWords.IsSubsetOf(Words(noteText));
    }

    /// <summary>
    /// Whether <paramref name="claim"/> holds only characters a person reading it would see: no control,
    /// format (which includes the right-to-left overrides and the invisible tag block), private-use or
    /// unassigned character, and no malformed surrogate. The text a person retires a bad lesson on has to
    /// be the text later sessions read.
    /// </summary>
    public static bool IsPrintable(string claim)
    {
        foreach (Rune rune in claim.EnumerateRunes())
        {
            if (rune.Value == Rune.ReplacementChar.Value
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned)
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> Words(string text)
    {
        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder word = new();
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
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
