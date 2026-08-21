using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Text;

/// <summary>
/// The rule for text Hall9k relays but did not author. Since adoption (PLAN.md §3.1a) an issue
/// title and body travel from whoever could file that issue into a terminal, a commit subject,
/// and a pull-request body, and in every one of those a character that acts rather than reads is
/// obeyed: an escape sequence repaints the rows above it, a lone carriage return overwrites the
/// line before it, and a bidirectional override reverses what a line appears to say without
/// changing a byte of what is stored.
/// <para>
/// It lives beside the seam that lets such text in rather than in either sink, because the CLI
/// (<c>ExternalText</c>) and the daemon (<c>PullRequestBody</c>) need the same answer to the same
/// question, cannot reference each other, and both reference this project. Two copies of a
/// character list are two lists that drift.
/// </para>
/// </summary>
public static partial class RelayedText
{
    /// <summary>
    /// Relayed text made safe to show, and otherwise left as close to what it said as safety
    /// allows. Tab and the line feeds survive because they are the layout of a Markdown body;
    /// every character the sink would obey instead of showing is dropped rather than escaped,
    /// because no issue body means one as content and rendering it as \u001b would be noise in
    /// its place.
    /// <para>
    /// A carriage return counts as layout only in front of a line feed, where it is half of a
    /// Windows line break. Alone it is not a line break at all: it returns the cursor to column
    /// zero, so relayed text can overwrite the line printed above it and hide what it replaced,
    /// which is the same attack as an escape sequence in slower motion. Lone ones are dropped.
    /// </para>
    /// </summary>
    public static string Printable(string text)
    {
        StringBuilder kept = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character is '\n' or '\t'
                || (character is '\r' && index + 1 < text.Length && text[index + 1] is '\n'))
            {
                kept.Append(character);
                continue;
            }

            // A character outside the BMP arrives as a pair of chars and is judged as the one
            // character it is, which is also the only way to see it at all: each half on its own
            // is a surrogate, a category that says nothing about what the pair means.
            if (char.IsHighSurrogate(character)
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                if (!ActsAsPair(new Rune(character, text[index + 1])))
                {
                    kept.Append(character).Append(text[index + 1]);
                }

                index++;
                continue;
            }

            if (!Acts(character))
            {
                kept.Append(character);
            }
        }

        return kept.ToString();
    }

    /// <summary>
    /// Printable text folded onto one line: the layout characters <see cref="Printable"/> keeps
    /// become spaces, because text free to emit a newline can print lines of its own choosing
    /// underneath the question a human is being asked, or a second paragraph underneath a commit
    /// subject, which is the same lie told without a single escape sequence.
    /// </summary>
    public static string OneLine(string text) =>
        Printable(text)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\t', ' ');

    /// <summary>
    /// Enough of the text for the reader, with an ellipsis where the rest was. The cut lands on a
    /// text-element boundary rather than a raw char index: anything outside the BMP is two chars,
    /// and an emoji sequence is several text elements' worth of chars joined by U+200D, so
    /// slicing at char <c>max</c> can leave half a surrogate pair, which renders as the
    /// replacement character. Adoption makes that ordinary rather than exotic, because these
    /// strings are now issue titles.
    /// </summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int budget = Math.Max(max - 1, 0);
        int cut = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (cut + element.Length > budget)
            {
                break;
            }

            cut += element.Length;
        }

        return text[..cut] + "…";
    }

    /// <summary>
    /// Relayed text with its closing keywords defused: a keyword followed by an issue reference
    /// (<c>#500</c>, <c>owner/repo#500</c>, <c>GH-500</c>, or a full issue or pull URL) has the
    /// reference wrapped in a code span, so GitHub reads "Closes `#500`" as words rather than as
    /// an instruction to close issue 500.
    /// <para>
    /// It has to be defused because since adoption (PLAN.md §3.1a) an objective, a title, or a
    /// summary can be quoting text written by anyone who can file an issue, and every one of
    /// those reaches GitHub: the pull request's title and body, and — because this repository
    /// merges fast-forward — the agent's own commit subjects, which land on the default branch
    /// carrying whatever the agent echoed from its task headline. Hall9k adopts and links, never
    /// moves an item's state (SLICE-1 S1-11), so nothing it merely relays may reach the issue
    /// tracker as a command.
    /// </para>
    /// <para>
    /// The reference is wrapped rather than deleted, because the text still says what its author
    /// said: GitHub does not link references inside code, so "Closes #500" survives as prose
    /// while losing its power over issue 500.
    /// </para>
    /// <para>
    /// What it is wrapped in is a run of backticks no other run in the text can pair with, which
    /// is not decoration. A single backtick would be the obvious wrapper and is wrong whenever
    /// the text already carries an unpaired one: "see `foo and closes #500" comes out as
    /// "see `foo and closes `#500`", and the renderer pairs the author's stray backtick with the
    /// one just inserted, so the prose in between becomes a code span and <c>#500</c> is left
    /// bare outside it — still defused, since the keyword went into the span, but now an
    /// autolinked cross-reference posting Hall9k's pull request onto issue 500's timeline, and a
    /// sentence mangled into code on the way. So the wrapper's length is the shortest that no
    /// unpaired run in the text shares, and normally that is one.
    /// </para>
    /// <para>
    /// The separator between the keyword and the reference is any run of colons and whitespace,
    /// which is wider than the shapes GitHub is documented to close on. That is deliberate: the
    /// exact parser is GitHub's and not ours to mirror from memory, "Closes:#500" and
    /// "Closes : #500" both read to a human as an instruction, and the cost of defusing a shape
    /// GitHub would have ignored is a pair of backticks nobody was going to reread.
    /// </para>
    /// </summary>
    public static string WithoutClosingKeywords(string text)
    {
        if (!ClosingKeyword().IsMatch(text))
        {
            return text;
        }

        (IReadOnlyList<(int Start, int End)> spans, IReadOnlySet<int> unpaired) = ScanBackticks(text);
        string wrapper = new('`', ShortestUnpairableRun(unpaired));
        return ClosingKeyword().Replace(text, match => IsInside(spans, match)
            ? match.Value
            : $"{match.Groups[1].Value}{match.Groups[2].Value}{wrapper}{match.Groups[3].Value}{wrapper}");
    }

    /// <summary>
    /// The shortest run length no unpaired run in the text uses, so the pair this method inserts
    /// can only close against itself: CommonMark pairs a run with the next run of exactly its own
    /// length, and every run the text already had is either inside a span it closed (so it is
    /// spoken for) or a length this one avoids.
    /// <para>
    /// The inserted pair cannot disturb a span that did pair, either, because a match overlapping
    /// a span is left alone (<see cref="IsInside"/>) — so nothing is ever inserted between an
    /// opener and the closer it found.
    /// </para>
    /// </summary>
    private static int ShortestUnpairableRun(IReadOnlySet<int> unpaired)
    {
        int length = 1;
        while (unpaired.Contains(length))
        {
            length++;
        }

        return length;
    }

    /// <summary>
    /// A keyword already inside a code span is already inert, and rewriting it produces the very
    /// leak the rewrite exists to prevent: a summary containing "`closes #12`" would come out as
    /// "`closes `#12``", where the original span now closes early, <c>#12</c> sits bare between
    /// two spans, and GitHub links — and closes — the reference the backticks were protecting.
    /// So a match overlapping a span is left exactly as its author wrote it.
    /// <para>
    /// Fenced blocks need no case of their own: a fence is a run of three backticks matched by
    /// the next run of three, which is what the span scanner already looks for. A fence shape it
    /// cannot pair (an info string with a backtick in it, an opener longer than its closer) falls
    /// back to defusing inside the fence, which costs a visible pair of backticks in a code
    /// sample and never costs an issue its state.
    /// </para>
    /// </summary>
    private static bool IsInside(IReadOnlyList<(int Start, int End)> spans, Match match) =>
        spans.Any(span => match.Index < span.End && span.Start < match.Index + match.Length);

    /// <summary>
    /// The code spans in the text, by CommonMark's rule: a run of backticks opens a span, and the
    /// next run of exactly the same length closes it. A run with no matching partner is literal
    /// text and opens nothing, which is why an unmatched run advances past itself rather than
    /// swallowing the rest of the document.
    /// <para>
    /// The lengths of those partnerless runs come back alongside the spans, because they are the
    /// ones still looking for a partner when the renderer reaches whatever this class inserts
    /// afterwards (<see cref="ShortestUnpairableRun"/>).
    /// </para>
    /// </summary>
    private static (List<(int Start, int End)> Spans, HashSet<int> UnpairedLengths) ScanBackticks(string text)
    {
        List<(int Start, int End)> spans = [];
        HashSet<int> unpaired = [];
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] is not '`')
            {
                index++;
                continue;
            }

            int openEnd = RunEnd(text, index);
            int length = openEnd - index;
            int close = MatchingRun(text, openEnd, length);
            if (close < 0)
            {
                unpaired.Add(length);
                index = openEnd;
                continue;
            }

            spans.Add((index, close + length));
            index = close + length;
        }

        return (spans, unpaired);
    }

    /// <summary>Where the run of backticks starting at <paramref name="start"/> ends.</summary>
    private static int RunEnd(string text, int start)
    {
        int end = start;
        while (end < text.Length && text[end] is '`')
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// Where the next run of exactly <paramref name="length"/> backticks starts, or -1 when the
    /// opening run has no partner. A longer run does not close a shorter one, so it is skipped
    /// whole rather than matched at its first character.
    /// </summary>
    private static int MatchingRun(string text, int from, int length)
    {
        int index = from;
        while (index < text.Length)
        {
            index = text.IndexOf('`', index);
            if (index < 0)
            {
                return -1;
            }

            int end = RunEnd(text, index);
            if (end - index == length)
            {
                return index;
            }

            index = end;
        }

        return -1;
    }

    [GeneratedRegex(
        @"\b(close[sd]?|fix(?:e[sd])?|resolve[sd]?)([:\s]+)((?:[\w.-]+/[\w.-]+)?#\d+|GH-\d+|https?://\S+/(?:issues|pull)/\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClosingKeyword();

    /// <summary>
    /// A character the sink obeys instead of showing: the control characters (Cc), where the
    /// escape sequences live, and the format characters (Cf) whose purpose is to change how the
    /// text around them is laid out.
    /// <para>
    /// The second group is named one character at a time rather than taken as the category, and
    /// that is the correction of a real overreach rather than fussiness. Cf also holds U+200D
    /// ZERO WIDTH JOINER and U+200C ZERO WIDTH NON-JOINER, and those are content: the joiner is
    /// what makes a multi-person or professioned emoji one glyph, so dropping the category turned
    /// an issue titled "Add (technologist emoji) avatar support" into two unrelated glyphs, and
    /// the non-joiner is orthographically required in Persian. Text that is merely relayed is
    /// still text, and mangling it silently at the moment it is displayed is its own kind of lie.
    /// </para>
    /// </summary>
    private static bool Acts(char character) =>
        char.IsControl(character) || IsLayoutOverride(character);

    /// <summary>
    /// The format characters that reorder or hide what surrounds them. U+202E RIGHT-TO-LEFT
    /// OVERRIDE and the isolates U+2066-U+2069 reverse the visual order of the text after them
    /// while leaving the stored string untouched, so an issue title carrying one can read on
    /// screen as the opposite of what it says: the human at the adoption prompt approves one
    /// objective and the draft records another. The annotation characters do the same job by
    /// marking a run of text as an overlay for another.
    /// </summary>
    private static bool IsLayoutOverride(char character) => character is
        '\u061C'                            // ARABIC LETTER MARK
        or '\u200E' or '\u200F'             // LEFT-TO-RIGHT MARK, RIGHT-TO-LEFT MARK
        or (>= '\u202A' and <= '\u202E')    // the embeddings, their pop, and the two overrides
        or (>= '\u2066' and <= '\u2069')    // the isolates and their pop
        or (>= '\u206A' and <= '\u206F')    // the deprecated format characters
        or '\uFEFF'                         // ZERO WIDTH NO-BREAK SPACE: a byte-order mark adrift
        or (>= '\uFFF9' and <= '\uFFFB');   // interlinear annotation: text that hides other text

    /// <summary>
    /// Outside the BMP the target is one block: U+E0000, which spells out ASCII in characters
    /// that render as nothing at all. That is how a title carries a second, invisible sentence
    /// past the human reading the first one, and it is the whole of what astral text can do here.
    /// <para>
    /// Named as a block rather than taken as the Format category, for exactly the reason the BMP
    /// list is named one character at a time: astral Cf is mostly content. U+110BD KAITHI NUMBER
    /// SIGN marks a numeral, U+13430-U+1343F are the quadrat controls that lay Egyptian
    /// hieroglyphs out on the page, and U+1D173-U+1D17A are the beams, ties and phrases of
    /// musical notation. Taking the category would silently mangle every one of them, which is
    /// the same overcorrection as the zero width joiner this file already carries the scar of.
    /// </para>
    /// <para>
    /// The block does have one content use, and it is worth naming rather than hiding: a
    /// subdivision flag (the England, Scotland and Wales emoji) is a black flag followed by tag
    /// letters. Dropping them leaves a plain black flag, so that text degrades into a visibly
    /// different glyph rather than into a hidden sentence — which is the one direction this
    /// trade is allowed to fail in.
    /// </para>
    /// </summary>
    private static bool ActsAsPair(Rune rune) => rune.Value is >= 0xE0000 and <= 0xE007F;
}
