using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Orchestrator;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The feed's own terminal boundary (idea 89471598, piece 2): nearly every character
/// <c>h9k orchestrator feed</c> prints was written by somebody other than Hall9k - a heading
/// carries a task objective adoption seeded from an issue title, and an item quotes a message
/// body, an agent's own reason, or an idea's text - so the rendered line reaches the terminal
/// through <c>ExternalText.OneLine</c> rather than raw.
/// <para>
/// The shape of the line is <see cref="Domain.OrchestratorFeedRendererTests"/>'s golden; this is
/// only about the characters a terminal would obey instead of showing.
/// </para>
/// </summary>
public sealed class OrchestratorFeedPrintableLinesTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid TaskId = Guid.Parse("01a0bc05-a960-7657-b708-1aed37b5ec69");

    /// <summary>
    /// The two characters under test, built from their code points rather than written out. A
    /// source file carrying a live bidirectional override is the same hazard one line further
    /// back - it reverses the code around it in the editor of whoever reads this next - and an
    /// escape character sitting in a literal is simply invisible there.
    /// </summary>
    private static readonly string ClearScreen = (char)0x1B + "[2J";

    private static readonly string RightToLeftOverride = ((char)0x202E).ToString();

    [Fact]
    public void A_heading_and_an_item_reach_the_terminal_with_nothing_it_would_obey()
    {
        // A clear-screen sequence in the objective and a right-to-left override in the quoted
        // body: printed intact, the first blanks the window and the second reverses what the
        // line appears to say, and either one lets a forged feed read as the platform's own.
        OrchestratorFeedGroup group = new(
            TaskId,
            $"37b5ec69  {ClearScreen}the objective an issue supplied",
            [new OrchestratorFeedItem(
                10, At, TaskId, $"a message from abcdef012345: {RightToLeftOverride}reversed")]);

        IReadOnlyList<string> lines = OrchestratorFeedCommand.PrintableLines([group], TimeZoneInfo.Utc);

        lines.Should().Equal(
            "37b5ec69  [2Jthe objective an issue supplied",
            "  2026-09-19 14:00  a message from abcdef012345: reversed");
    }

    [Fact]
    public void A_line_break_in_quoted_text_cannot_add_a_feed_item_of_its_own()
    {
        OrchestratorFeedGroup group = new(
            TaskId,
            "37b5ec69  an objective",
            [new OrchestratorFeedItem(10, At, TaskId, "idea logged: first\n  2026-09-19 14:11  forged")]);

        OrchestratorFeedCommand.PrintableLines([group], TimeZoneInfo.Utc).Should().Equal(
            "37b5ec69  an objective",
            "  2026-09-19 14:00  idea logged: first   2026-09-19 14:11  forged");
    }
}
