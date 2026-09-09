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
    [InlineData("Ran the gates in the foreground; no build or test was left running in the background.")]
    [InlineData("The suite finished; I am not waiting on anything in the background.")]
    public void A_compliant_summary_that_affirms_nothing_was_left_behind_is_not_a_match(string summary) =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(summary).Should().BeFalse();

    /// <summary>
    /// A negation word that denies something else entirely, unrelated to the pending-task claim
    /// later in the same period-delimited clause, must not suppress that later claim just because
    /// both share a clause. "no" here negates "way", not the still-running background task named
    /// after the comma (independent pre-PR review, cycle 2, adversarial lens — this exact phrasing
    /// escaped the prior, whole-clause negation scoping).
    /// </summary>
    [Fact]
    public void A_negation_of_something_unrelated_does_not_suppress_a_later_genuine_pending_task() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "There's no way to shorten the test run, so it's still running in the background while I wait for it to finish.")
            .Should().BeTrue();

    /// <summary>
    /// A negation paired with a still-in-flight word ("complete") in an earlier, unrelated
    /// sub-clause must not suppress a later sub-clause that actually names the pending background
    /// task — the earlier sub-clause never mentions "background" at all, so there is nothing there
    /// for the negation to deny (independent pre-PR review, cycle 3, adversarial lens — the prior
    /// fix scoped negation to a sub-clause naming "background" *or* a still-in-flight word, and the
    /// "or" let a common word like "complete" stand in for "background" and short-circuit past it).
    /// </summary>
    [Fact]
    public void A_negation_paired_with_an_unrelated_still_in_flight_word_does_not_suppress_a_later_genuine_pending_task() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "I don't know how long this will take to complete, but the background test is still running.")
            .Should().BeTrue();

    /// <summary>
    /// A negation doesn't need to repeat "background" itself to deny it — a later sub-clause can
    /// refer back by pronoun ("which") and still count, because the denial applies to the same
    /// background task the earlier sub-clause named (independent pre-PR review, cycle 4, adversarial
    /// lens — the prior fix only checked the sub-clause naming "background" itself, missing a
    /// negation that lands in the very next sub-clause instead).
    /// </summary>
    [Fact]
    public void A_negation_in_a_later_sub_clause_referring_back_by_pronoun_suppresses_the_pending_task_claim() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "A background test, which never finished, so I killed it.")
            .Should().BeFalse();

    /// <summary>
    /// A negation in a later sub-clause that opens with its own new subject ("I") denies something
    /// else entirely — an expectation about failure, not whether the background task is still
    /// pending — and must not suppress the genuine pending-task claim named earlier in the same
    /// sentence (independent pre-PR review, cycle 5, adversarial lens — the prior fix scanned every
    /// sub-clause through the end of the sentence once one of them named "background", so this
    /// unrelated "don't" wrongly suppressed the claim).
    /// </summary>
    [Theory]
    [InlineData("The background build is still running, but I don't expect it to fail.")]
    [InlineData("The background build is still running, but I don't think it matters.")]
    [InlineData("The background build is still running, so I'm not going to wait for it.")]
    public void A_negation_in_a_later_sub_clause_with_its_own_new_subject_does_not_suppress_a_genuine_pending_task(
        string summary) =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(summary).Should().BeTrue();

    /// <summary>
    /// A negation doesn't need a pronoun to refer back to the earlier "background" mention — a
    /// later sub-clause can deny it by re-naming "background" directly instead, and that still
    /// counts as the same denial (independent pre-PR review, cycle 6, adversarial lens — the
    /// cycle-5 fix's break condition only recognized the pronoun form and stopped scanning before
    /// ever inspecting a later sub-clause that re-named "background" and negated it in the same
    /// breath).
    /// </summary>
    [Fact]
    public void A_negation_in_a_later_sub_clause_that_re_names_background_directly_suppresses_the_pending_task_claim() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "I started a background test, but no background task is actually still running.")
            .Should().BeFalse();

    /// <summary>
    /// A later sub-clause that re-names "background" is only the same denial when it is still
    /// talking about the same pending task — carrying one of the still-in-flight words, the way
    /// cycle 6's own "no background task is actually still running" does. An unrelated aside that
    /// merely shares the literal word "background" ("chatter") must not suppress the genuine
    /// still-running claim named earlier in the same sentence (independent pre-PR review, cycle 7,
    /// adversarial lens — the cycle-6 fix's substring check matched any later sub-clause merely
    /// containing "background", not one actually continuing the same referent).
    /// </summary>
    [Fact]
    public void A_later_sub_clause_that_re_names_background_unrelated_to_the_earlier_task_does_not_suppress_it() =>
        PendingBackgroundTaskParser.NamesPendingBackgroundTask(
            "The background build is still running, but no background chatter is worth mentioning here.")
            .Should().BeTrue();
}
