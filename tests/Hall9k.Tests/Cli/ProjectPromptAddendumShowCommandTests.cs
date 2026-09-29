using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A prompt addendum's content and its over-cap reason both travel in the same
/// <c>ProjectPromptAddendumSet</c> event, so either one can be written by any project member who
/// can set an addendum — outside text like an adopted issue body, not something this node
/// authored. This test drives <see cref="ProjectPromptAddendumShowCommand.RenderedAddendumContent"/>
/// and <see cref="ProjectPromptAddendumShowCommand.RenderedOverCapReason"/> — the exact methods
/// <c>ProjectPromptAddendumShowCommand.RunAsync</c> prints through — rather than
/// <see cref="ExternalText"/> directly, the same shape <c>ProjectRunSkillShowCommandTests</c> pins
/// a skill's own content to.
/// </summary>
public sealed class ProjectPromptAddendumShowCommandTests
{
    // Built from code points rather than written out as literal characters: a source file
    // carrying a live bidirectional override reverses the code around it in the editor of
    // whoever reads this next, and an escape or BEL character in a literal is simply invisible
    // there.
    private static readonly string RightToLeftOverride = ((char)0x202E).ToString();
    private static readonly string PopDirectionalFormatting = ((char)0x202C).ToString();
    private static readonly string Escape = ((char)0x1B).ToString();
    private static readonly string Bell = ((char)0x07).ToString();

    [Fact]
    public void An_addendum_cannot_act_on_the_terminal_it_is_shown_in()
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

        string rendered = ProjectPromptAddendumShowCommand.RenderedAddendumContent(content);

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

        // An ordinary addendum's own layout survives untouched: line feeds, a tab, and a CRLF
        // break.
        rendered.Should().Contain("\n\tindented step\r\nlast step");
    }

    [Fact]
    public void An_over_cap_reason_cannot_act_on_the_terminal_and_stays_on_one_line()
    {
        string reason =
            $"too long{Escape}[2J because it needed{Escape}]52;c;aGVsbG8={Bell} everything\r\n"
            + $"second line{RightToLeftOverride} reversed{PopDirectionalFormatting}"
            + "\rovertype\ttabbed";

        string rendered = ProjectPromptAddendumShowCommand.RenderedOverCapReason(reason);

        rendered.Should().NotContain(Escape).And.NotContain(Bell)
            .And.NotContain(RightToLeftOverride).And.NotContain(PopDirectionalFormatting)
            .And.NotContain("\n").And.NotContain("\r");

        // Every line break folds to a space rather than vanishing, so words on either side of one
        // do not run together. A lone CR is not a line break; it is dropped outright, the same as
        // ExternalText.ForTerminal drops it, so the words around it run together rather than gain
        // a space.
        rendered.Should().Contain("everything second line").And.Contain("reversedovertype tabbed");
    }

    [Fact]
    public void An_over_cap_reason_still_has_its_own_markup_escaped()
    {
        string rendered = ProjectPromptAddendumShowCommand.RenderedOverCapReason("[red]not really red[/]");

        rendered.Should().Be("[[red]]not really red[[/]]");
    }
}
