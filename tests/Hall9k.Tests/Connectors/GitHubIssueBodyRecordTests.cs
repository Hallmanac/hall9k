using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// <see cref="GitHubIssueBody.WithCriteriaChecklist"/>'s own rewrite rules, now that the record
/// section it used to have to avoid is retired (idea 202383dc, A3a: the record lives in the
/// ledger, not in a collapsed section of the issue body). What is still load-bearing here is the
/// promise the class's own doc comment states: a human's own prose above and below the checklist
/// is theirs to keep, and a rewrite touches only the checklist section itself — from its heading
/// to the last item under it, never further.
/// <para>
/// Every test in this file that used to pin "the checklist search must not wander into the record
/// section" is gone along with that section: there is nothing left for a checklist rewrite to
/// wander into. What is kept is the underlying promise those tests were really guarding —
/// prose above and prose below the list both survive a regeneration untouched.
/// </para>
/// </summary>
public sealed class GitHubIssueBodyRecordTests
{
    [Fact]
    public void Regenerating_the_checklist_leaves_the_prose_above_and_below_it_alone()
    {
        const string ProseAbove = "## Objective\n\nSomething.\n\n## Notes from Brian\n\nStill mine.";
        string body = $"{ProseAbove}\n\n## Acceptance criteria\n\n- [ ] The old one";

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["The new one", "And another"]);

        rewritten.Should().Contain(ProseAbove)
            .And.Contain("- [ ] The new one")
            .And.Contain("- [ ] And another")
            .And.NotContain("- [ ] The old one");
    }

    /// <summary>
    /// The prose case the checklist rewrite used to eat: a note with no heading of its own, sitting
    /// under the checklist with nothing separating them but a blank line. The span has to end at
    /// the last actual list item, not at "the rest of the body", or regenerating the checklist
    /// silently deletes a human's own note (independent pre-PR review, cycle 1, adversarial lens;
    /// the finding pre-dates the record's retirement and is unrelated to it).
    /// </summary>
    [Fact]
    public void A_note_with_no_heading_under_the_checklist_survives_the_checklist_being_regenerated()
    {
        const string Note = "One of these came from Brian on the Mac, and it matters.";
        string body = $"## Objective\n\nSomething.\n\n## Acceptance criteria\n\n- [x] The old one\n\n{Note}";

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["The new one"]);

        rewritten.Should().Contain(Note, "a human's own note is theirs to keep")
            .And.Contain("- [ ] The new one")
            .And.NotContain("The old one");
        rewritten.IndexOf(Note, StringComparison.Ordinal).Should().BeGreaterThan(
            rewritten.IndexOf("- [ ] The new one", StringComparison.Ordinal),
            "the note stays where it was — under the checklist");
    }

    /// <summary>
    /// The same protection when a heading does intervene: the section still ends at the last item,
    /// so the heading and its own prose below it are untouched.
    /// </summary>
    [Fact]
    public void A_heading_below_the_checklist_and_its_prose_are_both_left_alone()
    {
        const string Below = "## Notes from Brian\n\nStill mine.";
        string body = $"## Acceptance criteria\n\n- [ ] One\n- [ ] Two\n\n{Below}";

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["Only this one now"]);

        rewritten.Should().Contain(Below)
            .And.Contain("- [ ] Only this one now")
            .And.NotContain("- [ ] One")
            .And.NotContain("- [ ] Two");
    }

    [Fact]
    public void A_checklist_regenerated_into_a_body_that_had_none_lands_at_the_end()
    {
        string rewritten = GitHubIssueBody.WithCriteriaChecklist("## Objective\n\nSomething.", ["The only one"]);

        rewritten.Should().Be("## Objective\n\nSomething.\n\n## Acceptance criteria\n\n- [ ] The only one");
    }

    [Fact]
    public void A_body_with_no_checklist_heading_and_a_blank_criteria_set_is_returned_unchanged()
    {
        GitHubIssueBody.WithCriteriaChecklist("## Objective\n\nSomething.", [])
            .Should().Be("## Objective\n\nSomething.");
    }
}
