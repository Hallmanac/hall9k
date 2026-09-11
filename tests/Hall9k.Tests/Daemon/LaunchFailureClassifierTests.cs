using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The zero-work SHAPE (task: a session that exits at once with no work done is treated as the
/// node failing to launch sessions) — never message text, since the origin outage's two causes
/// (an expired credential, a GitHub fetch timeout) were already two different strings. Mirrors
/// <see cref="BudgetExhaustionParserTests"/>'s own discipline: the never-guess rule applies to
/// every field this classifier reads, not just the ones that happen to be present.
/// </summary>
public sealed class LaunchFailureClassifierTests
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(2);

    private static AgentResult ZeroWork(int durationMs = 150) => new(
        IsError: true, InputTokens: 0, CacheReadInputTokens: 0, CacheCreationInputTokens: 0,
        OutputTokens: 0, CostUsd: null, Turns: 1, Summary: "Failed to authenticate", DurationMs: durationMs);

    [Fact]
    public void The_2026_09_07_outages_own_shape_is_classified_as_a_launch_failure() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork(durationMs: 85), MaxDuration).Should().BeTrue();

    [Fact]
    public void A_result_that_is_not_an_error_is_never_a_launch_failure() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { IsError = false }, MaxDuration).Should().BeFalse(
            "a session that finished cleanly obviously launched");

    [Fact]
    public void More_than_one_turn_is_never_a_launch_failure() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { Turns = 2 }, MaxDuration).Should().BeFalse(
            "a session that took a second turn did launch and start working");

    [Fact]
    public void An_unknown_turn_count_is_never_guessed_as_one() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { Turns = null }, MaxDuration).Should().BeFalse(
            "num_turns absent from the result payload is unmeasured, never read as exactly one");

    [Fact]
    public void Any_recorded_input_tokens_rule_it_out() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { InputTokens = 1 }, MaxDuration).Should().BeFalse();

    [Fact]
    public void Any_recorded_cache_read_tokens_rule_it_out() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { CacheReadInputTokens = 1 }, MaxDuration).Should().BeFalse(
            "a cached session's input arrives almost entirely as cache reads (log #30) — this is the field a naive input_tokens-only check would miss");

    [Fact]
    public void Any_recorded_output_tokens_rule_it_out() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { OutputTokens = 1 }, MaxDuration).Should().BeFalse();

    [Fact]
    public void An_unknown_duration_is_never_guessed_as_fast() =>
        LaunchFailureClassifier.IsLaunchFailure(ZeroWork() with { DurationMs = null }, MaxDuration).Should().BeFalse(
            "duration_ms absent from the result payload is unmeasured, never read as instant");

    [Fact]
    public void A_duration_at_or_past_the_threshold_is_a_genuine_session_not_a_launch_failure() =>
        LaunchFailureClassifier.IsLaunchFailure(
            ZeroWork(durationMs: (int)MaxDuration.TotalMilliseconds), MaxDuration).Should().BeFalse(
            "a session that ran a full threshold's worth of wall clock did more than fail to launch");

    /// <summary>
    /// The "two shapes are told apart" requirement's classifier-level half: nothing about this
    /// type reads message text at all, so a result that happens to carry the recognizable
    /// usage-limit wording is classified purely on its numeric shape here — it is
    /// <see cref="BudgetExhaustionParser"/>, checked first by the caller (<c>RunSupervisor</c>,
    /// <c>ReviewEngine</c>), that actually routes a budget-exhausted result to a park instead of
    /// a hold; <c>Integration.RunSupervisorTests</c> proves that caller-level ordering end to end.
    /// </summary>
    [Fact]
    public void A_result_carrying_the_budget_exhausted_text_is_still_classified_by_shape_alone()
    {
        AgentResult budgetShaped = ZeroWork() with { Summary = "Claude AI usage limit reached|1762952400" };
        LaunchFailureClassifier.IsLaunchFailure(budgetShaped, MaxDuration).Should().BeTrue(
            "this classifier never reads Summary for anything but a fallback; text is the caller's own, separate check");
    }
}
