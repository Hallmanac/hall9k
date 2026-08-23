using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The never-guess rule applied to one error shape (backlog 40): only the recognizable
/// usage-limit text says "external and clock-recoverable". Everything else — including a
/// generic <c>is_error</c> result with no text at all — has to stay the generic failure it is.
/// </summary>
public sealed class BudgetExhaustionParserTests
{
    [Theory]
    [InlineData("Claude AI usage limit reached|1762952400")]
    [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
    [InlineData("USAGE LIMIT REACHED|1762952400")]
    public void The_recognized_usage_limit_shape_is_classified_as_budget_exhaustion(string summary) =>
        BudgetExhaustionParser.IsBudgetExhausted(summary).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Something went wrong.")]
    [InlineData("rate_limit_error: overloaded, try again later")]
    [InlineData("Agent process died without a result.")]
    public void An_ambiguous_or_generic_error_is_never_read_as_budget_exhaustion(string? summary) =>
        BudgetExhaustionParser.IsBudgetExhausted(summary).Should().BeFalse(
            "a single vague error proves nothing about why a session died — the origin diagnosis "
            + "needed the full cross-run pattern, not one error's wording");
}
