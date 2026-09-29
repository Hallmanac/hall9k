namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Recognizes GitHub's own diff-endpoint file-count ceiling (documented at 300 changed files),
/// reported by gh as an HTTP 406 whose body reads "the diff exceeded the maximum number of files
/// (300)" — the one <c>gh pr diff</c> failure the pull-request review pre-flight
/// (<c>RunLauncher.DispatchPrReviewPreflightAsync</c>) treats as "this pull request cannot be read
/// this way" rather than a generic, permanently-failing launch error (independent pre-PR review,
/// cycle 1, adversarial lens: before this, a pull request touching more than 300 files — a
/// dependency bump, a generated-code refresh, a rename sweep — failed the pre-flight's launch on
/// every attempt and could never be reviewed at all).
/// </summary>
public static class GitHubDiffSizeLimitClassifier
{
    private const string Marker = "maximum number of files";

    public static bool IsDiffTooLarge(string? message) =>
        message is not null && message.Contains(Marker, StringComparison.OrdinalIgnoreCase);
}
