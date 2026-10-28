using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task skip-to-pr</c> at the command line (idea 6e6d0200, Card 3): the reason flags, the
/// prompt, and what every surface renders from the event.
/// </summary>
public sealed class TaskSkipToPullRequestCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "owner-a-fingerprint-0123456789";

    private static TaskSkipToPullRequestCommand.Settings Flags(
        string? reason = null, bool noReason = false, bool byAgent = false) => new()
    {
        Id = "28b19893",
        Reason = reason,
        NoReason = noReason,
        ReasonByAgent = byAgent,
    };

    private static string NeverPrompted() => throw new InvalidOperationException("the prompt must not be shown");

    // ---- the reason flags ----

    [Fact]
    public void A_reason_flag_records_that_text_trimmed_and_marked_the_humans_own()
    {
        TaskSkipToPullRequestCommand.ResolvedReason resolved = TaskSkipToPullRequestCommand.ResolveReason(
            Flags(reason: "  docs only  "), terminalIsInteractive: false, NeverPrompted);

        resolved.Should().Be(new TaskSkipToPullRequestCommand.ResolvedReason("docs only", ByAgent: false));
    }

    [Fact]
    public void Reason_by_agent_marks_the_reason_as_the_driving_sessions_and_not_the_humans()
    {
        TaskSkipToPullRequestCommand.ResolvedReason resolved = TaskSkipToPullRequestCommand.ResolveReason(
            Flags(reason: "You said the flake was known", byAgent: true), terminalIsInteractive: false, NeverPrompted);

        resolved.ByAgent.Should().BeTrue();
        resolved.Text.Should().Be("You said the flake was known");
    }

    [Fact]
    public void No_reason_records_none_and_never_prompts_even_on_a_terminal()
    {
        TaskSkipToPullRequestCommand.ResolvedReason resolved = TaskSkipToPullRequestCommand.ResolveReason(
            Flags(noReason: true), terminalIsInteractive: true, NeverPrompted);

        resolved.Should().Be(new TaskSkipToPullRequestCommand.ResolvedReason(null, ByAgent: false));
    }

    [Fact]
    public void With_neither_flag_a_terminal_is_asked_and_its_answer_is_recorded_as_the_humans_own()
    {
        TaskSkipToPullRequestCommand.ResolvedReason resolved = TaskSkipToPullRequestCommand.ResolveReason(
            Flags(), terminalIsInteractive: true, () => "  the reviewers are blocked on a flake ");

        resolved.Should().Be(new TaskSkipToPullRequestCommand.ResolvedReason("the reviewers are blocked on a flake", false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_answer_to_the_prompt_records_no_reason(string answer)
    {
        TaskSkipToPullRequestCommand.ResolvedReason resolved = TaskSkipToPullRequestCommand.ResolveReason(
            Flags(), terminalIsInteractive: true, () => answer);

        resolved.Text.Should().BeNull();
        resolved.ByAgent.Should().BeFalse();
    }

    [Fact]
    public void With_neither_flag_and_no_terminal_it_refuses_and_names_both_flags()
    {
        Action resolve = () => TaskSkipToPullRequestCommand.ResolveReason(
            Flags(), terminalIsInteractive: false, NeverPrompted);

        resolve.Should().Throw<DomainValidationException>()
            .WithMessage("*--reason*").And.Message.Should().Contain("--no-reason");
    }

    [Fact]
    public void The_settings_refuse_contradictory_or_empty_reason_flags()
    {
        Flags(reason: "x", noReason: true).Validate().Successful.Should().BeFalse("two answers to one question");
        Flags(reason: "   ").Validate().Successful.Should().BeFalse("a blank reason is --no-reason's job");
        Flags(byAgent: true).Validate().Successful.Should().BeFalse("it marks who wrote a --reason that is not there");
        Flags(noReason: true, byAgent: true).Validate().Successful.Should().BeFalse();
        Flags(reason: "x", byAgent: true).Validate().Successful.Should().BeTrue();
        Flags(noReason: true).Validate().Successful.Should().BeTrue();
        Flags().Validate().Successful.Should().BeTrue("the prompt, or the no-terminal refusal, decides later");
    }

    // ---- what the surfaces render ----

    private static readonly ReviewSkipRecord AgentWritten = new(
        DomainId.New(), Fingerprint, Now, "You said the reviewers were blocked on a known flake", true,
        [ReviewSkipPlan.Gates, ReviewSkipPlan.Track(ReviewLens.Conformance), ReviewSkipPlan.SettlingGate]);

    private static MemberLabelLookup Labels() => new(new ProjectMemberLabels
    {
        Id = DomainId.New(),
        Labels = [new ProjectMemberLabel(Fingerprint, [], DisplayName.Parse("Brian"), "brianhallmanac")],
    });

    private static RunDetails Overridden(ReviewSkipRecord? skip) => new()
    {
        Id = DomainId.New(),
        LastReviewVerdict = ReviewVerdict.MergeReady,
        ReviewSettlement = ReviewSettlement.Overridden,
        ReviewSkip = skip,
    };

    [Fact]
    public void Task_show_names_who_skipped_what_and_the_agent_marked_reason()
    {
        string line = TaskShowCommand.ComposeReviewOutcome(Overridden(AgentWritten), Labels())!;

        line.Should().Contain("skipped to PR by Brian");
        line.Should().Contain(ReviewSkipPlan.Gates).And.Contain("the Conformance review track").And.Contain(ReviewSkipPlan.SettlingGate);
        line.Should().Contain("reason (written by the driving session): You said the reviewers were blocked on a known flake");
        line.Should().NotContain("how it was reached was not recorded");
    }

    [Fact]
    public void Task_show_says_no_reason_given_when_none_was_recorded_and_not_the_unrecorded_fallback()
    {
        ReviewSkipRecord noReason = AgentWritten with { Reason = null, ReasonByAgent = false };

        string line = TaskShowCommand.ComposeReviewOutcome(Overridden(noReason), Labels())!;

        line.Should().Contain("no reason given").And.NotContain("how it was reached was not recorded");
    }

    [Fact]
    public void Task_show_marks_a_humans_own_reason_as_theirs()
    {
        ReviewSkipRecord own = AgentWritten with { Reason = "docs only", ReasonByAgent = false };

        string line = TaskShowCommand.ComposeReviewOutcome(Overridden(own), Labels())!;

        line.Should().Contain("reason: docs only").And.NotContain("driving session");
    }

    [Fact]
    public void Task_show_without_a_known_label_falls_back_to_the_owners_short_id()
    {
        string line = TaskShowCommand.ComposeReviewOutcome(
            Overridden(AgentWritten with { OwnerRootFingerprint = null }), MemberLabelLookup.Empty)!;

        line.Should().Contain($"owner {DomainId.Short(AgentWritten.SkippedByOwnerId)}");
    }

    [Fact]
    public void The_passage_carries_the_override_as_a_line_naming_who_what_and_why()
    {
        TaskPassage empty = new(
            PassagePhase.NotApplicable, PassagePhase.NotApplicable, PassagePhase.NotApplicable,
            new ReviewCyclePassage(0, TimeSpan.Zero, false, 0, TimeSpan.Zero, false),
            PassagePhase.NotApplicable, PassagePhase.NotApplicable, [], PassagePhase.NotApplicable, [], 0, null,
            PassagePhase.NotApplicable);

        TaskShowCommand.ComposePassageLines(empty with { ReviewSkips = [AgentWritten] }, Labels())
            .Should().Contain(line => line.Contains("skipped to PR") && line.Contains("Brian")
                && line.Contains("the settling gate") && line.Contains("written by the driving session"));
        TaskShowCommand.ComposePassageLines(empty, Labels()).Should().BeEmpty("a passage with no override renders as it always has");
    }
}
