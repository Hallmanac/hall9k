using FluentAssertions;
using Hall9k.Daemon.RunSkills;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The discovery session's trailer, read the way the daemon reads it (idea b9b09779, piece 4).
/// The rule every case here is pinning: an unreadable trailer is never turned into a document.
/// Inventing one from whatever prose the summary carried is exactly how a fabricated launch
/// procedure would reach the ledger.
/// </summary>
public sealed class RunSkillResultParserTests
{
    private static string Document(string launch = "dotnet run (README.md)") =>
        "## Prerequisites\n\nThe .NET 10 SDK.\n\n"
        + "## One-time setup\n\ndotnet restore\n\n"
        + $"## Launch\n\n{launch}\n\n"
        + "## How to know it is up\n\nThe port opens.\n\n"
        + "## Address or entry point\n\nhttp://localhost:5000\n\n"
        + "## Human steps\n\nA GitHub token; put it in .env as GH_TOKEN.\n";

    [Fact]
    public void A_well_formed_trailer_yields_the_shape_and_the_document_verbatim()
    {
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"I read the repository.\n\nRUN SKILL SHAPE: full-text\nRUN SKILL MARKDOWN:\n{Document()}");

        composition.Usable.Should().BeTrue();
        composition.Shape.Should().Be(RunSkillShape.FullText);
        composition.Markdown.Should().Contain("## Human steps");
        composition.Markdown.Should().StartWith("## Prerequisites");
    }

    [Fact]
    public void A_document_that_quotes_the_markers_inside_itself_does_not_confuse_the_markers_above_it()
    {
        // The markdown block runs to the end of the summary, so a skill explaining its own
        // contract must not be able to win the marker search from inside the document.
        string document = Document() + "\nThe daemon reads RUN SKILL SHAPE: pointer off the trailer.\n";
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: full-text\nRUN SKILL MARKDOWN:\n{document}");

        composition.Shape.Should().Be(RunSkillShape.FullText);
        composition.Markdown.Should().Contain("The daemon reads RUN SKILL SHAPE: pointer off the trailer.");
    }

    [Fact]
    public void A_restated_shape_line_above_the_markdown_takes_the_last_one()
    {
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: full-text\nOn reflection:\nRUN SKILL SHAPE: pointer\nRUN SKILL MARKDOWN:\n{Document()}");

        composition.Shape.Should().Be(RunSkillShape.Pointer);
    }

    [Theory]
    [InlineData(null, "no summary at all")]
    [InlineData("", "no summary at all")]
    [InlineData("I had a look and it seems fine.", "no RUN SKILL MARKDOWN")]
    public void An_absent_trailer_is_unreadable_rather_than_guessed_at(string? summary, string expected)
    {
        RunSkillComposition composition = RunSkillResultParser.Parse(summary);

        composition.Usable.Should().BeFalse();
        composition.Shape.Should().Be(RunSkillShape.Unknown);
        composition.Markdown.Should().BeEmpty();
        composition.Problem.Should().Contain(expected);
    }

    [Fact]
    public void A_markdown_block_with_no_shape_above_it_is_unreadable()
    {
        RunSkillComposition composition = RunSkillResultParser.Parse($"RUN SKILL MARKDOWN:\n{Document()}");

        composition.Usable.Should().BeFalse();
        composition.Problem.Should().Contain("RUN SKILL SHAPE");
    }

    [Fact]
    public void None_discoverable_is_not_a_shape_a_session_may_declare()
    {
        // It is the daemon's own finding, reached from the survey before any session runs, so a
        // session claiming it would be claiming something it was never in a position to know.
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: none-discoverable\nRUN SKILL MARKDOWN:\n{Document()}");

        composition.Usable.Should().BeFalse();
        composition.Problem.Should().Contain("not one of the two");
    }

    [Fact]
    public void A_document_missing_a_shared_section_is_unreadable_and_names_what_is_missing()
    {
        RunSkillComposition composition = RunSkillResultParser.Parse(
            "RUN SKILL SHAPE: pointer\nRUN SKILL MARKDOWN:\n## Launch\n\nmake dev\n");

        composition.Usable.Should().BeFalse();
        composition.Problem.Should().Contain("Prerequisites");
    }

    [Fact]
    public void A_session_that_fenced_its_whole_answer_still_yields_the_document_without_the_fence()
    {
        // The prompt says not to fence it, and then shows its only worked example inside one.
        // A skill landing on the ledger with a stray ``` at each end passes every other check
        // here, so it would corrupt the shipped artifact silently.
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: pointer\nRUN SKILL MARKDOWN:\n```markdown\n{Document()}```\n");

        composition.Usable.Should().BeTrue();
        composition.Markdown.Should().StartWith("## Prerequisites");
        composition.Markdown.Should().NotContain("```");
    }

    [Fact]
    public void A_blank_line_under_the_opening_fence_does_not_save_the_fence_from_being_stripped()
    {
        // The same over-fenced answer as above with one press of return in it. Reading only the
        // line immediately under the fence let this one through with both fences attached and the
        // six headings intact, which is the silent corruption the unfencing exists to prevent.
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: pointer\nRUN SKILL MARKDOWN:\n```markdown\n\n{Document()}```\n");

        composition.Usable.Should().BeTrue();
        composition.Markdown.Should().StartWith("## Prerequisites");
        composition.Markdown.Should().NotContain("```");
    }

    [Fact]
    public void A_fenced_command_inside_the_document_is_never_mistaken_for_an_outer_fence()
    {
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: full-text\nRUN SKILL MARKDOWN:\n{Document("```\nmake dev\n```")}");

        composition.Usable.Should().BeTrue();
        composition.Markdown.Should().StartWith("## Prerequisites");
        composition.Markdown.Should().Contain("```\nmake dev\n```");
        composition.Markdown.Should().Contain("## Human steps");
    }

    [Fact]
    public void A_document_that_opens_and_closes_with_its_own_fenced_commands_is_left_entirely_alone()
    {
        // The unfencing above must not become a corruption of its own: this document's first and
        // last lines are both fences belonging to its own content, and stripping one off each end
        // would break two code blocks to fix a fence that was never there.
        string document = "```\ngit clone ...\n```\n" + Document() + "\n```\nmake dev\n```";
        RunSkillComposition composition = RunSkillResultParser.Parse(
            $"RUN SKILL SHAPE: full-text\nRUN SKILL MARKDOWN:\n{document}");

        composition.Usable.Should().BeTrue();
        composition.Markdown.Should().Be(document);
    }

    [Fact]
    public void A_markdown_marker_with_nothing_under_it_is_unreadable()
    {
        RunSkillComposition composition = RunSkillResultParser.Parse(
            "RUN SKILL SHAPE: pointer\nRUN SKILL MARKDOWN:\n");

        composition.Usable.Should().BeFalse();
        composition.Problem.Should().Contain("no document under it");
    }
}
