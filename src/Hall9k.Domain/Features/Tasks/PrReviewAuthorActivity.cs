using System.Globalization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What happened to a reviewed pull request between two looks at it (task: a pr-review task
/// stays open while the pull request's review threads are unresolved) — and the one line every
/// surface says it in.
/// <para>
/// A pure comparison of two watermarks, deliberately: it takes no store, no clock, and no provider,
/// so the whole decision "does this wake the reviewer, and what does it tell them" is testable
/// without Docker or a GitHub account. The daemon's follow-through sweep is its only caller
/// today, and the line it composes is what <c>h9k status</c> and <c>h9k task show</c> both render
/// off the task's own stream — one wording, one owner, so they cannot disagree.
/// </para>
/// </summary>
/// <param name="ReplyCount">Comments that landed in the reviewer's own threads, written by somebody other than them, since the previous look.</param>
/// <param name="ThreadsWithReplies">How many distinct threads those comments landed in.</param>
/// <param name="NewCommitCount">
/// Commits the pull request gained since the previous look, or null when either look reported no
/// commit count to compare — a push whose size is genuinely unknown, stated as unknown rather
/// than reported as zero (AGENTS.md: never guess at unobserved facts).
/// </param>
/// <param name="HeadMoved">Whether the pull request's head commit changed, which is what "pushed" is actually observed as.</param>
/// <param name="ReReviewNewlyRequested">
/// Whether the reviewer is being asked back — a review request standing against them now that was
/// not standing at the previous look. The one thing in here that is an explicit ask of the reviewer
/// rather than an inference from movement, which is why it wakes them on its own: an author who
/// resolves the threads themselves and clicks re-request review without a word or a push has said
/// "your turn" as plainly as any reply, and holding the wait open while the board still read
/// "waiting on its author" left the two of them each expecting the other (independent pre-PR
/// review, cycle 1, adversarial lens). Only the transition counts, never the standing request: the
/// re-baselining observation appended beside the wake records it, so the same ask cannot fire again
/// on the next poll — the dedup rule the reply counts already live by.
/// </param>
public sealed record PrReviewAuthorActivity(
    int ReplyCount, int ThreadsWithReplies, int? NewCommitCount, bool HeadMoved, bool ReReviewNewlyRequested)
{
    /// <summary>Nothing happened — the answer for a quiet poll.</summary>
    public static readonly PrReviewAuthorActivity None = new(0, 0, null, false, false);

    /// <summary>Whether this is worth waking the reviewer for at all.</summary>
    public bool Any => ReplyCount > 0 || HeadMoved || ReReviewNewlyRequested;

    /// <summary>
    /// The difference between two looks at the same pull request.
    /// <para>
    /// Both sides count REPLIES — comments waiting on the reviewer, everything after their own last
    /// word in each thread (<see cref="PrReviewThreadWatermark.ReplyCount"/>) — never comments as
    /// such. That is what lets the FIRST look count too, and it has to: the reviewer posts, the
    /// author answers a minute later, and the first poll is the one that sees it. Suppressing the
    /// whole first look instead swallowed that answer into the baseline and told nobody, ever —
    /// neither the board nor the scoped lap, whose anchor is that same first look (independent
    /// pre-PR review, cycle 1, both lenses). The self-wake that suppression was for cannot happen
    /// here at all, because a reviewer's own comments are not replies to them.
    /// </para>
    /// <para>
    /// A thread present in <paramref name="after"/> that <paramref name="before"/> has never seen
    /// contributes its whole reply count. A thread that vanished contributes nothing — a deleted
    /// comment or a thread outside a truncated page is an absence, not a reply. A thread whose
    /// reply count went DOWN contributes nothing either: that is the reviewer having said something
    /// in it since (their own comment moves the boundary), which is news about them and not about
    /// anybody answering them.
    /// </para>
    /// <para>
    /// The head half needs no baseline of its own: the head the review was posted against is
    /// recorded when the wait begins, so a push landing between the review and the first poll —
    /// minutes, easily — is genuinely observable on that first look.
    /// </para>
    /// <para>
    /// The re-review half is a transition and only a transition, for the reason
    /// <see cref="ReReviewNewlyRequested"/> states: <paramref name="reReviewBefore"/> is what the
    /// previous observation recorded, so a request that has been standing for a week is not
    /// re-announced every few minutes.
    /// </para>
    /// </summary>
    public static PrReviewAuthorActivity Between(
        IReadOnlyList<PrReviewThreadWatermark> before,
        IReadOnlyList<PrReviewThreadWatermark> after,
        string? headBefore,
        string? headAfter,
        int? commitsBefore,
        int? commitsAfter,
        bool reReviewBefore,
        bool reReviewAfter)
    {
        int replies = 0;
        int threads = 0;
        foreach (PrReviewThreadWatermark thread in after)
        {
            PrReviewThreadWatermark? previous = before.FirstOrDefault(
                candidate => candidate.ThreadId == thread.ThreadId);
            int gained = previous is null
                ? thread.ReplyCount
                : thread.ReplyCount - previous.ReplyCount;
            if (gained > 0)
            {
                replies += gained;
                threads++;
            }
        }

        // A moved head is the observation; the count is the nicety. Both looks must have reported a
        // count for the subtraction to mean anything, and a count that went DOWN — a force-push
        // that dropped commits — is real movement whose size cannot honestly be called a number of
        // new commits, so it is reported as a push with no count rather than as a negative one.
        bool headMoved = headBefore is not null && headAfter is not null && headBefore != headAfter;
        int? newCommits = headMoved && commitsBefore is { } from && commitsAfter is { } to && to > from
            ? to - from
            : null;

        return new PrReviewAuthorActivity(
            replies, threads, newCommits, headMoved, reReviewAfter && !reReviewBefore);
    }

    /// <summary>
    /// The one line the board, <c>h9k task show</c>, and the daemon's log all say. It names what
    /// changed and what is still outstanding, because "somebody replied" without the count of
    /// what is still open sends the reviewer to GitHub to find out.
    /// <para>
    /// It says the pull request moved rather than that its AUTHOR answered, because who wrote a
    /// reply is not what this comparison observed: the counts say a comment the reviewer did not
    /// write arrived in a thread of theirs, and that is the author on an ordinary pull request but
    /// a teammate or a bot just as legitimately. Naming the author anyway would put an unobserved
    /// attribution on the task's own stream (AGENTS.md's never-guess rule; independent pre-PR
    /// review, cycle 1, conformance lens), and the reviewer's next step — read the thread — is the
    /// same either way.
    /// </para>
    /// <para>
    /// A re-review request is the one part it does attribute, because GitHub records who a review
    /// request is addressed TO and that is the reviewer themselves: "a re-review is requested of
    /// you" is read straight off the request, not inferred from anybody's authorship.
    /// </para>
    /// </summary>
    public string Describe(string repository, int number, int openThreadCount)
    {
        List<string> parts = [];
        if (ReplyCount > 0)
        {
            parts.Add(
                $"{Plural(ReplyCount, "reply", "replies")} in {Plural(ThreadsWithReplies, "thread", "threads")}");
        }

        if (HeadMoved)
        {
            parts.Add(NewCommitCount is { } commits
                ? Plural(commits, "new commit", "new commits")
                : "new commits pushed (the count is not readable)");
        }

        // Last, so a re-review request that arrived alongside replies or a push reads as the
        // conclusion of the line rather than competing with them for the front of it.
        if (ReReviewNewlyRequested)
        {
            parts.Add("a re-review requested of you");
        }

        // Two parts read "a and b" exactly as they always did; three — replies, a push and a
        // re-review request in one look — read "a, b and c" rather than stringing three "and"s.
        string what = parts.Count switch
        {
            0 => "nothing this watch can name",
            1 => parts[0],
            _ => $"{string.Join(", ", parts[..^1])} and {parts[^1]}",
        };

        string outstanding = openThreadCount switch
        {
            0 => "every thread you opened is resolved now",
            1 => "1 of your threads is still unresolved",
            _ => $"{openThreadCount.ToString(CultureInfo.InvariantCulture)} of your threads are still unresolved",
        };

        return $"{repository}#{number.ToString(CultureInfo.InvariantCulture)} moved since your review: "
            + $"{what} — {outstanding}.";
    }

    private static string Plural(int count, string one, string many) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many)}";
}
