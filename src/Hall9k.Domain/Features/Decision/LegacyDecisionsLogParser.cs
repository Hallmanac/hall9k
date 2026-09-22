using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// Reads PLAN.md's §16 v0 Decisions Log into the entries the one-time import records (idea
/// d805fd8b, piece 3). Its input is the frozen snapshot in <see cref="LegacyKnowledgeSource"/>,
/// never a live file, so what it has to handle is one known document rather than markdown in
/// general.
/// <para>
/// <b>An entry is a line at column zero opening <c>N. **</c>, and it owns every line until the
/// next one.</b> That is the whole grammar. The log's entries are a markdown ordered list whose
/// continuation paragraphs are unindented (lazy continuation), so there is no indentation to
/// measure and no nesting to track: a nested list inside an entry is indented and therefore never
/// mistaken for a new entry, which is exactly why the column-zero requirement is load-bearing.
/// The entries are not in numeric order in the file, and this parser does not reorder them; the
/// import mints ids in the order it is handed, and the rendered document orders on the ids.
/// </para>
/// <para>
/// <b>Placeholder entries are read too</b>, under their own token (<c>Decisions Log
/// PLACEHOLDER-6df5f975</c>). A branch that has not yet reached its mechanical renumbering step
/// is carrying a real decision under a token rather than a number, and dropping it because it is
/// unnumbered would lose the decision outright. The token is the honest citation for it: it is
/// the text the rest of the repository cites it by at that moment. The frozen snapshot carries
/// no such entry, because main assigned its one placeholder entry #265 before this branch
/// merged; the handling stays, because which entries a snapshot catches mid-flight is not this
/// parser's own assumption to make.
/// </para>
/// <para>
/// <b>One thing is dropped, and only one.</b> A blockquote paragraph whose opening line is a
/// <c>placement note:</c> is the numbering machinery talking about itself: 168 renumbering notes
/// explaining which number an entry ended up with, and one P2P note explaining which block of
/// numbers a run of entries was filed under. All of it is about where an entry sits in a scheme
/// this change retires, and none of it says anything about what was decided. The P2P note is the
/// only one that loses a fact anybody might want, that #38 to #58 came out of the 2026-08-18 and
/// 2026-08-19 design sessions, and #38 itself says so in its own first sentence. Every other
/// blockquote is kept.
/// </para>
/// </summary>
public static class LegacyDecisionsLogParser
{
    /// <summary>The house style this repository's own prose already cites a log entry by, and therefore the legacy id an imported entry keeps.</summary>
    public const string CitationPrefix = "Decisions Log ";

    /// <summary>
    /// An entry's opening line: a number or a placeholder token, a period, a space, and the bold
    /// run every entry's own claim opens with. Anchored at column zero, which is what separates an
    /// entry from a nested ordered list inside one.
    /// </summary>
    private static readonly Regex EntryHeadPattern =
        new(@"^(?<token>\d+|PLACEHOLDER-[0-9a-f]{8})\. \*\*", RegexOptions.Compiled);

    /// <summary>
    /// The opening line of a blockquote about numbering rather than about a decision. Two
    /// wordings exist in the snapshot, "Renumbering placement note:" and "P2P placement note:",
    /// and the label rather than the full sentence is what is matched, so a third wording of the
    /// same thing would have been caught too.
    /// </summary>
    private static readonly Regex PlacementNoteOpenerPattern =
        new(@"^> [^:]{0,40}placement note:", RegexOptions.Compiled);

    public static IReadOnlyList<LegacyDecisionEntry> Parse(string section)
    {
        string[] lines = section.ReplaceLineEndings("\n").Split('\n');

        List<(string Token, int Start)> heads = [];
        for (int index = 0; index < lines.Length; index++)
        {
            Match match = EntryHeadPattern.Match(lines[index]);
            if (match.Success)
            {
                heads.Add((match.Groups["token"].Value, index));
            }
        }

        List<LegacyDecisionEntry> entries = [];
        for (int head = 0; head < heads.Count; head++)
        {
            (string token, int start) = heads[head];
            int end = head + 1 < heads.Count ? heads[head + 1].Start : lines.Length;

            string statement = Statement(lines[start..end], token);
            if (statement.Length > 0)
            {
                entries.Add(new LegacyDecisionEntry(Citation(token), statement));
            }
        }

        return entries;
    }

    /// <summary>
    /// The citation the rest of the repository writes for this entry: <c>Decisions Log #62</c>
    /// for a numbered entry, and <c>Decisions Log PLACEHOLDER-6df5f975</c> for one still waiting
    /// on its number, which is written without a hash because that is how the placeholder token
    /// is cited everywhere else.
    /// </summary>
    private static string Citation(string token) =>
        CitationPrefix + (token.StartsWith("PLACEHOLDER-", StringComparison.Ordinal) ? token : $"#{token}");

    /// <summary>
    /// The entry's own text: its numbering token removed from the front, its renumbering notes
    /// removed, and the blank-line runs those notes leave behind collapsed back to one, so the
    /// text reads as the paragraphs it was written as rather than carrying the shape of what was
    /// taken out of it.
    /// </summary>
    private static string Statement(string[] span, string token)
    {
        List<string> kept = [];
        bool insidePlacementNote = false;
        foreach (string line in span)
        {
            if (PlacementNoteOpenerPattern.IsMatch(line))
            {
                insidePlacementNote = true;
                continue;
            }

            if (insidePlacementNote)
            {
                // The note runs to the end of its own blockquote; the first line that is not part
                // of one ends it and is itself kept.
                if (line.StartsWith('>'))
                {
                    continue;
                }

                insidePlacementNote = false;
            }

            if (line.Length == 0 && kept.Count > 0 && kept[^1].Length == 0)
            {
                continue;
            }

            kept.Add(line);
        }

        if (kept.Count > 0)
        {
            kept[0] = kept[0][(token.Length + 2)..];
        }

        return string.Join('\n', kept).Trim();
    }
}
