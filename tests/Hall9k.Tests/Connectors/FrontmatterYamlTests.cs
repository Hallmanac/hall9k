using FluentAssertions;
using Hall9k.Connectors.Text;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The scalar half of the frontmatter both <c>h9k task add --file</c> and the task record on a
/// published issue are written in. The document grammar stays the forgiving line-oriented one this
/// platform's own <c>task.md</c> renders; what these pin is that every value is now read as a real
/// YAML scalar, which is the 2026-09-06 adoption finding this class was written for.
/// </summary>
public sealed class FrontmatterYamlTests
{
    [Fact]
    public void A_double_quoted_scalar_arrives_without_its_quote_characters()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("objective: \"Adopt an issue\"\n");

        parsed.Scalar("objective").Should().Be("Adopt an issue");
    }

    [Fact]
    public void A_double_quoted_scalar_keeps_its_escapes()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("objective: \"Say \\\"yes\\\"\\nthen stop\"\n");

        parsed.Scalar("objective").Should().Be("Say \"yes\"\nthen stop");
    }

    [Fact]
    public void A_single_quoted_scalar_arrives_unquoted_with_its_doubled_quotes_collapsed()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("objective: 'Brian''s issue'\n");

        parsed.Scalar("objective").Should().Be("Brian's issue");
    }

    [Fact]
    public void A_plain_scalar_that_merely_opens_and_closes_with_a_quote_is_left_alone()
    {
        // "one" and "two" is not a quoted scalar — the closing quote is not the last character —
        // and stripping its outer characters would silently corrupt it.
        Frontmatter parsed = FrontmatterYaml.Parse("objective: \"one\" and \"two\"\n");

        parsed.Scalar("objective").Should().Be("\"one\" and \"two\"");
    }

    [Fact]
    public void A_plain_scalar_carrying_a_colon_reads_verbatim_as_the_file_format_always_has()
    {
        Frontmatter parsed = FrontmatterYaml.Parse(
            "---\nobjective: h9k task show: says which install published it\n---\n");

        parsed.Scalar("objective").Should().Be("h9k task show: says which install published it");
    }

    [Fact]
    public void A_literal_block_scalar_reads_as_the_multi_line_text_it_denotes()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("context: |\n  first line\n  second line\nproject: hall9k\n");

        parsed.Scalar("context").Should().Be("first line\nsecond line\n");
        parsed.Scalar("project").Should().Be("hall9k", "the key after the block is still read");
    }

    [Fact]
    public void A_strip_chomped_block_scalar_keeps_no_trailing_newline()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("context: |-\n  only line\n");

        parsed.Scalar("context").Should().Be("only line");
    }

    [Fact]
    public void A_block_scalar_keeps_the_blank_lines_inside_it()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("context: |-\n  one\n\n  two\nproject: hall9k\n");

        parsed.Scalar("context").Should().Be("one\n\ntwo");
        parsed.Scalar("project").Should().Be("hall9k");
    }

    [Fact]
    public void An_empty_block_scalar_swallows_nothing_underneath_it()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("context: |\nproject: hall9k\n");

        parsed.Scalar("context").Should().BeNull();
        parsed.Scalar("project").Should().Be("hall9k");
    }

    [Fact]
    public void A_folded_block_scalar_joins_its_lines_with_spaces()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("objective: >-\n  one\n  two\n");

        parsed.Scalar("objective").Should().Be("one two");
    }

    [Fact]
    public void A_pipe_inside_a_plain_value_is_not_a_block_header()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("objective: | the pipe is text\nproject: hall9k\n");

        parsed.Scalar("objective").Should().Be("| the pipe is text");
        parsed.Scalar("project").Should().Be("hall9k");
    }

    [Fact]
    public void A_sequence_reads_its_items_as_scalars_too()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("criteria:\n  - \"quoted one\"\n  - plain two\n");

        parsed.List("criteria").Should().Equal("quoted one", "plain two");
    }

    [Fact]
    public void A_sequence_item_may_itself_be_a_block_scalar()
    {
        Frontmatter parsed = FrontmatterYaml.Parse(
            "criteria:\n  - |-\n    a criterion: with a colon\n  - second\n");

        parsed.List("criteria").Should().Equal("a criterion: with a colon", "second");
    }

    [Fact]
    public void A_flow_sequence_reads_as_a_list_and_an_empty_one_as_no_items()
    {
        FrontmatterYaml.Parse("blocked-by-issues: [81, 82]\n").List("blocked-by-issues")
            .Should().Equal("81", "82");
        FrontmatterYaml.Parse("blocked-by-issues: []\n").List("blocked-by-issues")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_key_written_in_both_sequence_forms_loses_neither()
    {
        // Neither writer produces this, and a reader that preferred one form would silently lose
        // whichever items the other one declared.
        Frontmatter parsed = FrontmatterYaml.Parse("blocked-by-issues: [81]\n\n- 99\n");

        parsed.List("blocked-by-issues").Should().Equal("81", "99");
    }

    [Fact]
    public void A_plain_value_that_merely_opens_with_a_bracket_stays_the_string_it_is()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("objective: [BUG] the login times out\n");

        parsed.Scalar("objective").Should().Be("[BUG] the login times out");
    }

    [Fact]
    public void An_inline_comma_list_and_its_items_both_reach_the_file_formats_own_reading()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("blocked-by: aaa, bbb\n- ccc\n");

        parsed.ListOrInline("blocked-by").Should().Equal("aaa", "bbb", "ccc");
    }

    [Fact]
    public void A_delimited_document_hands_back_the_body_under_the_closing_marker()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("---\nproject: hall9k\n---\n\nThe agent context.\n");

        parsed.Scalar("project").Should().Be("hall9k");
        parsed.Body.Should().Be("The agent context.");
    }

    [Fact]
    public void An_undelimited_document_is_frontmatter_all_the_way_down_and_has_no_body()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("project: hall9k\ntype: feature\n");

        parsed.Body.Should().BeNull();
        parsed.Scalar("type").Should().Be("feature");
    }

    [Fact]
    public void Flags_and_numbers_read_the_words_yaml_reads_them_as()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("pre-approved: true\nsession-cap: 4\nmodel: opus\n");

        parsed.Flag("pre-approved").Should().BeTrue();
        parsed.Number("session-cap").Should().Be(4);
        parsed.Flag("model").Should().BeNull("a value that is not a boolean says nothing about one");
        parsed.Number("model").Should().BeNull();
    }

    [Theory]
    [InlineData("a plain objective")]
    [InlineData("one with a colon: and more")]
    [InlineData("one with a trailing hash # like this")]
    [InlineData("[BUG] one opening with a bracket")]
    [InlineData("- one opening with a dash")]
    [InlineData("one\nwith\nnewlines")]
    [InlineData("one ending in a newline\n")]
    [InlineData("one ending in two newlines\n\n")]
    [InlineData("  one opening with spaces")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("")]
    public void What_the_writer_writes_is_what_the_reader_reads_back(string value)
    {
        string written = FrontmatterYaml.WriteScalar("objective", value) + "project: hall9k\n";

        Frontmatter parsed = FrontmatterYaml.Parse(written);

        parsed.Scalar("objective").Should().Be(value.Length == 0 ? null : value);
        parsed.Scalar("project").Should().Be("hall9k", "the key after the value is still reachable");
        written.Should().NotContain("\"", "the writer never quotes — that is the whole point of it");
    }

    [Theory]
    [InlineData("a plain criterion")]
    [InlineData("one with a colon: and more")]
    [InlineData("one\nwith a newline")]
    // A criterion ending in two or more newlines is written as a |+ block item, whose trailing
    // blank lines the block reader hands back to the scanner. Those blank lines used to close the
    // open sequence, so every item after this one was dropped (independent pre-PR review, cycle 1).
    [InlineData("one ending in two newlines\n\n")]
    public void A_written_list_item_round_trips_too(string value)
    {
        string written = "criteria:\n" + FrontmatterYaml.WriteListItem(value, FrontmatterYaml.BlockIndent)
            + FrontmatterYaml.WriteListItem("second", FrontmatterYaml.BlockIndent)
            + "project: hall9k\n";

        Frontmatter parsed = FrontmatterYaml.Parse(written);

        parsed.List("criteria").Should().Equal(value, "second");
        parsed.Scalar("project").Should().Be("hall9k");
    }

    /// <summary>
    /// YAML permits a block sequence's items to be separated by blank lines, and a hand-annotated
    /// record block routinely has them. A blank line used to close the open sequence, so every item
    /// after it was dropped silently — a readiness contract shorter than the one the origin wrote,
    /// with nothing said about the loss (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_blank_line_between_sequence_items_drops_none_of_them()
    {
        Frontmatter parsed = FrontmatterYaml.Parse(
            "criteria:\n  - first criterion\n\n  - second criterion\n\n  - third: with a colon\n"
            + "project: hall9k\n");

        parsed.List("criteria").Should().Equal(
            "first criterion", "second criterion", "third: with a colon");
        parsed.Scalar("project").Should().Be("hall9k");
        parsed.Scalar("third").Should().BeNull(
            "an item carrying a colon is an item, never a stray top-level key");
    }

    [Fact]
    public void A_blank_line_before_the_next_key_still_closes_nothing_it_should_not()
    {
        Frontmatter parsed = FrontmatterYaml.Parse(
            "criteria:\n  - only criterion\n\nproject: hall9k\n\nobjective: adopt the issue\n");

        parsed.List("criteria").Should().Equal("only criterion");
        parsed.Scalar("project").Should().Be("hall9k");
        parsed.Scalar("objective").Should().Be("adopt the issue");
    }

    [Fact]
    public void A_key_with_no_value_and_no_items_reads_as_a_present_empty_list()
    {
        Frontmatter parsed = FrontmatterYaml.Parse("criteria:\nproject: hall9k\n");

        parsed.Has("criteria").Should().BeTrue();
        parsed.List("criteria").Should().BeEmpty();
        parsed.Scalar("project").Should().Be("hall9k");
    }
}
