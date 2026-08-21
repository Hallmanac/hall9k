using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// How an adopted work item reads on the one surface a human actually opens. The link matters
/// because the point of adoption is the round trip (PLAN.md §3.1a), and the caption matters
/// because the row would otherwise read as live status for something the platform looked at
/// once and never again.
/// </summary>
public sealed class TaskExternalReferenceTests
{
    [Fact]
    public void An_adopted_github_issue_renders_as_a_link_to_the_issue()
    {
        string markup = TaskShowCommand.ExternalMarkup("github:Hallmanac/hall9k#42");

        markup.Should().Contain("[link=https://github.com/Hallmanac/hall9k/issues/42]")
            .And.Contain("github:Hallmanac/hall9k#42");
    }

    [Fact]
    public void The_row_says_the_state_behind_it_is_not_being_watched()
    {
        string markup = TaskShowCommand.ExternalMarkup("github:Hallmanac/hall9k#42");

        markup.Should().Contain("read once at import; never re-checked");
    }

    [Fact]
    public void A_reference_no_source_can_place_prints_itself_without_a_link()
    {
        string markup = TaskShowCommand.ExternalMarkup("jira:PROJ-123");

        markup.Should().Be("jira:PROJ-123", "a link built on a guess is worse than no link");
    }

    [Fact]
    public void An_adopted_body_cannot_repaint_the_terminal_it_is_shown_in()
    {
        // Since adoption, agent context can be an issue body written by anyone who can file an
        // issue. Printed raw, an escape sequence in it is executed by the terminal rather than
        // read by the human, so an issue could be authored to make 'h9k task show' show
        // something other than the task.
        string body = "Imported from github:o/r#1.\n\u001b[2JHidden\u0007 text\u001b[31m";

        string rendered = ExternalText.ForTerminal(body);

        rendered.Should().NotContain("\u001b").And.NotContain("\u0007");
        rendered.Should().Contain("Imported from github:o/r#1.")
            .And.Contain("[2JHidden")
            .And.Contain(" text[31m", "the characters that were never control characters still read as themselves");
    }

    [Fact]
    public void A_carriage_return_that_is_not_a_line_break_cannot_paint_over_the_line_above()
    {
        // A lone CR is not layout. It returns the cursor to column zero, so a body carrying one
        // can overwrite what 'h9k task show' already printed and hide what it replaced. Half of
        // a Windows line break is a different thing and survives.
        string body = "Objective: adopt issue 42\rEverything is fine\r\nNext line";

        ExternalText.ForTerminal(body)
            .Should().Be("Objective: adopt issue 42Everything is fine\r\nNext line");
    }

    [Fact]
    public void The_layout_of_a_markdown_body_survives_being_made_safe_to_print()
    {
        // Tabs and newlines are the only control characters a Markdown body means: dropping them
        // would collapse an indented code block into one line, which is the same damage as
        // trimming it at import.
        string body = "Steps:\r\n\n\tcargo build\n    indented code\n";

        ExternalText.ForTerminal(body).Should().Be(body);
    }
}
