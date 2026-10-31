using FluentAssertions;
using Hall9k.Connectors.Text;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The Linear guard on pull request text: a pure function on the raw text that puts the neutral word
/// <c>ticket</c> between a linking word and an identifier it must not reach. The shapes come from the
/// matcher Linear publishes in its linear-release tool, and the word lists from Linear's documentation;
/// none of this touches Linear, which these functions never call.
/// </summary>
public sealed class RelayedTextLinearTests
{
    private static readonly IReadOnlySet<string> Teams = new HashSet<string> { "ENG", "DES", "OPS" };

    private static string Guard(string text, string? own = null, IReadOnlySet<string>? teams = null) =>
        RelayedText.WithoutLinearMagicWords(text, own, teams ?? Teams);

    [Theory]
    [InlineData("Fixes ENG-123, DES-5, and ENG-256", null, "Fixes ticket ENG-123, DES-5, and ENG-256")]
    [InlineData("Fixes: DES-5", null, "Fixes: ticket DES-5")]
    [InlineData("does not close ENG-2", null, "does not close ticket ENG-2")]
    [InlineData("Fixes ENG-123, DES-5", "ENG-123", "Fixes ENG-123, ticket DES-5")]
    [InlineData("Fixes DES-5, ENG-123", "ENG-123", "Fixes ticket DES-5, ENG-123")]
    [InlineData("Fixes ENG-123 and ENG-123", "ENG-123", "Fixes ENG-123 and ENG-123")]
    [InlineData("Fixes eng-123", "ENG-123", "Fixes eng-123")]
    [InlineData("Fixes ENG-0123", "ENG-123", "Fixes ENG-0123")]
    [InlineData("FIXES DES-5 & OPS-9", null, "FIXES ticket DES-5 & OPS-9")]
    [InlineData("closes DES-5 DES-6", null, "closes ticket DES-5 DES-6")]
    [InlineData("Resolves:DES-5", null, "Resolves:ticket DES-5")]
    [InlineData("Fixes \t : \t DES-5", null, "Fixes \t : \t ticket DES-5")]
    [InlineData("Fixes\u00A0DES-5", null, "Fixes\u00A0ticket DES-5")]
    [InlineData("Fixes ENG-123,\u2003DES-5", "ENG-123", "Fixes ENG-123,\u2003ticket DES-5")]
    public void A_linking_word_directly_before_another_issue_gets_the_neutral_word(
        string text, string? own, string expected) =>
        Guard(text, own).Should().Be(expected);

    [Theory]
    [InlineData("close"), InlineData("closes"), InlineData("closed"), InlineData("closing")]
    [InlineData("fix"), InlineData("fixes"), InlineData("fixed"), InlineData("fixing")]
    [InlineData("resolve"), InlineData("resolves"), InlineData("resolved"), InlineData("resolving")]
    [InlineData("complete"), InlineData("completes"), InlineData("completed"), InlineData("completing")]
    [InlineData("implement"), InlineData("implements"), InlineData("implemented"), InlineData("implementing")]
    [InlineData("Linear issue"), InlineData("linear   issue")]
    [InlineData("ref"), InlineData("refs"), InlineData("references"), InlineData("part of"), InlineData("Part  Of")]
    [InlineData("contributes to"), InlineData("toward"), InlineData("towards"), InlineData("updates")]
    [InlineData("skip"), InlineData("ignore")]
    public void Every_word_in_the_list_is_guarded_in_any_case(string word)
    {
        Guard($"{word} DES-5").Should().Be($"{word} ticket DES-5");
        Guard($"{word.ToUpperInvariant()} DES-5").Should().Be($"{word.ToUpperInvariant()} ticket DES-5");
    }

    [Theory]
    [InlineData("skip ENG-123", "ENG-123", "skip ticket ENG-123")]
    [InlineData("Ignore: ENG-123, DES-5", "ENG-123", "Ignore: ticket ENG-123, DES-5")]
    [InlineData("IGNORE ENG-123", "ENG-123", "IGNORE ticket ENG-123")]
    public void The_unlinking_words_are_guarded_before_the_tasks_own_identifier_too(
        string text, string own, string expected) =>
        Guard(text, own).Should().Be(expected);

    [Theory]
    [InlineData("Fixes ENG-123", "ENG-123")]
    [InlineData("Part of ENG-123", "ENG-123")]
    [InlineData("Relates to DES-5", "ENG-123")]
    [InlineData("related to DES-5", "ENG-123")]
    [InlineData("Relates to ENG-9 and DES-5", null)]
    public void A_closing_word_before_the_tasks_own_identifier_alone_and_the_relation_words_are_left_alone(
        string text, string? own) =>
        Guard(text, own).Should().Be(text);

    [Theory]
    [InlineData("Fixes\nDES-5")]
    [InlineData("Fixes `DES-5`")]
    [InlineData("Fixes the login. DES-5 is next")]
    [InlineData("Fixes DES-5.1 of the spec")]
    [InlineData("Fixes DES-12345678901")]
    [InlineData("bugfix DES-5")]
    [InlineData("Fixes ABCDEFGH-5")]
    [InlineData("Fixes ticket DES-5")]
    [InlineData("Fixes (DES-5)")]
    [InlineData("Fixes #12")]
    public void Text_that_is_not_a_linking_word_directly_before_an_identifier_is_left_alone(string text) =>
        Guard(text).Should().Be(text);

    [Theory]
    [InlineData("Closes https://linear.app/hall9k/issue/DES-5", "Closes ticket https://linear.app/hall9k/issue/DES-5")]
    [InlineData("Closes https://linear.app/hall9k/issue/DES-5/fix-the-login-page", "Closes ticket https://linear.app/hall9k/issue/DES-5/fix-the-login-page")]
    [InlineData("Closes [DES-5](https://linear.app/hall9k/issue/DES-5/slug)", "Closes ticket [DES-5](https://linear.app/hall9k/issue/DES-5/slug)")]
    [InlineData("Closes [the login bug](https://linear.app/hall9k/issue/DES-5)", "Closes ticket [the login bug](https://linear.app/hall9k/issue/DES-5)")]
    [InlineData("Closes [DES-5](<DES-5>)", "Closes ticket [DES-5](<DES-5>)")]
    [InlineData("Closes ENG-1, https://linear.app/hall9k/issue/DES-5/slug and OPS-3", "Closes ticket ENG-1, https://linear.app/hall9k/issue/DES-5/slug and OPS-3")]
    public void A_target_is_an_identifier_an_issue_url_or_a_markdown_link_to_either(string text, string expected) =>
        Guard(text).Should().Be(expected);

    [Fact]
    public void The_guard_reads_the_raw_text_so_a_phrase_inside_a_code_span_or_a_fence_is_guarded_like_any_other()
    {
        Guard("The old text said `Fixes DES-5` here.").Should().Be("The old text said `Fixes ticket DES-5` here.");
        Guard("```text\nResolves DES-5\n```").Should().Be("```text\nResolves ticket DES-5\n```");
        Guard("~~~\nPart of [DES-5](https://linear.app/hall9k/issue/DES-5)\n~~~")
            .Should().Be("~~~\nPart of ticket [DES-5](https://linear.app/hall9k/issue/DES-5)\n~~~");
    }

    [Fact]
    public void Each_line_is_read_on_its_own()
    {
        Guard("Fixes DES-5\nCloses OPS-1, ENG-9\nPart of ENG-123", "ENG-123")
            .Should().Be("Fixes ticket DES-5\nCloses ticket OPS-1, ENG-9\nPart of ENG-123");
    }

    [Theory]
    [InlineData("Fixes ENG-123, DES-5, and ENG-256", null)]
    [InlineData("Fixes: DES-5 and `Closes OPS-1`", "ENG-123")]
    [InlineData("ignore ENG-123 skip DES-5", "ENG-123")]
    [InlineData("Linear issue DES-5 and ENG-NEW", null)]
    public void The_guard_is_idempotent(string text, string? own)
    {
        string once = Guard(text, own);

        Guard(once, own).Should().Be(once);
    }

    [Fact]
    public void The_inserted_word_is_never_issue_because_linear_issue_is_a_closing_phrase()
    {
        string guarded = Guard("Fixes DES-5 and closes OPS-1");

        guarded.Should().NotContainEquivalentOf("issue");
        guarded.Should().Contain("ticket");
    }

    [Fact]
    public void A_token_that_only_looks_like_a_key_is_left_alone_when_the_teams_are_known_and_guarded_by_shape_when_they_are_not()
    {
        Guard("Fix SHA-256 hashing, UTF-8, and CVE-2024-1").Should().Be("Fix SHA-256 hashing, UTF-8, and CVE-2024-1");
        RelayedText.WithoutLinearMagicWords("Fixes SHA-256", null, null)
            .Should().Be("Fixes ticket SHA-256", "a failed team read matches by shape");
    }

    [Fact]
    public void Team_keys_are_compared_without_regard_to_case()
    {
        Guard("Fixes des-5").Should().Be("Fixes ticket des-5");
    }

    [Theory]
    [InlineData("Create ENG-NEW for it", "Create ENG NEW for it")]
    [InlineData("ENG-NEW and OPS-NEW", "ENG NEW and OPS NEW")]
    [InlineData("a brand-new approach", "a brand-new approach")]
    [InlineData("Brand-NEW approach", "Brand-NEW approach")]
    [InlineData("eng-new", "eng-new")]
    [InlineData("XYZ-NEW", "XYZ-NEW")]
    [InlineData("ENG-NEWS", "ENG-NEWS")]
    public void A_team_NEW_token_loses_its_hyphen_so_it_cannot_create_an_issue(string text, string expected) =>
        Guard(text).Should().Be(expected);

    [Fact]
    public void A_NEW_token_is_matched_by_shape_when_the_team_read_failed()
    {
        RelayedText.WithoutLinearMagicWords("XYZ-NEW and brand-new", null, null).Should().Be("XYZ NEW and brand-new");
    }

    [Theory]
    [InlineData("Fixes ENG-123", "ENG-123", false)]
    [InlineData("Work item: [ENG-123](https://linear.app/hall9k/issue/ENG-123/slug)", "ENG-123", false)]
    [InlineData("Fix SHA-256 hashing", "ENG-123", true)]
    [InlineData("Fixes DES-5", "ENG-123", true)]
    [InlineData("https://linear.app/hall9k/issue/DES-5/slug", "ENG-123", true)]
    [InlineData("Work item: [ENG-123](https://linear.app/acme-2/issue/ENG-123)", "ENG-123", false)]
    [InlineData("https://linear.app/hall9k/issue/ENG-123/fix-sha-256-hashing", "ENG-123", false)]
    [InlineData("https://linear.app/acme-2/issue/DES-5", "ENG-123", true)]
    [InlineData("ENG-NEW", "ENG-123", true)]
    [InlineData("a brand-new thing", "ENG-123", false)]
    [InlineData("nothing here", null, false)]
    public void Only_text_with_a_key_shaped_token_other_than_the_tasks_own_is_worth_asking_Linear_about(
        string text, string? own, bool expected) =>
        RelayedText.HasLinearKeyShape(text, own).Should().Be(expected);

    [Theory]
    [InlineData("DES-5: Fix login", "Fix login")]
    [InlineData("[DES-5] Fix login", "Fix login")]
    [InlineData("(DES-5) Fix login", "Fix login")]
    [InlineData("`DES-5` Fix login", "Fix login")]
    [InlineData("Fix login (DES-5)", "Fix login")]
    [InlineData("Fix login [DES-5]", "Fix login")]
    [InlineData("Fix login - DES-5", "Fix login")]
    [InlineData("Fix DES-5 login", "Fix login")]
    [InlineData("Fix DES-5: login", "Fix login")]
    [InlineData("DES-5, OPS-9 Fix login", "Fix login")]
    [InlineData("Fix login DES-5, OPS-9", "Fix login")]
    [InlineData("[DES-5](https://linear.app/hall9k/issue/DES-5/slug) Fix login", "Fix login")]
    [InlineData("Fix login https://linear.app/hall9k/issue/DES-5", "Fix login")]
    [InlineData("Fixes DES-5 #12", "Fixes #12")]
    [InlineData("des-5: Fix login", "Fix login")]
    [InlineData("Port login. Fixes DES-5.", "Port login. Fixes.")]
    public void Other_issues_are_dropped_from_a_title_with_whatever_only_framed_them(string title, string expected)
    {
        RelayedText.WithoutOtherLinearIdentifiers(title, "ENG-123", new HashSet<string> { "DES", "OPS" })
            .Should().Be(expected);
    }

    [Fact]
    public void An_identifier_that_only_extends_the_tasks_own_is_another_issue_and_is_dropped()
    {
        RelayedText.WithoutOtherLinearIdentifiers("ENG-1234 compatibility", "ENG-123", Teams)
            .Should().Be("compatibility");
    }

    [Theory]
    [InlineData("ENG-123: Fix login")]
    [InlineData("Fix SHA-256 hashing")]
    [InlineData("Fix UTF-8 handling")]
    [InlineData("Fix login  with two spaces")]
    public void A_title_naming_no_other_issue_comes_back_byte_for_byte(string title)
    {
        RelayedText.WithoutOtherLinearIdentifiers(title, "ENG-123", Teams).Should().BeSameAs(title);
    }

    [Fact]
    public void A_title_made_only_of_another_issue_comes_back_with_no_words()
    {
        RelayedText.WithoutOtherLinearIdentifiers("[DES-5]", "ENG-123", Teams).Should().BeEmpty();
        RelayedText.WithoutOtherLinearIdentifiers("DES-5: DES-6", null, Teams).Should().BeEmpty();
    }
}
