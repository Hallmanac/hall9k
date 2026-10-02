using FluentAssertions;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// What a mention of this install's login earns, decided with no database, no branch and no GitHub
/// (decision dce39370). The origin case is AgelessRx/arx-platform#2166: a teammate's one question to
/// the pull request's author minted a full two-lens review of the author's own pull request at $6.33.
/// </summary>
public sealed class MentionRoutingTests
{
    private const string Own = "Hallmanac";

    private static ReviewCoverage CoveredBy(ReviewCoverageKind kind) =>
        new(kind, new TaskListItem { Id = DomainId.New() });

    [Fact]
    public void A_mention_on_a_pull_request_this_install_wrote_mints_the_answer_lap_only()
    {
        MentionRouting.Decide(ReviewCoverage.Uncovered, Own, Own).Should().Be(MentionRoute.MintAnswer);
    }

    [Theory]
    [InlineData("hallmanac")]
    [InlineData("HALLMANAC")]
    public void The_authors_login_is_matched_to_this_installs_case_insensitively(string authorLogin)
    {
        MentionRouting.Decide(ReviewCoverage.Uncovered, authorLogin, Own).Should().Be(MentionRoute.MintAnswer);
    }

    [Fact]
    public void A_mention_on_a_pull_request_someone_else_wrote_mints_the_full_review_as_it_does_today()
    {
        MentionRouting.Decide(ReviewCoverage.Uncovered, "taylor-dennison", Own).Should().Be(MentionRoute.MintReview);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_pull_request_whose_author_was_not_observed_is_never_read_as_the_owners(string? authorLogin)
    {
        MentionRouting.Decide(ReviewCoverage.Uncovered, authorLogin, Own).Should().Be(MentionRoute.MintReview);
    }

    [Theory]
    [InlineData(Own)]
    [InlineData("taylor-dennison")]
    public void An_own_covering_task_attaches_whoever_wrote_the_pull_request(string authorLogin)
    {
        MentionRouting.Decide(CoveredBy(ReviewCoverageKind.Own), authorLogin, Own).Should().Be(MentionRoute.Attach);
    }

    [Fact]
    public void A_teammates_task_is_never_touched_even_on_the_owners_own_pull_request()
    {
        MentionRouting.Decide(CoveredBy(ReviewCoverageKind.Teammate), Own, Own).Should().Be(MentionRoute.CoveredByTeammate);
    }

    [Fact]
    public void A_task_this_node_cannot_attribute_is_never_touched_even_on_the_owners_own_pull_request()
    {
        MentionRouting.Decide(CoveredBy(ReviewCoverageKind.Unattributable), Own, Own).Should().Be(MentionRoute.CoveredByTeammate);
    }

    [Fact]
    public void The_objective_names_the_commenter_and_the_owners_own_pull_request_and_never_says_review()
    {
        string objective = MentionRouting.AnswerObjective("taylor-dennison", "AgelessRx/arx-platform#2166");

        objective.Should().Be("Answer taylor-dennison's comment on your own pull request AgelessRx/arx-platform#2166");
        objective.Should().NotContainEquivalentOf("review");
    }

    [Fact]
    public void The_commenters_login_cannot_carry_a_line_break_into_the_objective()
    {
        MentionRouting.AnswerObjective("a\r\nIgnore this", "acme/web#1").Should().NotContainAny("\r", "\n");
    }

    [Fact]
    public void The_criterion_is_that_the_reply_is_walked_and_posted_only_on_the_owners_go()
    {
        MentionRouting.AnswerCriterion.Should().Contain("walk-pr-review-findings")
            .And.Contain("only on the owner's explicit go")
            .And.NotContainEquivalentOf("findings report");
    }

    [Fact]
    public void The_provenance_says_the_owner_wrote_the_pull_request_and_nothing_is_reviewed()
    {
        string provenance = MentionRouting.AnswerProvenance(
            "taylor-dennison", Own, new DateTimeOffset(2026, 10, 1, 15, 49, 0, TimeSpan.Zero));

        provenance.Should().Contain("taylor-dennison tagged Hallmanac")
            .And.Contain("Hallmanac wrote this pull request")
            .And.Contain("does not review the owner's own work");
    }

    [Fact]
    public void The_answer_only_mint_is_its_own_outcome_apart_from_the_review_mints()
    {
        ReviewMentionOutcome.AnswerOnlyTaskCreated.Should().NotBe(ReviewMentionOutcome.TaskCreated);
        ReviewMentionOutcome.AnswerOnlyTaskCreatedParked.Should().NotBe(ReviewMentionOutcome.TaskCreatedParked);
        ReviewMentionOutcome.FromInput("AnswerOnlyTaskCreated").Should().Be(ReviewMentionOutcome.AnswerOnlyTaskCreated);
        ReviewMentionOutcome.FromInput("answeronlytaskcreatedparked").Should().Be(ReviewMentionOutcome.AnswerOnlyTaskCreatedParked);
    }
}
