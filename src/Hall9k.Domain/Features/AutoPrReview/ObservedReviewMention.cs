namespace Hall9k.Domain.Features.AutoPrReview;

/// <summary>
/// One GitHub comment this install has already acted on because it mentioned its own login (idea
/// 2f079bcd, auto-pr-review's second trigger) — permanent, unlike its sibling
/// <see cref="ObservedReviewRequest"/>: a comment never stops having been written, so this row is
/// never deleted the way a withdrawn review request's is. Its whole job is dedupe — "have I
/// already handled this exact comment id" — which is why every field beyond the id and the
/// outcome is provenance rather than something a later sweep re-reads.
/// <para>
/// One row per decider per comment, the identical reasoning <see cref="ObservedReviewRequest.ComputeId"/>
/// gives at length for why the node, project and login are all part of the key: two projects on
/// one node sharing a repository, or two installs sharing one database under the same <c>gh</c>
/// login, must each decide and record their own answer rather than overwrite the other's.
/// </para>
/// </summary>
public sealed class ObservedReviewMention
{
    /// <summary><see cref="ComputeId"/> — one row per decider per comment.</summary>
    public string Id { get; set; } = string.Empty;

    public Guid ObservingNodeId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary><c>owner/repo</c> as the observing project's own recorded URL spells it.</summary>
    public string Repository { get; set; } = string.Empty;

    public int Number { get; set; }

    public string PullRequestUrl { get; set; } = string.Empty;

    /// <summary>The login <c>gh</c> was authenticated as when this mention was observed — read fresh, never cached.</summary>
    public string MentionedLogin { get; set; } = string.Empty;

    /// <summary>The mentioning comment's own id, as GitHub reports it — the dedupe key's own payload.</summary>
    public string CommentId { get; set; } = string.Empty;

    public string CommentAuthorLogin { get; set; } = string.Empty;

    public string CommentUrl { get; set; } = string.Empty;

    /// <summary>GitHub's own timestamp for the comment — what the no-backfill cutoff compared against.</summary>
    public DateTimeOffset CommentCreatedAt { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public ReviewMentionOutcome Outcome { get; set; } = ReviewMentionOutcome.Unknown;

    /// <summary>What the outcome needs said in words to be actionable. Null when the outcome says it all.</summary>
    public string? OutcomeDetail { get; set; }

    /// <summary>The task this mention minted or attached to; null only when nothing was ever recorded against it (a held outcome).</summary>
    public Guid? TaskId { get; set; }

    /// <summary>
    /// Keyed on the decider and the comment together — see the class doc for why the node,
    /// project and login are part of it. Repository and login are lower-cased for the identical
    /// casing reason <see cref="ObservedReviewRequest.ComputeId"/> gives; the comment id is kept
    /// verbatim, since GitHub's own comment ids are opaque and case-significant.
    /// </summary>
    public static string ComputeId(
        Guid observingNodeId, Guid projectId, string repository, int number, string mentionedLogin, string commentId) =>
        $"{observingNodeId:N}-{projectId:N}-{repository.ToLowerInvariant()}#{number}@{mentionedLogin.ToLowerInvariant()}:{commentId}";
}
