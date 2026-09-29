using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The run-skill discovery-failure reason reaches <c>h9k project show</c>'s settings pane through
/// two <c>RunSkillRow</c> branches — the trailer beside a standing skill and the row printed in
/// its place — and both interpolate the same reason a discovery agent declared, so both need the
/// same guarantee <c>ProjectRunSkillShowCommand</c>'s own warning and pending-state line already
/// carry. This test drives <see cref="ProjectShowCommand.RenderedDiscoveryFailure"/> — the exact
/// method both branches print through — rather than <c>ExternalText</c> directly.
/// </summary>
public sealed class ProjectShowCommandTests
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
    public void A_discovery_failure_reason_cannot_act_on_the_terminal_and_stays_on_one_line()
    {
        string failure =
            $"parse failed{Escape}[2J at line{Escape}]52;c;aGVsbG8={Bell} three\r\n"
            + $"cause: {RightToLeftOverride}desruc{PopDirectionalFormatting}"
            + "\rovertype\ttabbed";

        string rendered = ProjectShowCommand.RenderedDiscoveryFailure(failure);

        rendered.Should().NotContain(Escape).And.NotContain(Bell)
            .And.NotContain(RightToLeftOverride).And.NotContain(PopDirectionalFormatting)
            .And.NotContain("\n").And.NotContain("\r");

        // Every line break folds to a space rather than vanishing, so words on either side of one
        // do not run together. A lone CR is not a line break; it is dropped outright, the same as
        // ExternalText.ForTerminal drops it, so the words around it run together rather than gain
        // a space.
        rendered.Should().Contain("three cause:").And.Contain("desrucovertype tabbed");
    }

    [Fact]
    public void A_discovery_failure_reason_still_has_its_own_markup_escaped()
    {
        string rendered = ProjectShowCommand.RenderedDiscoveryFailure("[red]not really red[/]");

        rendered.Should().Be("[[red]]not really red[[/]]");
    }
}
