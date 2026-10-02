using Hall9k.Connectors.Text;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>An unpersisted, in-process answer, so an enum rather than a closed vocabulary.</summary>
internal enum MentionRoute
{
    /// <summary>A live task of this owner's covers the pull request: the mention is attached to it, behind the existing follow-up gates.</summary>
    Attach,

    /// <summary>Only another owner's task, or one this node cannot attribute, covers it: nothing is touched or minted.</summary>
    CoveredByTeammate,

    /// <summary>Nothing live covers it and someone else wrote it: a full two-lens review is minted.</summary>
    MintReview,

    /// <summary>Nothing live covers it and this install's own login wrote it: only the bounded answer lap is minted.</summary>
    MintAnswer,
}

/// <summary>
/// What a comment mentioning this install's login on a pull request earns, decided purely over the
/// covering-task verdict and who wrote the pull request, with no database, no branch and no GitHub
/// (decision dce39370). The review-request trigger can never reach a pull request's own author, and
/// the author already holds the context a review of their own work would supply, so a mention on
/// the owner's own pull request starts a short lap that drafts a reply rather than a review. The
/// pull request's author is matched to this install's login case-insensitively, the comparison
/// <c>GitHubReviewAssignments.TryBuildMatch</c> already uses for its own self-author filter, and
/// the author comes from the read the sweep already made, so no new <c>gh</c> call is spent.
/// </summary>
internal static class MentionRouting
{
    /// <param name="coverage">The owner-scoped covering-task verdict, <see cref="OwnerScopedCoverage.Decide"/>'s.</param>
    /// <param name="pullRequestAuthorLogin">
    /// The pull request's author as the sweep's own read named it, or null when that read did not.
    /// An author nobody observed never reads as the owner: the full review a mention earned before
    /// this rule stands, since guessing at an unobserved fact would trade a review for a draft.
    /// </param>
    /// <param name="ownLogin">The login <c>gh</c> was authenticated as when the mention was observed.</param>
    public static MentionRoute Decide(ReviewCoverage coverage, string? pullRequestAuthorLogin, string ownLogin) =>
        coverage.Kind switch
        {
            ReviewCoverageKind.Own => MentionRoute.Attach,
            ReviewCoverageKind.Teammate or ReviewCoverageKind.Unattributable => MentionRoute.CoveredByTeammate,
            _ => pullRequestAuthorLogin.IsNotBlank()
                && string.Equals(pullRequestAuthorLogin, ownLogin, StringComparison.OrdinalIgnoreCase)
                    ? MentionRoute.MintAnswer
                    : MentionRoute.MintReview,
        };

    /// <summary>
    /// The platform-authored objective of an answer-only task: it names who is being answered and says
    /// the pull request is the owner's own, and never reads as a review.
    /// </summary>
    public static string AnswerObjective(string commentAuthorLogin, string pullRequestReference) =>
        $"Answer {RelayedText.OneLine(commentAuthorLogin).Trim()}'s comment on your own pull request {pullRequestReference}";

    /// <summary>What the answer-only task's one acceptance criterion says: the draft is walked, and posted only on the owner's go.</summary>
    public const string AnswerCriterion =
        "The drafted reply is walked with the owner (walk-pr-review-findings) and posted only on the owner's explicit go.";

    /// <summary>The provenance line an answer-only task's agent context carries in place of the review's own.</summary>
    public static string AnswerProvenance(string commentAuthorLogin, string taggedLogin, DateTimeOffset commentCreatedAt) =>
        $"GitHub mention observed: {commentAuthorLogin} tagged {taggedLogin} in a comment on this pull request at "
        + $"{commentCreatedAt:yyyy-MM-dd HH:mm:ss}Z. {taggedLogin} wrote this pull request, so this task answers "
        + "that one comment with a drafted reply for the owner to walk; it does not review the owner's own work.";
}
