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

    /// <summary>
    /// Markdown's single-asterisk emphasis is itself Jira wiki markup's own bold notation, so
    /// leaving it unconverted silently reassigned italic to bold on the rendered card rather than
    /// merely failing to italicize (independent pre-PR review, conformance lens, cycle 8).
    /// </summary>
    [Fact]
    public void Single_asterisk_italics_become_a_Jira_underscore_span()
    {
        JiraMarkupText.FromMarkdown("the *optional* field").Should().Be("the _optional_ field");
    }

    [Fact]
    public void Single_asterisk_italics_and_double_asterisk_bold_both_convert_in_the_same_text()
    {
        JiraMarkupText.FromMarkdown("*italic* and **bold**").Should().Be("_italic_ and *bold*");
    }

    /// <summary>
    /// A bare asterisk with whitespace on either side — arithmetic, an ordinary sentence — is not
    /// emphasis, so it is left alone the same way the paired wiki-markup marks (hyphen, underscore,
    /// caret, tilde) already require real flanking rather than a bare character occurrence.
    /// </summary>
    [Fact]
    public void An_unflanked_asterisk_is_not_read_as_italic()
    {
        JiraMarkupText.FromMarkdown("3 * 4 = 12").Should().Be("3 * 4 = 12");
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
    public void A_fenced_code_block_with_a_hyphenated_language_tag_still_matches_its_own_fence()
    {
        // \w* could not consume "objective-c" past its hyphen, so the opening fence never matched
        // at all and the engine paired the block's own closing fence with the next unrelated
        // block's opening fence instead, wrapping prose in {code} and leaving the real code
        // markdown-converted (independent pre-PR review, adversarial lens, cycle 10).
        JiraMarkupText.FromMarkdown("```objective-c\nint a = *b*;\n```\nSome prose here.\n```json\n{\"x\": 1}\n```")
            .Should().Be("{code:objective-c}\nint a = *b*;\n{code}\nSome prose here.\n{code:json}\n{\"x\": 1}\n{code}");
    }

    [Fact]
    public void A_fenced_code_block_with_a_hash_language_tag_still_matches_its_own_fence()
    {
        JiraMarkupText.FromMarkdown("```c#\nvar x = 1;\n```")
            .Should().Be("{code:c#}\nvar x = 1;\n{code}");
    }

    [Fact]
    public void A_fenced_code_block_with_trailing_whitespace_after_the_language_tag_still_matches()
    {
        JiraMarkupText.FromMarkdown("```bash \necho hi\n```")
            .Should().Be("{code:bash}\necho hi\n{code}");
    }

    /// <summary>
    /// CommonMark allows a fenced code block nested as a list item's own continuation, indented to
    /// match the item's content column — but FencedCodeBlock anchored both fences to column 0, so
    /// this never matched at all and fell through to ConvertProse, which markdown-converted the
    /// code sample's own contents line by line, turning an indented "- name: build" into a second-
    /// level Jira bullet (independent pre-PR review, adversarial lens, cycle 13). The fence's own
    /// leading indentation belongs to the surrounding list structure, not the code sample, so it is
    /// stripped from every body line before the block reaches Jira's {code} macro — leaving the
    /// sample's own internal relative indentation (the "args:" line here) untouched.
    /// </summary>
    [Fact]
    public void A_fenced_code_block_indented_as_a_list_item_continuation_is_recognized_and_dedented()
    {
        JiraMarkupText.FromMarkdown("  ```yaml\n  - name: build\n    args: [\"--flag\"]\n  ```")
            .Should().Be("{code:yaml}\n- name: build\n  args: [\"--flag\"]\n{code}");
    }

    /// <summary>
    /// The second trigger the same column-0 assumption had: an opening fence's indentation used to
    /// pair with any closing fence at all, indented or not, so a top-level opening fence with an
    /// indented closing fence (or the reverse) matched across everything in between and boxed
    /// unrelated prose inside a single {code} block. Requiring the closing fence to repeat the
    /// opening fence's own exact indentation means a genuine mismatch fails to match at all instead
    /// — here, nothing in the text matches as a fenced block, so it all passes through untouched,
    /// rather than the fenced block boundaries relocating onto whichever backtick line happens to
    /// pair next (independent pre-PR review, adversarial lens, cycle 13).
    /// </summary>
    [Fact]
    public void Fenced_code_blocks_whose_open_and_close_indentation_differ_do_not_pair_across_the_mismatch()
    {
        string markdown = "Intro line.\n\n  ```yaml\nfoo: 1\n```\n\nOutro line.";

        JiraMarkupText.FromMarkdown(markdown).Should().Be(markdown);
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
    public void ToPlainLiteral_passes_a_task_guid_and_hyphenated_prose_through_unwrapped()
    {
        // The cycle-2 predicate keyed on bare character membership, so any hyphen — a task id's
        // GUID, an ordinary hyphenated word — tripped the same box the merge comment was carved
        // out to avoid: this is the exact text CloseoutEngine.MergeComment composes (independent
        // pre-PR review, adversarial lens, cycle 3).
        string text =
            "Recorded by Hall9k as task 28b19893-0000-4000-8000-000000000000 in project sample. "
            + "This is a one-off note at merge.";
        JiraMarkupText.ToPlainLiteral(text).Should().Be(text);
    }

    [Fact]
    public void ToPlainLiteral_passes_an_underscored_repository_name_through_unwrapped()
    {
        // Cycle 5's conformance finding: the cycle-3 fix carved the hyphen alone out of the bare
        // character rule, but underscore, plus, caret, and tilde are the same paired-flanking-
        // emphasis shape and were left trusting bare membership, so a merge comment naming a
        // GitHub repo with an underscore in it (common on GitHub) was still boxed, turning its
        // pull request URL from an auto-link into dead preformatted text.
        string text =
            "The pull request for this work has merged: "
            + "https://github.com/my_org/my_repo/pull/12 in project sample_project.";
        JiraMarkupText.ToPlainLiteral(text).Should().Be(text);
    }

    [Theory]
    [InlineData("h1. Rollback plan")]
    [InlineData("bq. per the incident channel")]
    [InlineData("Deployed (y)")]
    [InlineData("Verified :) all green")]
    [InlineData("??attributed??")]
    [InlineData("Screenshot: !file.png!")]
    [InlineData("This is -deleted- text")]
    [InlineData("This is _emphasised_ text")]
    [InlineData("This is +underlined+ text")]
    [InlineData("This is ^raised^ text")]
    [InlineData("This is ~lowered~ text")]
    public void ToPlainLiteral_wraps_text_carrying_a_construct_the_old_character_class_missed(string text)
    {
        // Headings, blockquotes, emoticons, citations, and image embeds use no character the
        // cycle-2 class even listed, so "plain" text containing one of them passed through
        // unwrapped and Jira rendered the construct instead of the literal text (independent
        // pre-PR review, adversarial lens, cycle 3). A genuine -strikethrough-/_italic_/
        // +underline+/^superscript^/~subscript~ (paired, word-flanked marks) still has to trigger
        // the box too, now that none of those five marks trusts a bare occurrence alone (cycle 5).
        JiraMarkupText.ToPlainLiteral(text).Should().Be($"{{noformat}}\n{text}\n{{noformat}}");
    }

    [Fact]
    public void ToPlainLiteral_passes_a_bare_issue_number_through_unwrapped()
    {
        // A bare "#" is only Jira's numbered-list marker at line start; anywhere else it is an
        // ordinary character an issue number or a hashtag needs, so a plain-composed comment
        // naming one was boxed in {noformat} for no real construct — the same over-triggering
        // cycle 3 carved the hyphen out for (independent pre-PR review, adversarial lens, cycle 10).
        string text = "Deployed behind the flag; see PR #38 for the rollback path.";
        JiraMarkupText.ToPlainLiteral(text).Should().Be(text);
    }

    [Fact]
    public void ToPlainLiteral_still_wraps_a_line_start_numbered_list_marker()
    {
        JiraMarkupText.ToPlainLiteral("# not a list").Should().Be("{noformat}\n# not a list\n{noformat}");
    }

    [Fact]
    public void ToPlainLiteral_returns_blank_text_unchanged()
    {
        JiraMarkupText.ToPlainLiteral(string.Empty).Should().Be(string.Empty);
    }
}
