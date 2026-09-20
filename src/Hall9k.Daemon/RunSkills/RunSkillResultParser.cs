using Hall9k.Domain.Features.Project;

namespace Hall9k.Daemon.RunSkills;

/// <summary>
/// What a discovery session's trailer actually yielded (idea b9b09779, piece 4).
/// <see cref="Shape"/> is <see cref="RunSkillShape.Unknown"/> and <see cref="Markdown"/> blank
/// whenever the trailer could not be read; <see cref="Problem"/> then says what was wrong, in
/// words a human reading <c>h9k project show</c> can act on.
/// </summary>
public sealed record RunSkillComposition(RunSkillShape Shape, string Markdown, string? Problem)
{
    public bool Usable => Problem is null;

    public static RunSkillComposition Unreadable(string problem) =>
        new(RunSkillShape.Unknown, string.Empty, problem);
}

/// <summary>
/// Tolerant reader of the run-skill discovery session's own trailer, the same "last marker line
/// wins" discipline <c>StackAssessmentResultParser</c> applies to its verdict. An unreadable
/// trailer is never guessed at: there is nothing here to tell "the session composed a skill this
/// parser mangled" apart from "the session never composed one", and inventing a document from
/// whatever prose the summary happened to carry is exactly what would put a fabricated launch
/// procedure on the ledger.
/// </summary>
internal static class RunSkillResultParser
{
    internal const string ShapeMarker = "RUN SKILL SHAPE:";
    internal const string MarkdownMarker = "RUN SKILL MARKDOWN:";

    public static RunSkillComposition Parse(string? summary)
    {
        if (summary.IsBlank())
        {
            return RunSkillComposition.Unreadable(
                "the discovery session produced no summary at all, so no run-skill trailer could be read");
        }

        string normalized = summary.Replace("\r\n", "\n", StringComparison.Ordinal);

        // The markdown block runs to the end of the summary, so a document that quotes either
        // marker inside itself (this very skill explaining its own contract) must not be allowed
        // to win the marker search. Both markers are therefore looked for only ABOVE the first
        // MARKDOWN marker line, which is the trailer's own fixed order.
        int markdownAt = IndexOfMarkerLine(normalized, MarkdownMarker);
        if (markdownAt < 0)
        {
            return RunSkillComposition.Unreadable(
                $"the discovery session's summary carried no {MarkdownMarker} line, so there is no composed "
                + "document to record");
        }

        string trailerSection = normalized[..markdownAt];
        string? shapeValue = LastMarkerValue(trailerSection, ShapeMarker);
        if (shapeValue.IsBlank())
        {
            return RunSkillComposition.Unreadable(
                $"the discovery session's summary carried no {ShapeMarker} line above its {MarkdownMarker} "
                + "block, so whether it is a pointer or a full-text skill is unknown");
        }

        RunSkillShape shape = RunSkillShape.FromInput(shapeValue);
        if (shape != RunSkillShape.Pointer && shape != RunSkillShape.FullText)
        {
            return RunSkillComposition.Unreadable(
                $"the discovery session declared shape '{shapeValue.Trim()}', which is not one of the two a "
                + $"session may choose ({RunSkillShape.Pointer.Value}, {RunSkillShape.FullText.Value})");
        }

        string markdown = normalized[markdownAt..];
        int endOfMarkerLine = markdown.IndexOf('\n');
        markdown = endOfMarkerLine < 0 ? string.Empty : Unfence(markdown[(endOfMarkerLine + 1)..].Trim('\n'));

        if (markdown.IsBlank())
        {
            return RunSkillComposition.Unreadable(
                $"the discovery session's {MarkdownMarker} line was the last thing it wrote, with no document "
                + "under it");
        }

        if (RunSkillDocument.MissingHeadings(markdown) is { Count: > 0 } missing)
        {
            return RunSkillComposition.Unreadable(
                $"the discovery session's composed document is missing {string.Join(", ", missing)}, so it is "
                + "not in the shape every project's run skill shares");
        }

        return new RunSkillComposition(shape, markdown, null);
    }

    /// <summary>
    /// The document with a code fence wrapped around the whole of it removed. The prompt tells a
    /// session not to fence its answer, but the only worked example it shows is itself inside a
    /// fence, so a session mimicking the example is a mistake worth expecting rather than
    /// punishing: the alternative is a run skill landing on the ledger with a stray <c>```</c> at
    /// each end, which passes every other check here (the six headings are still there) and so
    /// corrupts the shipped artifact silently. Found by reading the assembled prompt end to end
    /// rather than the template (this branch's own self-review, hunt 3).
    /// <para>
    /// Three conditions together, not two, and the third is what keeps this from doing damage of
    /// its own (this branch's own self-review, round two): a fence must open the first line, a
    /// bare fence must close the last, AND what sits between them must begin with a markdown
    /// heading. Without the third, a document that legitimately OPENS with a fenced command and
    /// happens to END with one too would have had one fence line stripped off each end — a fix
    /// corrupting the very artifact it exists to protect. A genuinely over-fenced answer always
    /// satisfies all three, since the document inside it starts at its first heading.
    /// </para>
    /// <para>
    /// The heading test reads the first NON-BLANK line inside the fence rather than the line
    /// immediately under it: a session that fenced its whole answer and pressed return before
    /// starting the document is making exactly the mistake this method exists to absorb, and
    /// testing only the next line let that one through with both fences still attached, six
    /// headings intact, straight onto the ledger (independent pre-PR review, cycle 1, conformance
    /// lens). Skipping blanks costs the third condition nothing: a document that legitimately
    /// opens with a fenced command still has that command, not a heading, as its own first
    /// non-blank line.
    /// </para>
    /// </summary>
    private static string Unfence(string markdown)
    {
        string[] lines = markdown.Split('\n');
        if (lines.Length < 3
            || !lines[0].TrimEnd().StartsWith("```", StringComparison.Ordinal)
            || lines[^1].Trim() != "```")
        {
            return markdown;
        }

        string[] inner = lines[1..^1];
        string? firstContentLine = inner.FirstOrDefault(line => line.Trim().Length > 0);
        return firstContentLine?.TrimStart().StartsWith('#') == true
            ? string.Join('\n', inner).Trim('\n')
            : markdown;
    }

    /// <summary>
    /// Where a line whose trimmed start is <paramref name="marker"/> begins — the FIRST such
    /// line, since the markdown block below it is unbounded and a later match would be inside the
    /// document rather than in the trailer.
    /// </summary>
    private static int IndexOfMarkerLine(string text, string marker)
    {
        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            int length = (lineEnd < 0 ? text.Length : lineEnd) - lineStart;
            if (text.AsSpan(lineStart, length).TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return lineStart;
            }

            if (lineEnd < 0)
            {
                return -1;
            }

            lineStart = lineEnd + 1;
        }

        return -1;
    }

    /// <summary>
    /// The value after the LAST line starting with <paramref name="marker"/>, the same tolerance
    /// the review and stack-assessment parsers give a session that restated its own trailer.
    /// </summary>
    private static string? LastMarkerValue(string text, string marker)
    {
        string? value = null;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                value = trimmed[marker.Length..].Trim();
            }
        }

        return value;
    }
}
