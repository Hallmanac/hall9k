using FluentAssertions;
using Hall9k.Domain.Features.PrReviewPreflight;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class PrReviewPreflightDiffExtractorTests
{
    private const string TwoFileDiff =
        "diff --git a/package.json b/package.json\n"
        + "index 1111111..2222222 100644\n"
        + "--- a/package.json\n"
        + "+++ b/package.json\n"
        + "@@ -1,2 +1,2 @@\n"
        + "-\"version\": \"1.0.0\"\n"
        + "+\"version\": \"1.0.1\"\n"
        + "diff --git a/src/App.cs b/src/App.cs\n"
        + "index 3333333..4444444 100644\n"
        + "--- a/src/App.cs\n"
        + "+++ b/src/App.cs\n"
        + "@@ -1,1 +1,1 @@\n"
        + "-old\n"
        + "+new\n";

    [Fact]
    public void No_matched_files_extracts_nothing()
    {
        PrReviewPreflightDiffExtractor.ExtractMatchedHunks(TwoFileDiff, []).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_diff_extracts_nothing()
    {
        PrReviewPreflightDiffExtractor.ExtractMatchedHunks(string.Empty, ["package.json"]).Should().BeEmpty();
    }

    [Fact]
    public void Only_the_matched_files_own_hunk_is_extracted()
    {
        string extracted = PrReviewPreflightDiffExtractor.ExtractMatchedHunks(TwoFileDiff, ["package.json"]);

        extracted.Should().Contain("package.json").And.Contain("\"version\": \"1.0.1\"");
        extracted.Should().NotContain("src/App.cs");
    }

    [Fact]
    public void A_long_extraction_is_capped_with_a_truncation_note()
    {
        string extracted = PrReviewPreflightDiffExtractor.ExtractMatchedHunks(
            TwoFileDiff, ["package.json"], maxCharacters: 10);

        extracted.Length.Should().BeGreaterThan(10, "the truncation note itself is appended after the cap");
        extracted.Should().Contain("capped at 10 characters");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, adversarial lens: the changed-file list is read back out
    /// of the same oid-pinned diff read rather than a second, separately-timed gh call, so this
    /// proves the extraction itself lists every file the diff's own headers name.
    /// </summary>
    [Fact]
    public void Every_files_path_in_the_diffs_own_headers_is_extracted()
    {
        PrReviewPreflightDiffExtractor.ExtractChangedFiles(TwoFileDiff).Should().Equal(
            "package.json", "src/App.cs");
    }

    [Fact]
    public void An_empty_diff_extracts_no_changed_files()
    {
        PrReviewPreflightDiffExtractor.ExtractChangedFiles(string.Empty).Should().BeEmpty();
    }

    /// <summary>
    /// Independent pre-PR review, cycle 7, adversarial lens: git quotes a header path that holds a
    /// non-ASCII byte under the default core.quotePath, octal-escaping each byte of the encoded
    /// character — a header a plain "a/... b/..." regex never matches at all, dropping the file
    /// outright.
    /// </summary>
    [Fact]
    public void A_quoted_header_path_with_an_octal_escaped_non_ascii_byte_is_still_extracted()
    {
        string diff =
            "diff --git \"a/.github/workflows/d\\303\\251pl.yml\" \"b/.github/workflows/d\\303\\251pl.yml\"\n"
            + "index 1111111..2222222 100644\n"
            + "--- \"a/.github/workflows/d\\303\\251pl.yml\"\n"
            + "+++ \"b/.github/workflows/d\\303\\251pl.yml\"\n"
            + "@@ -1,1 +1,1 @@\n"
            + "-old\n"
            + "+new\n";

        PrReviewPreflightDiffExtractor.ExtractChangedFiles(diff).Should().Equal(".github/workflows/dépl.yml");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 7, adversarial lens: an unquoted path that itself contains
    /// the literal " b/" separator makes the header ambiguous — the prior greedy regex always split
    /// at the last occurrence, misreading a new file's own name. The fix prefers the split where
    /// both halves are identical, which is every non-rename header, this one included.
    /// </summary>
    [Fact]
    public void A_new_files_own_path_containing_the_literal_separator_is_not_misread()
    {
        string diff =
            "diff --git a/.github/actions/setup b/action.yml b/.github/actions/setup b/action.yml\n"
            + "new file mode 100644\n"
            + "index 0000000..1111111\n"
            + "--- /dev/null\n"
            + "+++ b/.github/actions/setup b/action.yml\n"
            + "@@ -0,0 +1,1 @@\n"
            + "+new\n";

        PrReviewPreflightDiffExtractor.ExtractChangedFiles(diff).Should().Equal(
            ".github/actions/setup b/action.yml");
    }
}
