using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k task show</c> says about a changes-requested review (task: a changes-requested
/// pull-request review from a human becomes a fix lap). The composition is asserted rather than
/// the console, the same split every other pure rendering helper on that command uses.
/// <para>
/// Two of these are about honesty rather than layout: a review whose submission time the provider
/// never reported must not borrow this install's observation time as though it were GitHub's, and
/// a disagreement the session never attributed to a review must not be filed under whichever
/// review happened to be first.
/// </para>
/// </summary>
public sealed class ChangesRequestedReviewRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 15, 0, TimeSpan.Zero);

    [Fact]
    public void A_task_that_never_took_a_changes_requested_review_renders_no_block() =>
        TaskShowCommand.ComposeChangesRequestedReviews([new RunDetails()]).Should().BeEmpty();

    [Fact]
    public void Each_review_renders_its_reviewer_time_and_finding_count_with_a_link()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation(
                    "teammate", "https://github.com/x/y/pull/7#pullrequestreview-42", Now, 2, Now.AddMinutes(3)),
            ],
        };

        string line = TaskShowCommand.ComposeChangesRequestedReviews([run]).Should()
            .HaveCount(2, "the heading plus one row").And.Subject.Last();

        line.Should().Contain("@teammate")
            .And.Contain("2 findings")
            .And.Contain("https://github.com/x/y/pull/7#pullrequestreview-42");
    }

    [Fact]
    public void A_review_with_no_reported_submission_time_says_so_rather_than_borrowing_the_observation()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation(
                    "teammate", "https://x/y/pull/7#r1", SubmittedAt: null, FindingCount: 1, ObservedAt: Now),
            ],
        };

        TaskShowCommand.ComposeChangesRequestedReviews([run]).Last().Should()
            .Contain("submission time not reported")
            .And.Contain("1 finding");
    }

    [Fact]
    public void A_parked_disagreement_renders_the_finding_the_reasoning_and_the_unsent_draft()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation("teammate", "https://x/y/pull/7#r1", Now, 1, Now),
            ],
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement(
                    "reset the limiter per request", "per-window is the documented contract",
                    "Good catch on the naming, but the reset is per window deliberately.",
                    "src/Limiter.cs:42", "PRRT_abc", "https://x/y/pull/7#r1"),
            ],
        };

        IReadOnlyList<string> lines = TaskShowCommand.ComposeChangesRequestedReviews([run]);

        lines.Should().Contain(line => line.Contains("disagreed") && line.Contains("src/Limiter.cs:42"));
        lines.Should().Contain(line => line.Contains("reviewer asked") && line.Contains("reset the limiter per request"));
        lines.Should().Contain(line => line.Contains("session's reasoning") && line.Contains("documented contract"));
        lines.Should().Contain(line => line.Contains("proposed reply") && line.Contains("per window deliberately"));
        lines.Should().NotContain(line => line.Contains("you directed"),
            "nothing has been sent yet — the draft is still the implementer's to decide");
    }

    /// <summary>
    /// The disagreement's url came out of a fix session's free-text summary while the observation's
    /// came off the provider, so the two can differ in casing alone — which is why
    /// <c>h9k review resolve</c> vets them OrdinalIgnoreCase. Paired case-sensitively here, a reply
    /// that command accepts and posts against the review rendered as answering no review at all,
    /// leaving the two surfaces disagreeing about one fact (independent pre-PR review, cycle 1,
    /// both lenses).
    /// </summary>
    [Fact]
    public void A_disagreement_naming_its_review_in_different_casing_still_renders_under_it()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation("teammate", "https://github.com/x/y/pull/7#r1", Now, 1, Now),
            ],
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement(
                    "reset the limiter per request", "per-window is the documented contract",
                    "The reset is per window deliberately.", "src/Limiter.cs:42", "PRRT_abc",
                    "https://GitHub.com/x/y/pull/7#r1"),
            ],
        };

        IReadOnlyList<string> lines = TaskShowCommand.ComposeChangesRequestedReviews([run]);

        lines.Should().NotContain(line => line.Contains("not attributed to a specific review"),
            "the session named this very review, spelled with a different host casing");
        lines.Should().Contain(line => line.Contains("disagreed") && line.Contains("src/Limiter.cs:42"));
    }

    [Fact]
    public void A_disputed_review_body_renders_as_having_no_thread_to_reply_inside()
    {
        RunDetails run = new()
        {
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement("the body's point", "my reasoning", ProposedReply: string.Empty),
            ],
        };

        IReadOnlyList<string> lines = TaskShowCommand.ComposeChangesRequestedReviews([run]);

        lines.Should().Contain(line => line.Contains("no thread to reply inside"));
        lines.Should().Contain(line => line.Contains("proposed reply: none drafted"),
            "an undrafted reply is stated as absent, never rendered as an empty quote");
        lines.Should().Contain(line => line.Contains("not attributed to a specific review"),
            "the session named no review, and guessing one would file it under a verdict it may not answer");
    }

    [Fact]
    public void What_the_implementer_directed_renders_once_the_park_is_resolved()
    {
        RunDetails posted = new()
        {
            ChangesRequestedReplyDirections =
            [
                new ReviewDisagreementReplyDirection(
                    ReviewDisagreementReplyChoice.AsWritten, "the drafted text", "PRRT_abc", Now),
            ],
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement("point", "reasoning", "the drafted text", "src/A.cs:1", "PRRT_abc"),
            ],
        };
        RunDetails silent = new()
        {
            ChangesRequestedReplyDirections =
            [
                new ReviewDisagreementReplyDirection(
                    ReviewDisagreementReplyChoice.Nothing, null, null, Now),
            ],
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement("point", "reasoning", "the drafted text", "src/A.cs:1", "PRRT_abc"),
            ],
        };

        TaskShowCommand.ComposeChangesRequestedReviews([posted]).Should().Contain(
            line => line.Contains("you directed") && line.Contains("the drafted reply") && line.Contains("PRRT_abc"));
        TaskShowCommand.ComposeChangesRequestedReviews([silent]).Should().Contain(
            line => line.Contains("you directed") && line.Contains("the reviewer has heard nothing"));
    }

    /// <summary>
    /// A lap that pushed nothing the reviewer accepted leaves the next sweep re-reading the
    /// identical review url, so the same review lands on the run stream more than once. It renders
    /// as ONE review with the later reads counted on it — not as two rows each repeating the same
    /// parked disagreement underneath (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_review_read_again_by_a_later_sweep_renders_once_with_its_re_reads_counted()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation("teammate", "https://x/y/pull/7#r1", Now, 1, Now),
                new ChangesRequestedReviewObservation(
                    "teammate", "https://x/y/pull/7#r1", Now, 1, Now.AddHours(2)),
            ],
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement(
                    "reset the limiter per request", "per-window is the documented contract",
                    "The reset is per window deliberately.", "src/Limiter.cs:42", "PRRT_abc",
                    "https://x/y/pull/7#r1"),
            ],
        };

        IReadOnlyList<string> lines = TaskShowCommand.ComposeChangesRequestedReviews([run]);

        lines.Should().ContainSingle(line => line.Contains("@teammate"), "one review, one row");
        lines.Should().ContainSingle(line => line.Contains("proposed reply"),
            "the disagreement belongs to the review, not to whichever sweep read it");
        lines.Should().Contain(line => line.Contains("still standing at 1 later sweep"),
            "that the review outlived a lap is a fact worth stating rather than double-printing");
    }

    /// <summary>
    /// Collapsing by url must not collapse the reviews that have none: a provider that reported no
    /// url for two different reviewers' reviews leaves two reviews nobody can name, and reading
    /// them as one would hide a reviewer who blocked this pull request.
    /// </summary>
    [Fact]
    public void Two_reviews_whose_url_the_provider_never_reported_each_keep_their_own_row()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation("teammate", "", Now, 1, Now),
                new ChangesRequestedReviewObservation("other-teammate", "", Now, 2, Now.AddHours(1)),
            ],
        };

        IReadOnlyList<string> lines = TaskShowCommand.ComposeChangesRequestedReviews([run]);

        lines.Should().HaveCount(3, "the heading plus a row each");
        lines.Should().Contain(line => line.Contains("@teammate") && line.Contains("1 finding"));
        lines.Should().Contain(line => line.Contains("@other-teammate") && line.Contains("2 findings"));
        lines.Should().NotContain(line => line.Contains("still standing"),
            "neither review was re-read; they are two reviews that happen to be unnameable");
    }

    /// <summary>
    /// Closeout's thread read is capped at the pull request's first 100 threads, so a later sweep
    /// can genuinely count a review's findings differently. Collapsing the rows must not quietly
    /// drop that read.
    /// </summary>
    [Fact]
    public void A_later_read_that_counted_different_findings_is_named_rather_than_dropped()
    {
        RunDetails run = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation("teammate", "https://x/y/pull/7#r1", Now, 1, Now),
                new ChangesRequestedReviewObservation(
                    "teammate", "https://x/y/pull/7#r1", Now, 3, Now.AddHours(2)),
            ],
        };

        TaskShowCommand.ComposeChangesRequestedReviews([run]).Last().Should()
            .Contain("1 finding")
            .And.Contain("3 findings", "the later read saw something else, and the row says so");
    }

    /// <summary>
    /// The observation lands on the run that was watching the pull request and the disagreement on
    /// the run dispatched to answer it — two runs, one story, which is why the block reads across
    /// every run rather than only the newest.
    /// </summary>
    [Fact]
    public void The_block_reads_across_runs_because_the_two_halves_land_on_different_ones()
    {
        RunDetails watcher = new()
        {
            ChangesRequestedReviewObservations =
            [
                new ChangesRequestedReviewObservation("teammate", "https://x/y/pull/7#r1", Now, 1, Now),
            ],
        };
        RunDetails fixLap = new()
        {
            ChangesRequestedDisagreements =
            [
                new ReviewDisagreement("point", "reasoning", "draft", "src/A.cs:1", "PRRT_abc", "https://x/y/pull/7#r1"),
            ],
        };

        IReadOnlyList<string> lines = TaskShowCommand.ComposeChangesRequestedReviews([watcher, fixLap]);

        lines.Should().Contain(line => line.Contains("@teammate"));
        lines.Should().Contain(line => line.Contains("disagreed"));
        lines.Should().NotContain(line => line.Contains("not attributed"),
            "the disagreement names the review the other run observed, so it nests under it");
    }
}
