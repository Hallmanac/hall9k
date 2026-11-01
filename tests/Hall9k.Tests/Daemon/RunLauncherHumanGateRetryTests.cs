using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run.Projections;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Whether a retry that would resume straight at pull-request-open has to go back through the human gate
/// first. Only the decision is driven here; the shortcut's own dispatch is proved elsewhere.
/// </summary>
public sealed class RunLauncherHumanGateRetryTests
{
    private static readonly DateTimeOffset Answered = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_gated_task_whose_failed_run_recorded_no_answer_stands_unanswered()
    {
        RunLauncher.HumanGateStandsUnanswered(humanGate: true, new RunDetails()).Should().BeTrue();
    }

    [Fact]
    public void A_task_without_the_gate_never_stands_unanswered()
    {
        RunLauncher.HumanGateStandsUnanswered(humanGate: false, new RunDetails()).Should().BeFalse();
    }

    [Fact]
    public void A_failed_run_that_recorded_an_answer_at_the_gate_is_answered()
    {
        RunLauncher.HumanGateStandsUnanswered(humanGate: true, new RunDetails { HumanGateAnsweredAt = Answered })
            .Should().BeFalse();
    }

    [Fact]
    public void A_failed_run_that_was_itself_a_resume_at_pull_request_open_keeps_the_approval_the_run_before_it_was_given()
    {
        RunLauncher.HumanGateStandsUnanswered(humanGate: true, new RunDetails { ResumedAtPullRequestOpen = true })
            .Should().BeFalse("a second consecutive failure to open must not send an approved task back through a full build and review");
    }
}
