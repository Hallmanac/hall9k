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
}
