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
    /// documented clearly enough to trust, where <c>{noformat}</c> is. The one corner this does not
    /// cover: text that already contains the literal string <c>{noformat}</c> could still terminate
    /// the block early — flagged here rather than guessed past, since handling it would mean
    /// splitting the payload on an assumption about Jira's own parser this class has no way to
    /// verify.
    /// </summary>
    public static string ToPlainLiteral(string text) =>
        text.IsBlank() ? text : $"{{noformat}}\n{text}\n{{noformat}}";

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
    /// Inline code has to be carved out and left untouched by bold/link conversion, not converted
    /// in the same pass as them: converting bold first and code second let markdown syntax written
    /// literally inside a backtick span (<c>`**bold**`</c>) get rewritten before the code span ever
    /// wrapped it, so the card showed a monospace span containing bold text instead of the literal
    /// characters the author asked for (independent pre-PR review, adversarial lens, cycle 1 — a
    /// ride-along, the same leak <see cref="FromMarkdown"/>'s own fenced-code handling already
    /// avoids for block code). Walks the line the same way <see cref="FromMarkdown"/> walks the
    /// whole text around its fenced code blocks: bold/link conversion runs only on the spans between
    /// inline-code matches, and each code span itself is wrapped as <c>{{...}}</c> straight from its
    /// own captured text, never re-examined by <see cref="BoldText"/> or <see cref="InlineLink"/>.
    /// </summary>
    private static string ConvertInline(string text)
    {
        StringBuilder result = new(text.Length);
        int cursor = 0;
        foreach (Match code in InlineCode().Matches(text))
        {
            result.Append(ConvertBoldAndLinks(text[cursor..code.Index]));
            result.Append("{{").Append(code.Groups[1].Value).Append("}}");
            cursor = code.Index + code.Length;
        }

        result.Append(ConvertBoldAndLinks(text[cursor..]));
        return result.ToString();
    }

    private static string ConvertBoldAndLinks(string text) =>
        InlineLink().Replace(BoldText().Replace(text, "*$1*"), "[$1|$2]");

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

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldText();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineLink();
}
