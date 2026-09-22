namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// Reads AGENTS.md's standing-rule sections into the entries the one-time import records (idea
/// d805fd8b, piece 3), from the frozen snapshot in <see cref="LegacyKnowledgeSource"/>.
/// <para>
/// <b>A section is a <c>## </c> heading, a rule is a <c>- </c> bullet at column zero, and the
/// bullet owns its indented continuation lines.</b> AGENTS.md hard-wraps every rule at about a
/// hundred characters and indents the wrapped lines by two spaces, so the indentation is the only
/// thing separating one rule from the next. The indent is removed on the way out, which changes
/// no words: markdown reads the result as the single paragraph the rule was always written as.
/// </para>
/// <para>
/// <b>These rules never had numbers, so their citation is their position.</b> A legacy id reads
/// <c>AGENTS.md Git rules #4</c>: the file, the section, and where the rule sat in it. Nothing in
/// this repository cites an AGENTS.md rule by number today, so nothing resolves through these the
/// way it does through a §16 number; they exist so an imported rule still says where it came
/// from, and so a second run of the import can tell it has already read this one.
/// </para>
/// </summary>
public static class LegacyStandingRulesParser
{
    /// <summary>The file these rules were authored in, and the front of every legacy id this parser hands back.</summary>
    public const string CitationPrefix = "AGENTS.md ";

    private const string SectionHeadingPrefix = "## ";
    private const string BulletPrefix = "- ";
    private const string ContinuationIndent = "  ";

    public static IReadOnlyList<LegacyDecisionEntry> Parse(string rules)
    {
        string[] lines = rules.ReplaceLineEndings("\n").Split('\n');

        List<LegacyDecisionEntry> entries = [];
        string section = string.Empty;
        int positionInSection = 0;
        List<string> current = [];

        foreach (string line in lines)
        {
            if (line.StartsWith(SectionHeadingPrefix, StringComparison.Ordinal))
            {
                Close(entries, section, ref positionInSection, current);
                section = line[SectionHeadingPrefix.Length..].Trim();
                positionInSection = 0;
                continue;
            }

            if (line.StartsWith(BulletPrefix, StringComparison.Ordinal))
            {
                Close(entries, section, ref positionInSection, current);
                current.Add(line[BulletPrefix.Length..]);
                continue;
            }

            if (current.Count == 0)
            {
                continue;
            }

            // A wrapped line belongs to the bullet above it; anything else, a blank line included,
            // ends the bullet. Nothing in these two sections puts a second paragraph inside a
            // rule, so ending on the first unindented line loses nothing and keeps the grammar
            // small enough to read.
            if (line.StartsWith(ContinuationIndent, StringComparison.Ordinal))
            {
                current.Add(line[ContinuationIndent.Length..]);
                continue;
            }

            Close(entries, section, ref positionInSection, current);
        }

        Close(entries, section, ref positionInSection, current);
        return entries;
    }

    private static void Close(
        List<LegacyDecisionEntry> entries, string section, ref int positionInSection, List<string> current)
    {
        if (current.Count == 0)
        {
            return;
        }

        string statement = string.Join('\n', current).Trim();
        current.Clear();
        if (statement.Length == 0)
        {
            return;
        }

        positionInSection++;
        entries.Add(new LegacyDecisionEntry($"{CitationPrefix}{section} #{positionInSection}", statement));
    }
}
