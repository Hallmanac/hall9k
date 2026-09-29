using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A run skill's content can arrive from a discovery agent or replicate in from another node, so
/// by the time <c>h9k project run-skill show</c> prints it, it is outside text like an adopted
/// issue body, not something this node authored. Line 49 of
/// <c>ProjectRunSkillShowCommand.RunAsync</c> prints it through
/// <see cref="ExternalText.ForTerminal"/> before <c>AnsiConsole.WriteLine</c> ever sees it, exactly
/// the gate <c>TaskShowCommand</c> already puts an adopted body through
/// (<c>TaskExternalReferenceTests</c>) — this test pins the same guarantee for the run-skill
/// surface, since a skill carrying a control sequence could otherwise act on the operator's
/// terminal instead of merely being read by them.
/// </summary>
public sealed class ProjectRunSkillShowCommandTests
{
    // Built from code points rather than written out as literal characters: a source file
    // carrying a live bidirectional override reverses the code around it in the editor of
    // whoever reads this next (the same reason MessageShowLinesTests builds its own copy this
    // way), and an escape or BEL character in a literal is simply invisible there.
    private static readonly string RightToLeftOverride = ((char)0x202E).ToString();
    private static readonly string PopDirectionalFormatting = ((char)0x202C).ToString();
    private static readonly string Escape = ((char)0x1B).ToString();
    private static readonly string Bell = ((char)0x07).ToString();

    [Fact]
    public void A_run_skill_cannot_act_on_the_terminal_it_is_shown_in()
    {
        // A clear-screen escape sequence, an OSC 52 clipboard write terminated with BEL, a lone
        // carriage return that would overwrite the line above it, and a right-to-left override
        // that would reverse what a line appears to say — none of them authored by this node.
        string content =
            $"step one{Escape}[2J\n"
            + $"step two{Escape}]52;c;aGVsbG8={Bell}after\n"
            + "overtype\rvictim\n"
            + $"reversed: {RightToLeftOverride}elbaifitsuj si eno yreve{PopDirectionalFormatting}\n"
            + "\tindented step\r\n"
            + "last step";

        string rendered = ExternalText.ForTerminal(content);

        rendered.Should().NotContain(Escape, "an escape sequence is obeyed by the terminal, not read by it")
            .And.NotContain(Bell, "the BEL that terminates an OSC write is a control character too")
            .And.NotContain(RightToLeftOverride, "a right-to-left override reverses what a line appears to say")
            .And.NotContain(PopDirectionalFormatting, "its matching pop is the same kind of layout override");

        // What was never a control or override character still reads as itself: the sequences
        // lose their power, not their text.
        rendered.Should().Contain("step one[2J")
            .And.Contain("step two]52;c;aGVsbG8=after")
            .And.Contain("reversed: elbaifitsuj si eno yreve");

        // A lone CR is dropped rather than kept, so it cannot paint over the line before it.
        rendered.Should().Contain("overtypevictim").And.NotContain("overtype\rvictim");

        // An ordinary skill's own layout survives untouched: line feeds, a tab, and a CRLF break.
        rendered.Should().Contain("\n\tindented step\r\nlast step");
    }
}
