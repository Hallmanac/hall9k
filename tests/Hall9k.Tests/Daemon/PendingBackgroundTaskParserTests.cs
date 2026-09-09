using FluentAssertions;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Task: a headless build, fix, or recovery session never ends its turn while a gate it started
/// is still running in the background. The three inline cases below are the exact wording each of
/// the three origin incidents' sessions ended on (2026-09-07/08).
/// </summary>
public sealed class PendingBackgroundTaskParserTests
{
    [Theory]
    [InlineData("I'm waiting on the background dotnet test run (task ID b5ej6atfz) to complete before finishing this session.")]
    [InlineData("The full dotnet test run is still running in the background.")]
    [InlineData("Test suite is running in the background; I've set a monitor to notify me.")]
    public void Recognizes_each_origin_incidents_own_wording(string summary) =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(summary).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Done, all good.")]
    public void A_summary_with_nothing_backgrounded_is_not_a_match(string? summary) =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(summary).Should().BeFalse();

    /// <summary>
    /// The word "background" alone is ordinary English and appears in plenty of summaries that
    /// never backgrounded anything — matching on it alone would misreport an innocent mention as
    /// the exact policy violation this parser exists to catch (AGENTS.md's never-guess rule).
    /// </summary>
    [Fact]
    public void The_word_background_alone_without_a_still_in_flight_word_is_not_a_match() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "For background context, this fixes the off-by-one from finding 2.").Should().BeFalse();

    /// <summary>
    /// A session that complied with the foreground-gates rule and says so, in exactly the shape
    /// that rule's own wording invites, must not read as the violation it describes declining
    /// (independent pre-PR review, cycle 1, both lenses): "background" and an in-flight word
    /// ("completed") land in the same message without ever naming a pending task.
    /// </summary>
    [Fact]
    public void A_compliant_summary_that_declines_backgrounding_is_not_a_match() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "Ran the full suite in the foreground rather than backgrounding it; all gates completed.")
            .Should().BeFalse();

    /// <summary>
    /// The most natural way a compliant session affirms it left nothing behind — "nothing is left
    /// running" or "no background monitors were set" — must not itself be misread as the violation
    /// it is denying (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Theory]
    [InlineData("Ran the gates in the foreground. Nothing is left running in the background.")]
    [InlineData("Ran the gates in the foreground; no background monitors were set.")]
    public void A_compliant_summary_that_affirms_nothing_was_left_behind_is_not_a_match(string summary) =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(summary).Should().BeFalse();
}
