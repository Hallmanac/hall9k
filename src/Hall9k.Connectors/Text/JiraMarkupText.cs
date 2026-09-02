using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Text;

/// <summary>
/// Markdown converted to Jira's own wiki markup — the notation Jira Cloud's v2 API renders a
/// plain-string description or comment body through, which shares almost none of CommonMark's
/// syntax. Composed markdown reached Jira through this exact conversion under the old twg
/// transport too: twg's own <c>--description-format</c>/<c>--body-format</c> flags told the
/// Atlassian CLI to do it on hall9k's behalf. The REST client that replaced it (Decisions Log
/// #114) has to do the conversion itself now that nothing between hall9k and Jira understands
/// markdown — without it, a card composed with headings and bullets (this repo's own
/// story-authoring skill among them) rendered with literal <c>##</c>/<c>-</c>/<c>**</c> characters
/// (independent pre-PR review, cycle 1).
/// <para>
/// Deliberately not a full CommonMark implementation: this converts the constructs a composing
/// session's card-authoring skills actually produce (headings, bold, bullets, numbered lists,
/// links, inline code, fenced code blocks, blockquotes) rather than the whole spec — the same
/// proportionate scope <see cref="RelayedText"/> takes with relayed text generally. Markdown's own
/// underscore-italic (<c>_text_</c>) already reads identically in Jira wiki markup, so it needs no
/// conversion of its own.
/// </para>
/// </summary>
public static partial class JiraMarkupText
{
    /// <summary>
    /// <paramref name="text"/> rewritten from markdown into Jira wiki markup, block by block: a
    /// fenced code block's own contents are carried through untouched (renaming only the fence
    /// syntax itself), so nothing inside a code sample is mistaken for a heading or a link outside
    /// one.
    /// </summary>
    public static string FromMarkdown(string text)
    {
        if (text.IsBlank())
        {
            return text;
        }

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        StringBuilder result = new(normalized.Length);
        int cursor = 0;
        foreach (Match fence in FencedCodeBlock().Matches(normalized))
        {
            result.Append(ConvertProse(normalized[cursor..fence.Index]));
            string language = fence.Groups[1].Value;
            string body = fence.Groups[2].Value;
            result.Append(language.IsNotBlank() ? $"{{code:{language}}}\n{body}\n{{code}}" : $"{{code}}\n{body}\n{{code}}");
            cursor = fence.Index + fence.Length;
        }

        result.Append(ConvertProse(normalized[cursor..]));
        return result.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> as Jira wiki markup that renders exactly as written, with none of its
    /// characters interpreted as markup — the counterpart to <see cref="FromMarkdown"/> for a
    /// payload composed with <c>format: "plain"</c>. Jira's v2 API carries every description or
    /// comment body as a single wiki-markup string regardless of the format a payload named
    /// (Decisions Log #114): under the retired twg transport, <c>--description-format
    /// plain</c>/<c>--body-format plain</c> told Atlassian's own CLI to carry a "plain" payload's
    /// text past its wiki-markup parser untouched, and nothing on this side of the swap does that
    /// anymore, so "plain" text reaching Jira unconverted would have Jira's own renderer interpret
    /// it as markup instead (independent pre-PR review, adversarial lens, cycle 1). Wrapped in
    /// Jira's documented <c>{noformat}</c> macro, the one wiki-markup construct guaranteed to
    /// suppress every other construct inside it (headings, lists, links, bold, code), rather than a
    /// per-character escape this class would otherwise have to guess at — AGENTS.md's "never guess
    /// at unobserved facts": Jira's backslash-escape rules for individual markup characters are not
    /// documented clearly enough to trust, where <c>{noformat}</c> is.
    /// <para>
    /// Wrapping is conditional, not unconditional (independent pre-PR review, adversarial lens,
    /// cycle 2, origin: closeout's own merge comment, composed with no wiki-markup-active character
    /// anywhere in it, was boxed the same as anything else, which turned its one actionable element
    /// — the pull request's URL — from an auto-linked sentence into dead text inside a preformatted
    /// block): <paramref name="text"/> is wrapped only when it actually contains a Jira wiki-markup
    /// construct (see <see cref="ContainsWikiMarkupConstruct"/>), not merely a character one of those
    /// constructs happens to use — a plain occurrence of a construct's character is not itself the
    /// construct (independent pre-PR review, adversarial lens, cycle 3, origin: the cycle-2 version
    /// keyed on bare character membership, so any hyphen — a task id's GUID, an ordinary hyphenated
    /// word — tripped the same box the merge comment was carved out to avoid, while headings
    /// (<c>h1.</c>), blockquotes (<c>bq.</c>), citations, image embeds, and Jira's own documented
    /// emoticons used no character the old class even listed). A text this construct check does not
    /// match cannot be interpreted as markup, so passing it through unwrapped is a verified fact
    /// about the text rather than a guess at Jira's undocumented escaping rules — and it is what lets
    /// a bare URL, or an ordinary sentence with a hyphen in it, still read and auto-link the way
    /// "plain" text always rendered before this wrapping existed.
    /// </para>
    /// The one corner this does not cover: text that already contains the literal string
    /// <c>{noformat}</c> could still terminate the block early — flagged here rather than guessed
    /// past, since handling it would mean splitting the payload on an assumption about Jira's own
    /// parser this class has no way to verify.
    /// </summary>
    public static string ToPlainLiteral(string text) =>
        text.IsBlank() || !ContainsWikiMarkupConstruct(text)
            ? text
            : $"{{noformat}}\n{text}\n{{noformat}}";

    /// <summary>
    /// Whether <paramref name="text"/> contains a real Jira wiki-markup construct. Bold, list
    /// markers, table rows, links, monospace, and macros like <c>{noformat}</c> itself are each
    /// built from a character no ordinary sentence needs, so any bare occurrence of one is still
    /// trusted as the construct (the cycle-1/2 rule, unchanged). Line-anchored headings
    /// (<c>h1.</c>-<c>h6.</c>) and blockquotes (<c>bq.</c>), image embeds (<c>!file.png!</c>),
    /// citations (<c>??...??</c>), and Jira's documented emoticons (<c>:)</c>, <c>(y)</c>,
    /// <c>(i)</c>, and the rest of that fixed, published set — never guessed at, since an
    /// undocumented emoticon would be exactly the kind of unobserved fact AGENTS.md rules out
    /// assuming) are new as of cycle 3, matched by their own literal shape rather than any single
    /// character. The hyphen (strikethrough), underscore (italic), plus (underline), caret
    /// (superscript), and tilde (subscript) are each a *paired* construct — Jira only assigns them
    /// meaning as an opening/closing pair wrapped around content — so none of them is trusted as a
    /// bare occurrence: the hyphen is the one cycle 3 found tripping on a task GUID and on ordinary
    /// hyphenated prose (<c>one-off</c>), and cycle 5 found the same shape true of the other four
    /// (a GitHub org/repo like <c>my_org/my_repo</c> tripping the underscore, for the identical
    /// reason). Each counts only when it also has the paired construct's real shape — preceded by
    /// start-of-text, whitespace, or an opening paren, immediately followed by a non-space
    /// character, and closed the mirror way — the word-boundary flanking a genuine
    /// <c>-strikethrough-</c>/<c>_italic_</c>/<c>+underline+</c>/<c>^superscript^</c>/<c>~subscript~</c>
    /// has and a mid-word, digit-flanked, or otherwise unpaired mark never does.
    /// </summary>
    private static bool ContainsWikiMarkupConstruct(string text) => WikiMarkupConstruct().IsMatch(text);

    private static string ConvertProse(string text)
    {
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            lines[index] = ConvertLine(lines[index]);
        }

        return string.Join('\n', lines);
    }

    private static string ConvertLine(string line)
    {
        Match heading = HeadingLine().Match(line);
        if (heading.Success)
        {
            return $"h{heading.Groups[1].Length}. {ConvertInline(heading.Groups[2].Value)}";
        }

        Match bullet = BulletLine().Match(line);
        if (bullet.Success)
        {
            return $"{new string('*', Depth(bullet.Groups[1].Value))} {ConvertInline(bullet.Groups[2].Value)}";
        }

        Match numbered = NumberedLine().Match(line);
        if (numbered.Success)
        {
            return $"{new string('#', Depth(numbered.Groups[1].Value))} {ConvertInline(numbered.Groups[2].Value)}";
        }

        Match quote = BlockquoteLine().Match(line);
        return quote.Success
            ? $"bq. {ConvertInline(quote.Groups[1].Value)}"
            : ConvertInline(line);
    }

    /// <summary>List nesting depth from a line's leading whitespace: every two spaces is one more level, floored at one.</summary>
    private static int Depth(string leadingWhitespace) => (leadingWhitespace.Length / 2) + 1;

    /// <summary>
    /// Inline code, bold, and links, converted in one left-to-right scan rather than three
    /// independent regex passes — a hand-written scanner rather than a regex composition because the
    /// two things a regex composition cannot both give at once turned out to both matter: markdown
    /// written literally inside a backtick span (<c>`**bold**`</c>) must not be read as bold
    /// (independent pre-PR review, adversarial lens, cycle 1), and a bold span or a link that itself
    /// wraps a code span (<c>**Use the `--file` flag**</c>) must still convert as bold with the code
    /// span converted inside it (independent pre-PR review, adversarial lens, cycle 2 — the sequential-
    /// segments version this replaced split every such span at the code boundary and matched neither
    /// regex). A real markdown parser gets both by building an inline tree, where emphasis may legally
    /// contain a code span; this reaches the same result without one, by having the bold/link scan
    /// itself skip straight over any code span it encounters while hunting for its own closing
    /// delimiter — so a <c>**</c> or a <c>]</c>/<c>)</c> written literally inside a code span's own
    /// content is never mistaken for a delimiter closing something outside it — and by recursively
    /// converting whatever bold/link content it does find, so a code span nested inside still becomes
    /// <c>{{...}}</c>.
    /// </summary>
    private static string ConvertInline(string text)
    {
        StringBuilder result = new(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] == '`' && TryReadCodeSpan(text, index, out int codeEnd))
            {
                result.Append("{{").Append(text[(index + 1)..(codeEnd - 1)]).Append("}}");
                index = codeEnd;
                continue;
            }

            if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '*'
                && TryFindDelimiter(text, index + 2, "**", out int boldClose))
            {
                result.Append('*').Append(ConvertInline(text[(index + 2)..boldClose])).Append('*');
                index = boldClose + 2;
                continue;
            }

            if (text[index] == '[' && TryConvertLink(text, index, out string linkOutput, out int linkEnd))
            {
                result.Append(linkOutput);
                index = linkEnd;
                continue;
            }

            result.Append(text[index]);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// <c>[link text](url)</c> starting at <paramref name="start"/> (a <c>[</c>), converted to Jira's
    /// pipe-delimited link — or refused (returns false) the moment any required piece is missing, the
    /// same "no match, leave it alone" fallback the regex this replaced always had. The link text is
    /// scanned for its own closing <c>]</c> the same delimiter-skipping way <see cref="ConvertInline"/>
    /// scans for a closing <c>**</c>, so a link wrapping a code span (<c>[the `--file` flag](url)</c>)
    /// converts with the code span turned into <c>{{...}}</c> rather than left as raw backticks.
    /// </summary>
    private static bool TryConvertLink(string text, int start, out string output, out int end)
    {
        if (TryFindDelimiter(text, start + 1, "]", out int textClose)
            && textClose + 1 < text.Length && text[textClose + 1] == '('
            && TryFindDelimiter(text, textClose + 2, ")", out int urlClose))
        {
            string linkText = text[(start + 1)..textClose];
            string url = text[(textClose + 2)..urlClose];
            output = $"[{ConvertInline(linkText)}|{url}]";
            end = urlClose + 1;
            return true;
        }

        output = string.Empty;
        end = start;
        return false;
    }

    /// <summary>
    /// The index of the next literal occurrence of <paramref name="delimiter"/> at or after
    /// <paramref name="from"/>, skipping straight over every complete inline-code span found along the
    /// way (<see cref="TryReadCodeSpan"/>) so a delimiter character written literally inside one — a
    /// stray <c>**</c>, <c>]</c>, or <c>)</c> in a code sample — is never mistaken for the delimiter
    /// this search is actually looking for.
    /// </summary>
    private static bool TryFindDelimiter(string text, int from, string delimiter, out int index)
    {
        int position = from;
        while (position < text.Length)
        {
            if (text[position] == '`' && TryReadCodeSpan(text, position, out int codeEnd))
            {
                position = codeEnd;
                continue;
            }

            if (position + delimiter.Length <= text.Length
                && string.CompareOrdinal(text, position, delimiter, 0, delimiter.Length) == 0)
            {
                index = position;
                return true;
            }

            position++;
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// A complete inline-code span starting at <paramref name="start"/> (a backtick): at least one
    /// character, no backtick, no newline — the same literal-content rule the regex this replaced
    /// enforced. <paramref name="end"/> is the index just past the closing backtick on success.
    /// </summary>
    private static bool TryReadCodeSpan(string text, int start, out int end)
    {
        int closing = start + 1;
        while (closing < text.Length && text[closing] != '`' && text[closing] != '\n')
        {
            closing++;
        }

        if (closing < text.Length && text[closing] == '`' && closing > start + 1)
        {
            end = closing + 1;
            return true;
        }

        end = start;
        return false;
    }

    [GeneratedRegex(@"```(\w*)\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex FencedCodeBlock();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^(\s*)\d+\.\s+(.*)$")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^>\s?(.*)$")]
    private static partial Regex BlockquoteLine();

    /// <summary>
    /// One alternation covering every construct <see cref="ContainsWikiMarkupConstruct"/>'s own doc
    /// comment enumerates. The single-character marks that are never paired — bold's <c>*</c>, list
    /// and table markers, links, monospace, macro braces — keep the cycle-1/2 rule of any bare
    /// occurrence counting, since none of them was found to over-trigger. The five *paired* marks
    /// (hyphen/strikethrough, underscore/italic, plus/underline, caret/superscript,
    /// tilde/subscript) each get the same real-construct-shape test instead: a mark preceded by
    /// start-of-text, whitespace, or an opening paren, immediately followed by a non-space
    /// character, and closed by the identical mark the mirror way — the word-boundary flanking a
    /// genuine paired span has and a mid-word, digit-flanked, or singly-occurring mark never does.
    /// The hyphen is the one cycle 3 found tripping on a task GUID and on ordinary hyphenated prose
    /// (<c>one-off</c>); cycle 5 found the same true of the other four (a GitHub org/repo like
    /// <c>my_org/my_repo</c> tripping the underscore).
    /// </summary>
    [GeneratedRegex(
        @"[*#{}\[\]|\\]" +
        @"|(?<![\w-])-(?=\S)(?:[^\r\n]*?\S)?-(?![\w-])" +
        @"|(?<!\w)_(?=\S)(?:[^\r\n]*?\S)?_(?!\w)" +
        @"|(?<![\w+])\+(?=\S)(?:[^\r\n]*?\S)?\+(?![\w+])" +
        @"|(?<![\w^])\^(?=\S)(?:[^\r\n]*?\S)?\^(?![\w^])" +
        @"|(?<![\w~])~(?=\S)(?:[^\r\n]*?\S)?~(?![\w~])" +
        @"|^h[1-6]\.(?:\s|$)" +
        @"|^bq\.(?:\s|$)" +
        @"|\?\?[^\r\n?]+\?\?" +
        @"|!\S[^\r\n!]*!" +
        @"|:\)|:\(|:P|:D|;\)|\(y\)|\(n\)|\(i\)|\(/\)|\(x\)|\(!\)|\(\+\)|\(-\)|\(\?\)|\(on\)|\(off\)|\(\*[rgby]?\)",
        RegexOptions.Multiline)]
    private static partial Regex WikiMarkupConstruct();
}
