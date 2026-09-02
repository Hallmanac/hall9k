using FluentAssertions;
using Hall9k.Connectors.Text;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The constructs a composing session's own card-authoring skills actually produce, converted from
/// markdown to the wiki markup Jira's v2 API renders a plain-string description or comment
/// through — the conversion the old twg transport did on hall9k's behalf via
/// <c>--description-format</c>/<c>--body-format</c>, and which the REST client now has to do
/// itself (Decisions Log #114, independent pre-PR review cycle 1).
/// </summary>
public sealed class JiraMarkupTextTests
{
    [Fact]
    public void Headings_become_Jira_heading_levels()
    {
        JiraMarkupText.FromMarkdown("# One\n## Two\n### Three")
            .Should().Be("h1. One\nh2. Two\nh3. Three");
    }

    [Fact]
    public void Bullets_and_numbered_items_become_Jira_list_markers()
    {
        JiraMarkupText.FromMarkdown("- one\n- two\n1. first\n2. second")
            .Should().Be("* one\n* two\n# first\n# second");
    }

    [Fact]
    public void Bold_text_becomes_a_single_asterisk_span()
    {
        JiraMarkupText.FromMarkdown("This is **important** text").Should().Be("This is *important* text");
    }

    [Fact]
    public void Underscore_italics_pass_through_unchanged_because_the_syntax_already_matches()
    {
        JiraMarkupText.FromMarkdown("This is _emphasised_ text").Should().Be("This is _emphasised_ text");
    }

    [Fact]
    public void Inline_code_becomes_a_monospace_span()
    {
        JiraMarkupText.FromMarkdown("Run `h9k status` first").Should().Be("Run {{h9k status}} first");
    }

    [Fact]
    public void A_link_becomes_a_pipe_delimited_Jira_link()
    {
        JiraMarkupText.FromMarkdown("See [the docs](https://example.com/docs)")
            .Should().Be("See [the docs|https://example.com/docs]");
    }

    [Fact]
    public void A_blockquote_becomes_a_bq_line()
    {
        JiraMarkupText.FromMarkdown("> quoted text").Should().Be("bq. quoted text");
    }

    [Fact]
    public void A_fenced_code_block_keeps_its_contents_untouched_but_renames_the_fence()
    {
        JiraMarkupText.FromMarkdown("```csharp\nvar x = 1;\n## not a heading\n```")
            .Should().Be("{code:csharp}\nvar x = 1;\n## not a heading\n{code}");
    }

    [Fact]
    public void A_fenced_code_block_with_no_language_uses_the_bare_code_macro()
    {
        JiraMarkupText.FromMarkdown("```\nplain\n```").Should().Be("{code}\nplain\n{code}");
    }

    [Fact]
    public void A_given_when_then_block_converts_headings_and_bullets_together()
    {
        JiraMarkupText.FromMarkdown("## Acceptance criteria\n- **Given** a task\n- **When** it merges")
            .Should().Be("h2. Acceptance criteria\n* *Given* a task\n* *When* it merges");
    }

    [Fact]
    public void Blank_text_is_returned_unchanged()
    {
        JiraMarkupText.FromMarkdown(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Markdown_written_literally_inside_inline_code_survives_unconverted()
    {
        // Bold conversion used to run before inline-code conversion, so markdown syntax written
        // literally inside a backtick span got rewritten before the code span ever wrapped it
        // (independent pre-PR review, adversarial lens, cycle 1 — a ride-along).
        JiraMarkupText.FromMarkdown("Use `**bold**` for emphasis").Should().Be("Use {{**bold**}} for emphasis");
    }

    [Fact]
    public void A_link_shaped_pattern_written_literally_inside_inline_code_survives_unconverted()
    {
        JiraMarkupText.FromMarkdown("Use `[text](url)` for a link").Should().Be("Use {{[text](url)}} for a link");
    }

    [Fact]
    public void Bold_text_wrapping_an_inline_code_span_converts_both()
    {
        // The sequential-segments version of ConvertInline split a span like this at the code
        // boundary, so neither the bold nor the code half matched (independent pre-PR review,
        // adversarial lens, cycle 2).
        JiraMarkupText.FromMarkdown("**Use the `--file` flag**").Should().Be("*Use the {{--file}} flag*");
    }

    [Fact]
    public void A_link_wrapping_an_inline_code_span_converts_both()
    {
        JiraMarkupText.FromMarkdown("[the `--file` flag](https://example.com)")
            .Should().Be("[the {{--file}} flag|https://example.com]");
    }

    [Fact]
    public void ToPlainLiteral_wraps_text_in_a_noformat_block()
    {
        JiraMarkupText.ToPlainLiteral("## not a heading here").Should().Be("{noformat}\n## not a heading here\n{noformat}");
    }

    [Fact]
    public void ToPlainLiteral_passes_text_through_unwrapped_when_it_carries_no_wiki_markup_active_character()
    {
        // Closeout's own merge comment is exactly this shape: plain prose plus a bare URL, with
        // nothing in it Jira wiki markup ever assigns meaning to. Wrapping it in {noformat}
        // anyway regressed the URL from an auto-linked sentence into dead text inside a
        // preformatted block (independent pre-PR review, adversarial lens, cycle 2).
        string text = "The pull request for this work has merged: https://github.com/hall9k/hall9k/pull/1";
        JiraMarkupText.ToPlainLiteral(text).Should().Be(text);
    }

    [Fact]
    public void ToPlainLiteral_returns_blank_text_unchanged()
    {
        JiraMarkupText.ToPlainLiteral(string.Empty).Should().Be(string.Empty);
    }
}
