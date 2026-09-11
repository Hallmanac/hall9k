namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Reads the pull request the build session composed for itself off the agent's own session-end
/// result, the same way <see cref="HandoffParser"/> reads the handoff out of the same text.
/// <para>
/// The text arrives as a marked block in the final message rather than as a file the agent
/// writes, because the build prompt names the project home and the worktree but never the run
/// directory (<c>RunLauncher</c> resolves that at dispatch and nothing hands it to the prompt),
/// so a file would have to be written outside the worktree the session was told to work only in.
/// The daemon already has proven machinery for a marked block in the terminal result, and the
/// handoff is exactly this shape.
/// </para>
/// <para>
/// The marker is matched the way every other marker on this result is (log #23): the LAST one
/// wins, because agents sometimes quote the instructions before answering.
/// </para>
/// </summary>
public static class PrSummaryParser
{
    /// <summary>
    /// The marker line the build prompt asks for. Declared here, and used by the prompt builders
    /// from here, so the instruction and the parser can never drift apart — the same contract
    /// <see cref="HandoffParser.Marker"/> holds for the handoff.
    /// </summary>
    public const string Marker = "PR SUMMARY:";

    /// <summary>The line inside the block carrying the pull request's title.</summary>
    public const string TitlePrefix = "Title:";

    /// <summary>
    /// What the session composed: the title it wrote (null when the block carried no
    /// <see cref="TitlePrefix"/> line, which is a real answer rather than an empty string) and
    /// the body below it, which may itself be empty when the session wrote a title and nothing
    /// under it.
    /// </summary>
    public sealed record PrSummary(string? Title, string Body);

    /// <summary>
    /// The block the session's result carried, or null when it carried none. Null is a real
    /// answer: no artifact is written, and <c>PullRequestBody</c> falls back to the skeleton it
    /// has always composed.
    /// <para>
    /// The block runs from the last <see cref="Marker"/> line to whichever closing marker comes
    /// first after it, and to the end of the text when none does. Two markers can close it, and
    /// each is located by its own reader's own rule so that all three agree on where one block
    /// stops and the next begins: <see cref="HandoffParser.Marker"/> at its LAST occurrence, since
    /// that is the line <see cref="HandoffParser.Parse"/> reads the handoff from, and
    /// <c>RESOLUTION:</c> at its first after the marker, since <c>ReviewResultParser</c>'s own
    /// block scanning already treats the resolution line as terminal wherever it appears. In
    /// practice each session shape carries exactly one of the two — a build session ends with a
    /// handoff and never a resolution, a review-fix session with a resolution and never a handoff
    /// — so the asymmetry is a belt-and-braces rule rather than one either shape exercises.
    /// </para>
    /// </summary>
    public static PrSummary? Parse(string? summary)
    {
        if (summary.IsBlank())
        {
            return null;
        }

        string[] lines = summary.Split('\n');
        int start = -1;
        int lastHandoff = -1;
        for (int index = 0; index < lines.Length; index++)
        {
            string trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
            {
                start = index;
            }
            else if (trimmed.StartsWith(HandoffParser.Marker, StringComparison.OrdinalIgnoreCase))
            {
                lastHandoff = index;
            }
        }

        if (start < 0)
        {
            return null;
        }

        // A closing marker that sits BEFORE the block never closes it: a session that quoted the
        // handoff instruction on its way past leaves a HANDOFF: line above its own summary, and
        // reading that as this block's end would leave the block empty.
        int end = lastHandoff > start ? lastHandoff : lines.Length;
        for (int index = start + 1; index < end; index++)
        {
            if (lines[index].TrimStart().StartsWith(ResolutionMarker, StringComparison.OrdinalIgnoreCase))
            {
                end = index;
                break;
            }
        }

        string firstLine = lines[start].TrimStart()[Marker.Length..];
        return ParseBlock(string.Join('\n', [firstLine, .. lines[(start + 1)..end]]));
    }

    /// <summary>
    /// One block's own text, split into its title and its body. Shared by <see cref="Parse"/> and
    /// by the opener reading <c>pr-summary.md</c> back off disk, so the artifact round-trips
    /// through exactly the rules the session was asked to write it under.
    /// <para>
    /// A surrounding fenced code block is tolerated because the skill's own process step tells
    /// the agent to output its result in one, so a session that follows both instructions
    /// faithfully hands over a fenced block and must not be punished for it.
    /// </para>
    /// </summary>
    public static PrSummary? ParseBlock(string? block)
    {
        if (block.IsBlank())
        {
            return null;
        }

        IReadOnlyList<string> lines =
            WithoutSurroundingFence([.. block.Split('\n').Select(line => line.TrimEnd('\r'))]);

        string? title = null;
        int bodyStart = lines.Count;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].IsBlank())
            {
                continue;
            }

            if (lines[index].TrimStart().StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                title = lines[index].TrimStart()[TitlePrefix.Length..].Trim();
                bodyStart = index + 1;
            }
            else
            {
                // No Title: line on the first non-blank line means the session wrote none.
                // Recorded as title-null with the body kept whole, never as a title guessed off
                // the block's opening sentence (AGENTS.md: never guess at unobserved facts).
                bodyStart = index;
            }

            break;
        }

        string body = string.Join('\n', lines.Skip(bodyStart)).Trim();
        return title.IsBlank() && body.IsBlank() ? null : new PrSummary(title.IsBlank() ? null : title, body);
    }

    /// <summary>
    /// The block as the run directory keeps it: the title line, a blank line, then the body, so
    /// <see cref="ParseBlock"/> reads back exactly what was captured. A title-less block renders
    /// as its body alone rather than as an empty <see cref="TitlePrefix"/> line, which would read
    /// on the way back in as a title the session wrote and left blank.
    /// </summary>
    public static string Render(PrSummary summary) =>
        summary.Title is null ? summary.Body : $"{TitlePrefix} {summary.Title}\n\n{summary.Body}".TrimEnd();

    /// <summary>
    /// <c>ReviewResultParser.ParseFixOutcome</c>'s own marker, restated here rather than
    /// referenced: that parser lives in <c>Hall9k.Daemon</c>, which references this assembly and
    /// not the other way round (AGENTS.md, reference graph). Its own block scanning already
    /// hard-codes the identical literal for the identical reason.
    /// </summary>
    internal const string ResolutionMarker = "RESOLUTION:";

    /// <summary>
    /// The lines inside a fenced code block, when the whole block is one; the lines exactly as
    /// they arrived otherwise. Only a fence that both opens and closes the block is stripped: a
    /// fenced example the session included INSIDE its body opens no fence on the block's own
    /// first line, and a body that opens with one and never closes it is not a wrapper at all.
    /// </summary>
    private static IReadOnlyList<string> WithoutSurroundingFence(IReadOnlyList<string> lines)
    {
        int first = -1;
        int last = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].IsNotBlank())
            {
                first = first < 0 ? index : first;
                last = index;
            }
        }

        if (first < 0 || last <= first)
        {
            return lines;
        }

        string opener = lines[first].Trim();
        int fenceLength = opener.Length - opener.TrimStart('`').Length;
        return fenceLength >= 3 && lines[last].Trim() == new string('`', fenceLength)
            ? [.. lines.Skip(first + 1).Take(last - first - 1)]
            : lines;
    }
}
