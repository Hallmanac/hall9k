using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class ReviewResultParserTests
{
    [Theory]
    [InlineData("All clear.\n\nVERDICT: merge-ready")]
    [InlineData("findings...\nverdict: MERGE-READY")]
    [InlineData("  VERDICT:   merge-ready  ")]
    public void Merge_ready_verdicts_parse_regardless_of_case_and_whitespace(string summary) =>
        ReviewResultParser.ParseVerdict(summary).Should().Be(ReviewVerdict.MergeReady);

    [Fact]
    public void Needs_fixes_verdict_parses()
    {
        ReviewResultParser.ParseVerdict("1. Foo.cs:12 — off-by-one.\n\nVERDICT: needs-fixes")
            .Should().Be(ReviewVerdict.NeedsFixes);
    }

    [Fact]
    public void The_last_verdict_line_wins_when_the_reviewer_quotes_the_instructions()
    {
        string summary =
            "The instructions said to end with VERDICT: merge-ready or VERDICT: needs-fixes.\n" +
            "I found a real defect.\n" +
            "VERDICT: needs-fixes";

        ReviewResultParser.ParseVerdict(summary).Should().Be(ReviewVerdict.NeedsFixes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Looks fine to me, ship it.")]
    [InlineData("VERDICT: looks great")]
    public void A_missing_or_unrecognized_verdict_maps_to_the_unknown_sentinel(string? summary) =>
        ReviewResultParser.ParseVerdict(summary).Should().Be(ReviewVerdict.Unknown);

    [Theory]
    [InlineData("Fixed both findings.\nRESOLUTION: fixed", "Fixed")]
    [InlineData("Finding 2 is a deliberate design choice.\nresolution: DISPUTED", "Disputed")]
    [InlineData("Finding 1 is fixed.\nRESOLUTION: fixed, except finding 2 which I judge disputed", "Disputed")]
    [InlineData("Done, all good.", "")]
    public void Fix_resolutions_parse_with_disputes_taking_precedence(string summary, string expected) =>
        ReviewResultParser.ParseFixOutcome(summary).Value.Should().Be(expected);

    /// <summary>
    /// The block a changes-requested fix lap parks with (task: a changes-requested pull-request
    /// review from a human becomes a fix lap) — three positions and the two tags that say where a
    /// reply would go and which review it answers.
    /// </summary>
    [Fact]
    public void A_disagreement_block_parses_its_three_positions_and_its_tags()
    {
        string summary = """
            Fixed the two findings I agreed with and replied in their threads.

            DISAGREEMENT: at=src/Limiter.cs:42; thread=PRRT_abc; review=https://x/y/pull/7#r1
            REVIEWER ASKED: reset the limiter on every request rather than per window.
            MY REASONING: per-window is the documented contract (PLAN.md 12.3), and
            per-request would let a single client starve the pool.
            PROPOSED REPLY:
            Good catch on the naming — the reset really is per window, deliberately:
            PLAN.md 12.3 sets the contract. Happy to rename it if that reads better.

            RESOLUTION: disputed

            HANDOFF:
            nothing surprising here
            """;

        ReviewDisagreement disagreement =
            ReviewResultParser.ParseDisagreements(summary).Should().ContainSingle().Subject;

        disagreement.Location.Should().Be("src/Limiter.cs:42");
        disagreement.ThreadId.Should().Be("PRRT_abc");
        disagreement.ReviewUrl.Should().Be("https://x/y/pull/7#r1");
        disagreement.Finding.Should().Be("reset the limiter on every request rather than per window.");
        disagreement.Reasoning.Should().StartWith("per-window is the documented contract");
        disagreement.Reasoning.Should().Contain("starve the pool");
        disagreement.ProposedReply.Should().StartWith("Good catch on the naming");
        disagreement.ProposedReply.Should().NotContain(
            "RESOLUTION", "the marker line ends the block rather than becoming part of the reply");
        disagreement.ProposedReply.Should().NotContain(
            "HANDOFF", "the handoff is for the next task, not for the reviewer");
    }

    /// <summary>
    /// A body with no sub-markers is read whole as the session's reasoning rather than dropped: it
    /// plainly said something, and the only thing unrecoverable is which half of it was which —
    /// which the human resolving the park can see for themselves.
    /// </summary>
    [Fact]
    public void A_disagreement_with_no_sub_markers_keeps_its_prose_as_the_reasoning()
    {
        ReviewDisagreement disagreement = ReviewResultParser.ParseDisagreements(
            "DISAGREEMENT: at=src/A.cs:1\nI think the existing behaviour is right.\nRESOLUTION: disputed")
            .Should().ContainSingle().Subject;

        disagreement.Reasoning.Should().Be("I think the existing behaviour is right.");
        disagreement.Finding.Should().BeEmpty("nothing said what the reviewer asked for; it is not invented");
        disagreement.ProposedReply.Should().BeEmpty("no reply was drafted, so there is none to post as written");
    }

    /// <summary>A review body disputed has no thread and no location — both absent rather than filled in.</summary>
    [Fact]
    public void A_disagreement_naming_no_thread_or_location_carries_neither()
    {
        ReviewDisagreement disagreement = ReviewResultParser.ParseDisagreements(
            "DISAGREEMENT: review=https://x/y/pull/7#r1\nMY REASONING: no.\nPROPOSED REPLY:\nRespectfully, no.")
            .Should().ContainSingle().Subject;

        disagreement.Location.Should().BeNull();
        disagreement.ThreadId.Should().BeNull();
        disagreement.ReviewUrl.Should().Be("https://x/y/pull/7#r1");
        disagreement.ProposedReply.Should().Be("Respectfully, no.");
    }

    /// <summary>
    /// Two blocks are two disagreements, each ending where the next begins — the parser stays
    /// general even though the prompt asks for one, so a lap that parks two is recorded honestly
    /// rather than collapsed.
    /// </summary>
    [Fact]
    public void Each_disagreement_block_ends_where_the_next_begins()
    {
        IReadOnlyList<ReviewDisagreement> disagreements = ReviewResultParser.ParseDisagreements(
            """
            DISAGREEMENT: at=src/A.cs:1; thread=t1
            PROPOSED REPLY:
            first reply
            DISAGREEMENT: at=src/B.cs:2; thread=t2
            PROPOSED REPLY:
            second reply
            RESOLUTION: disputed
            """);

        disagreements.Should().HaveCount(2);
        disagreements[0].ProposedReply.Should().Be("first reply");
        disagreements[1].ProposedReply.Should().Be("second reply");
    }

    /// <summary>
    /// No block is not "the session agreed with everything" — the RESOLUTION marker is what says a
    /// disagreement exists, and the park is recorded on that whether or not a block parses.
    /// </summary>
    [Fact]
    public void A_summary_with_no_disagreement_block_parses_to_none()
    {
        ReviewResultParser.ParseDisagreements("Fixed everything.\nRESOLUTION: disputed").Should().BeEmpty();
        ReviewResultParser.ParseDisagreements(null).Should().BeEmpty();
    }

    /// <summary>
    /// A follow-up's own triage (task: every review thread on a pull request gets a triage
    /// disposition before any fix work), read off the same tolerant contract every other marker
    /// block here already uses.
    /// </summary>
    [Fact]
    public void A_thread_disposition_block_parses_its_tags_and_its_reasoning()
    {
        string summary = """
            Triaged every unresolved thread before touching any code.

            THREAD DISPOSITION: thread=PRRC_1; disposition=decline; kind=bot; author=copilot
            Reproduced in a scratch repo: git push does update the remote-tracking ref.
            THREAD DISPOSITION: thread=PRRC_2; disposition=fix; kind=human; author=brianhallmanac
            Renamed the limiter per the reviewer's suggestion.

            RESOLUTION: fixed

            HANDOFF:
            nothing surprising here
            """;

        IReadOnlyList<ReviewThreadOutcome> outcomes = ReviewResultParser.ParseThreadDispositions(summary);

        outcomes.Should().HaveCount(2);
        outcomes[0].ThreadId.Should().Be("PRRC_1");
        outcomes[0].Disposition.Should().Be(ReviewThreadDisposition.Decline);
        outcomes[0].IsHuman.Should().BeFalse();
        outcomes[0].Author.Should().Be("copilot");
        outcomes[0].Reasoning.Should().Contain("Reproduced in a scratch repo");
        outcomes[1].ThreadId.Should().Be("PRRC_2");
        outcomes[1].Disposition.Should().Be(ReviewThreadDisposition.Fix);
        outcomes[1].IsHuman.Should().BeTrue();
        outcomes[1].Reasoning.Should().NotContain(
            "RESOLUTION", "the marker line ends the block rather than becoming part of the reasoning");
        outcomes[1].Reasoning.Should().NotContain(
            "HANDOFF", "the handoff is for the next task, not for the triage record");
    }

    /// <summary>A block naming no thread id carries nothing this platform can measure against a real thread, so it is dropped.</summary>
    [Fact]
    public void A_thread_disposition_block_with_no_thread_id_is_dropped()
    {
        ReviewResultParser.ParseThreadDispositions("THREAD DISPOSITION: disposition=fix\nfixed it")
            .Should().BeEmpty();
    }

    /// <summary>An unrecognized or absent disposition tag is a fact worth keeping, not a parse failure to hide.</summary>
    [Fact]
    public void A_thread_disposition_block_missing_its_disposition_tag_reads_as_unknown()
    {
        ReviewThreadOutcome outcome = ReviewResultParser.ParseThreadDispositions(
            "THREAD DISPOSITION: thread=PRRC_1\nsomething was said")
            .Should().ContainSingle().Subject;

        outcome.Disposition.Should().Be(ReviewThreadDisposition.Unknown);
    }

    /// <summary>
    /// An absent or unrecognized `kind=` tag is an unobserved fact, not a guessed "bot" — the same
    /// nullable treatment <c>RunDetails.UnresolvedHumanReviewThreads</c> already gives this exact
    /// question at the run level (cycle-1 pre-PR review, both lenses).
    /// </summary>
    [Theory]
    [InlineData("THREAD DISPOSITION: thread=PRRC_1; disposition=fix\nfixed it")]
    [InlineData("THREAD DISPOSITION: thread=PRRC_1; disposition=fix; kind=User\nfixed it")]
    public void A_thread_disposition_block_with_no_recognized_kind_tag_reads_IsHuman_as_null(string summary)
    {
        ReviewThreadOutcome outcome = ReviewResultParser.ParseThreadDispositions(summary)
            .Should().ContainSingle().Subject;

        outcome.IsHuman.Should().BeNull();
    }

    /// <summary>
    /// A session that quotes the triage contract's own worked example before answering — the
    /// same observed habit <see cref="ExampleLocationPlaceholder"/> already guards against for a
    /// finding's location — must not have that echoed placeholder recorded as a real thread id
    /// (cycle-1 pre-PR review, adversarial finding).
    /// </summary>
    [Fact]
    public void A_thread_disposition_block_echoing_the_contracts_placeholder_thread_id_is_dropped()
    {
        ReviewResultParser.ParseThreadDispositions(
            $"{ReviewResultParser.ThreadDispositionMarker} thread={ReviewResultParser.ThreadIdPlaceholder}; "
            + "disposition=fix|decline|route; kind=human|bot; author=<login>\n"
            + "<why: the fix's brief restatement, the decline's evidence, or the route's scope reason>")
            .Should().BeEmpty();
    }

    /// <summary>
    /// The recap prose the prompt asks a follow-up to write after its triage blocks — and, for a
    /// disputed thread, the "both positions" narrative above the RESOLUTION line — must not be
    /// absorbed into the last block's own reasoning, mis-attributing it as that thread's evidence
    /// (cycle-1 pre-PR review, both lenses). The `SUMMARY:` marker closes the last block the same
    /// way `RESOLUTION:`/`HANDOFF:` already do.
    /// </summary>
    [Fact]
    public void The_summary_marker_stops_trailing_recap_prose_from_landing_in_the_last_threads_reasoning()
    {
        string summary = """
            THREAD DISPOSITION: thread=PRRC_1; disposition=decline; kind=bot; author=copilot
            Reproduced in a scratch repo: git push does update the remote-tracking ref.
            THREAD DISPOSITION: thread=PRRC_2; disposition=fix; kind=human; author=brianhallmanac
            Renamed the limiter per the reviewer's suggestion.
            SUMMARY:
            A third thread asks for a different projection shape entirely.

            RESOLUTION: disputed
            """;

        IReadOnlyList<ReviewThreadOutcome> outcomes = ReviewResultParser.ParseThreadDispositions(summary);

        outcomes.Should().HaveCount(2);
        outcomes[1].ThreadId.Should().Be("PRRC_2");
        outcomes[1].Reasoning.Should().Be("Renamed the limiter per the reviewer's suggestion.");
        outcomes[1].Reasoning.Should().NotContain("A third thread");
    }

    /// <summary>
    /// No headers at all is "this run's prompt never taught the vocabulary", not "nothing was
    /// triaged" — the same reading <see cref="A_summary_with_no_disagreement_block_parses_to_none"/>
    /// already documents for its own marker.
    /// </summary>
    [Fact]
    public void A_summary_with_no_thread_disposition_block_parses_to_none()
    {
        ReviewResultParser.ParseThreadDispositions("Fixed the CI failure.\nRESOLUTION: fixed").Should().BeEmpty();
        ReviewResultParser.ParseThreadDispositions(null).Should().BeEmpty();
    }
}
