using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Finding a Linear identifier in a pull request's title or body, against the team keys the install's
/// connection can see. The connection read itself is pinned in <c>LinearTeamKeysTests</c>; this is the
/// pure half, which never calls Linear.
/// </summary>
public sealed class LinkedLinearIdentifierScanTests
{
    private const string Workspace = "https://linear.app/hall9k";
    private static readonly IReadOnlySet<string> Teams = new HashSet<string> { "ENG", "DES", "PLAT" };

    private static string? Find(string text, IReadOnlySet<string>? teams = null) =>
        LinkedWorkItemImport.FindLinearIdentifier(text, teams ?? Teams, Workspace);

    [Theory]
    [InlineData("Fix the login. Closes ENG-12.", "ENG-12")]
    [InlineData("see DES-5", "DES-5")]
    [InlineData("des-5 is the issue", "DES-5")]
    [InlineData("(PLAT-300)", "PLAT-300")]
    [InlineData("https://linear.app/hall9k/issue/DES-5", "DES-5")]
    [InlineData("https://linear.app/hall9k/issue/DES-5/fix-the-login-page", "DES-5")]
    [InlineData("[the bug](https://linear.app/hall9k/issue/PLAT-9/slug)", "PLAT-9")]
    [InlineData("ENG-0012", "ENG-12")]
    public void An_identifier_is_found_bare_or_inside_an_issue_url_with_or_without_its_slug(string text, string expected) =>
        Find(text).Should().Be(expected);

    [Theory]
    [InlineData("Handle UTF-8 input")]
    [InlineData("SHA-256 of the file")]
    [InlineData("tracked as CVE-2024-1")]
    [InlineData("RFC-7231 says so")]
    [InlineData("ENG-1.5 of the spec")]
    [InlineData("ABCDEFGH-5")]
    [InlineData("nothing at all")]
    public void A_token_that_merely_looks_like_a_key_is_not_found(string text) =>
        Find(text).Should().BeNull();

    [Fact]
    public void An_issue_from_a_team_other_than_the_projects_default_is_found()
    {
        Find("Part of PLAT-4", new HashSet<string> { "PLAT" }).Should().Be("PLAT-4");
    }

    [Fact]
    public void The_first_identifier_of_a_visible_team_wins_in_the_order_the_text_reads()
    {
        Find("SHA-256 first, then DES-5 and ENG-1").Should().Be("DES-5");
        Find("ENG-1 and https://linear.app/hall9k/issue/DES-5").Should().Be("ENG-1");
        Find("https://linear.app/hall9k/issue/DES-5 and ENG-1").Should().Be("DES-5");
    }

    [Fact]
    public void An_issue_url_from_another_workspace_is_passed_over_with_its_slug()
    {
        Find("https://linear.app/other-co/issue/DES-5/fix-eng-9-things").Should().BeNull();
        Find("https://linear.app/other-co/issue/DES-5 then ENG-2").Should().Be("ENG-2");
    }
}
