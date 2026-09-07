using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// AGENTS.md is auto-loaded into every session's turn-1 context through CLAUDE.md's own
/// `@AGENTS.md` line, dispatched and interactive alike (task: dispatched sessions stop
/// inheriting account MCP connectors, and AGENTS.md is cut to what a headless session needs).
/// It grew to 1,002 lines with nothing to stop it before that task cut it back down; this guard
/// is what keeps it from silently regrowing the same way, at the 200-line ceiling the Claude Code
/// docs recommend for a CLAUDE.md-loaded file. Content that belongs to a narrower audience
/// (the interactive orchestrator window, the full CLI/domain reference) lives in
/// <c>ORCHESTRATOR-WINDOW.md</c> and the <c>hall9k-cli-reference</c> skill instead, loaded only
/// when actually needed rather than on every turn.
/// <para>
/// The ceiling reads 201, one over the recommendation, and the extra line is recorded rather than
/// rounded off: on 2026-09-07 two branches each added one standing git invariant to the Git rules
/// section, and the one that merged first (`h9k pr request-changes`, Decisions Log #149) took the
/// file to exactly 200. The second — an agent never tells a human reviewer they are wrong
/// (Decisions Log #152) — is a turn-1 rule for dispatched fix sessions, so it belongs in this file
/// rather than a skill loaded on demand, and reflowing unrelated paragraphs to buy back its line
/// would have hidden a capacity decision inside a cosmetic diff. This is a one-line exception, not
/// a new budget: the next addition here should cut something out rather than raise this again.
/// </para>
/// </summary>
public sealed class AgentsMarkdownLineCountTests
{
    private const int LineCeiling = 201;

    [Fact]
    public void AGENTS_markdown_stays_at_or_under_its_recorded_line_ceiling()
    {
        string agentsMarkdownPath = AgentsMarkdownPath();
        File.Exists(agentsMarkdownPath).Should().BeTrue($"AGENTS.md should exist at '{agentsMarkdownPath}'");

        int lineCount = File.ReadAllLines(agentsMarkdownPath).Length;

        lineCount.Should().BeLessThanOrEqualTo(LineCeiling,
            $"AGENTS.md is {lineCount} lines, over its recorded ceiling of {LineCeiling} — it is " +
            "auto-loaded into every dispatched session's turn-1 context, so new content belongs in " +
            "ORCHESTRATOR-WINDOW.md, the hall9k-cli-reference skill, or another skill, not appended " +
            "here unbounded. Move content out (or raise the ceiling here deliberately, with a recorded " +
            "why) rather than letting this grow back to what it was before.");
    }

    private static string AgentsMarkdownPath()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();
        string? repositoryRoot = Path.GetDirectoryName(sourceDirectory);
        if (repositoryRoot is null)
        {
            throw new InvalidOperationException($"'{sourceDirectory}' has no parent directory to resolve the repository root from");
        }

        return Path.Combine(repositoryRoot, "AGENTS.md");
    }
}
