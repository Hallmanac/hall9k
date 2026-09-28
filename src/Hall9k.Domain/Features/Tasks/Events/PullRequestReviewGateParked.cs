namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// This pr-review task was minted and published, but deliberately never assigned, because the
/// membership gate found its pull request's own author, the mentioning comment's own author (a
/// mention-triggered mint only), or both, was not a declared hall9k team member (or was a Bot) on
/// a repository the gate covers (security review idea 6be68ee2, finding 1; independent pre-PR
/// review, cycle 3, conformance lens, added the pull request's own author to the mention trigger's
/// own check). An unassigned task never dispatches — no worktree, branch, or session exists — so
/// this is the pre-checkout park: the human go is the existing <c>h9k task assign</c>, nothing new.
/// <para>
/// Every field here is a deterministic fact carried at mint time, never a model session's own
/// summary (tools before tokens): <see cref="AuthorLogin"/>, <see cref="AuthorAccountId"/> and
/// <see cref="AuthorAssociation"/> are GitHub's own reading of the pull request's own author, read
/// at zero new calls off the same timeline or mention query the mint itself already paid for —
/// never the mentioning comment's own author, which is already on the task's own
/// <c>PullRequestReviewMentionObserved</c> event and this outcome's own log line.
/// <see cref="AuthorAccountId"/> is what the gate actually matched on for a review-requested mint;
/// for a mention-triggered mint it is one of two signals the gate combined, so a card whose author
/// reads as a declared member can still be parked here, by the comment's own author instead —
/// carried beside <see cref="MemberAccountIds"/>, the project's own declared member ids as the gate
/// read them at this exact mint, so a deleted-and-recreated account is diagnosable from the card
/// alone: an operator can see whether the author's id is simply missing from the list, or whether
/// it once was one of these and the underlying account no longer exists.
/// <see cref="HeadOwner"/> and <see cref="IsCrossRepository"/> say whether the pull request's own
/// head is a fork; <see cref="IsPrivate"/> is the repository's own visibility this sweep read —
/// null when that read itself failed, which is what made the gate fail closed in the first place,
/// stated honestly here rather than guessed at (AGENTS.md, never guess at unobserved facts).
/// </para>
/// </summary>
/// <param name="MembersWithoutDeclaredAccount">
/// Every current project member none of whose nodes has declared a GitHub account at all — a
/// fleet on a version before v0.10.54, or one not restarted since (security review idea 6be68ee2,
/// finding 1). Named on the card so a member in exactly that state is diagnosable without first
/// suspecting the wrong thing: nothing here claims the pull request's own author IS one of these
/// members, only that these members could never have matched at all, declaration or not.
/// </param>
/// <param name="Title">
/// The pull request's own title at mint time (independent pre-PR review, cycle 1, conformance
/// lens) — read straight off the identical import <c>CreateOneAsync</c>/<c>CreateFromMentionAsync</c>
/// already pays for, never the task's own <c>Objective</c>, which this same security review made
/// platform-authored ("Review pull request owner/repo#N") for exactly the reason this field exists:
/// the objective is the one place an auto-mint prints everywhere with no fence around it, and the
/// park card is the one place that still owes the operator the pull request's own title. Optional,
/// defaulting to null, so a row an earlier build minted before this field existed still deserializes
/// — the card then says what could be observed, same as every other unread park fact.
/// </param>
public sealed record PullRequestReviewGateParked(
    Guid Id,
    string? AuthorLogin,
    long? AuthorAccountId,
    string? AuthorAssociation,
    string? HeadOwner,
    bool IsCrossRepository,
    bool? IsPrivate,
    int? ChangedFileCount,
    IReadOnlyList<long> MemberAccountIds,
    IReadOnlyList<string> MembersWithoutDeclaredAccount,
    DateTimeOffset ParkedAt,
    string? Title = null);
