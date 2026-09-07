using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The record section's place inside a GitHub issue body (task: a published task's GitHub issue
/// carries the whole task record). Two promises are load-bearing and both are asserted here: the
/// block round-trips byte for byte, and everything above it is a human's to keep — a rewrite that
/// took someone's own prose with it would make the feature a liability rather than a convenience.
/// </summary>
public sealed class GitHubIssueBodyRecordTests
{
    private static TaskRecord Record(string objective = "Adopt the whole task, not three fields of it") =>
        new(
            "hall9k",
            "feature",
            objective,
            ["The record round-trips", "Adoption reads it once"],
            "Some agent context.",
            null,
            PreApprovalMode.Off,
            [],
            0,
            null,
            null,
            TaskRecordCaps.None,
            new TaskOrigin(
                Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
                "HALLMANAC-MAC",
                Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
                "task/7325335a-slug",
                new DateTimeOffset(2026, 9, 7, 15, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void The_record_goes_in_collapsed_at_the_foot_under_a_summary_that_says_what_it_is()
    {
        string body = GitHubIssueBody.WithRecord(
            GitHubIssueBody.Compose("Context.", ["One criterion"]), Record());

        body.Should().Contain("## Acceptance criteria")
            .And.Contain("<details>")
            .And.Contain(GitHubIssueBody.RecordSummary)
            .And.Contain("```yaml");
        body.IndexOf("## Acceptance criteria", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("<details>", StringComparison.Ordinal),
                "a human reads the objective and the checklist first and the record last");
    }

    [Fact]
    public void The_block_round_trips_out_of_the_body_byte_for_byte()
    {
        TaskRecord record = Record();
        string body = GitHubIssueBody.WithRecord("## Objective\n\nSomething.", record);

        GitHubIssueBody.TryReadRecordYaml(body).Should().Be(record.ToYaml());
    }

    [Fact]
    public void The_block_survives_the_crlf_line_endings_github_hands_a_body_back_in()
    {
        TaskRecord record = Record();
        string body = GitHubIssueBody.WithRecord("## Objective\n\nSomething.", record)
            .ReplaceLineEndings("\r\n");

        GitHubIssueBody.TryReadRecordYaml(body).Should().Be(record.ToYaml());
    }

    [Fact]
    public void A_record_whose_context_carries_its_own_fence_still_closes_correctly()
    {
        TaskRecord record = Record() with { AgentContext = "Look at:\n\n```csharp\nvar x = 1;\n```\n" };

        string body = GitHubIssueBody.WithRecord(null, record);

        GitHubIssueBody.TryReadRecordYaml(body).Should().Be(record.ToYaml());
        TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(body))!.AgentContext
            .Should().Be(record.AgentContext);
    }

    [Fact]
    public void Rewriting_the_record_replaces_it_rather_than_adding_a_second_one()
    {
        string first = GitHubIssueBody.WithRecord("## Objective\n\nSomething.", Record());
        string second = GitHubIssueBody.WithRecord(first, Record("A reworded objective"));

        second.Split(GitHubIssueBody.RecordSummary).Length.Should().Be(2, "exactly one record section");
        TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(second))!.Objective
            .Should().Be("A reworded objective");
    }

    [Fact]
    public void A_humans_own_edits_above_the_record_survive_a_rewrite_untouched()
    {
        const string HumanProse =
            "## Objective\n\nSomething.\n\n## Notes from Brian\n\nThis one matters for the Mac node.\n\n"
            + "## Acceptance criteria\n\n- [ ] One criterion";
        string first = GitHubIssueBody.WithRecord(HumanProse, Record());

        string second = GitHubIssueBody.WithRecord(first, Record("A reworded objective"));

        second[..second.IndexOf("<details>", StringComparison.Ordinal)].TrimEnd()
            .Should().Be(HumanProse, "only the record section moves");
    }

    [Fact]
    public void Regenerating_the_checklist_leaves_the_prose_above_and_the_record_below_alone()
    {
        const string HumanProse = "## Objective\n\nSomething.\n\n## Notes from Brian\n\nStill mine.";
        string body = GitHubIssueBody.WithRecord(
            $"{HumanProse}\n\n## Acceptance criteria\n\n- [ ] The old one", Record());

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["The new one", "And another"]);

        rewritten.Should().Contain(HumanProse)
            .And.Contain("- [ ] The new one")
            .And.Contain("- [ ] And another")
            .And.NotContain("- [ ] The old one")
            .And.Contain(GitHubIssueBody.RecordSummary);
        rewritten.IndexOf("- [ ] The new one", StringComparison.Ordinal)
            .Should().BeLessThan(rewritten.IndexOf("<details>", StringComparison.Ordinal));
    }

    /// <summary>
    /// The prose case the checklist rewrite used to eat: a note with no heading of its own, sitting
    /// between the checklist and the record. The span ran to the next <c>## </c> heading — and with
    /// none there, all the way to the record section — so regenerating the checklist replaced the
    /// note with it and deleted a human's own text, which is precisely the liability this file's
    /// own header disclaims (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_note_with_no_heading_under_the_checklist_survives_the_checklist_being_regenerated()
    {
        const string Note = "One of these came from Brian on the Mac, and it matters.";
        string body = GitHubIssueBody.WithRecord(
            $"## Objective\n\nSomething.\n\n## Acceptance criteria\n\n- [x] The old one\n\n{Note}", Record());

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["The new one"]);

        rewritten.Should().Contain(Note, "a human's own note is theirs to keep")
            .And.Contain("- [ ] The new one")
            .And.NotContain("The old one");
        rewritten.IndexOf(Note, StringComparison.Ordinal).Should().BeGreaterThan(
            rewritten.IndexOf("- [ ] The new one", StringComparison.Ordinal),
            "the note stays where it was — under the checklist, above the record");
        rewritten.IndexOf(Note, StringComparison.Ordinal).Should().BeLessThan(
            rewritten.IndexOf("<details>", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same protection when a heading does intervene, which is the case the old span handled:
    /// the section still ends at the last item, so the prose under the heading below it is
    /// untouched and so is the heading.
    /// </summary>
    [Fact]
    public void A_heading_below_the_checklist_and_its_prose_are_both_left_alone()
    {
        const string Below = "## Notes from Brian\n\nStill mine.";
        string body = GitHubIssueBody.WithRecord(
            $"## Acceptance criteria\n\n- [ ] One\n- [ ] Two\n\n{Below}", Record());

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["Only this one now"]);

        rewritten.Should().Contain(Below)
            .And.Contain("- [ ] Only this one now")
            .And.NotContain("- [ ] One")
            .And.NotContain("- [ ] Two");
    }

    [Fact]
    public void A_checklist_regenerated_into_a_body_that_had_none_lands_above_the_record()
    {
        string body = GitHubIssueBody.WithRecord("## Objective\n\nSomething.", Record());

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["The only one"]);

        rewritten.IndexOf("## Acceptance criteria", StringComparison.Ordinal)
            .Should().BeLessThan(rewritten.IndexOf("<details>", StringComparison.Ordinal));
    }

    /// <summary>
    /// The defect this pins is not hypothetical: an adopted task's agent context is the issue body
    /// it came from, quoted whole, and a hall9k-published issue body contains a
    /// <c>&lt;details&gt;</c> section, a <c>&lt;/details&gt;</c> and an <c>## Acceptance criteria</c>
    /// heading. So the record's own YAML routinely carries all three, and a rewriter that ended the
    /// section at the first closing tag would cut it off inside its own YAML — leaving the tail
    /// behind and making the block unreadable from then on. This feature's own issue, #266, is the
    /// example.
    /// </summary>
    [Fact]
    public void A_record_whose_context_quotes_a_details_section_is_still_found_whole()
    {
        TaskRecord record = Record() with
        {
            AgentContext =
                "Imported from github:Hallmanac/hall9k#266.\n\n"
                + "## Acceptance criteria\n\n- [ ] something the origin wrote\n\n"
                + "<details>\n<summary>Hall9k task record (machine-readable, maintained by hall9k; "
                + "adopt with h9k task add --from-issue)</summary>\n\nquoted yaml would sit here\n\n"
                + "</details>\n",
        };
        string body = GitHubIssueBody.WithRecord("## Objective\n\nSomething.", record);

        GitHubIssueBody.TryReadRecordYaml(body).Should().Be(record.ToYaml());

        string rewritten = GitHubIssueBody.WithRecord(body, record with { Objective = "Reworded" });

        rewritten.Split("<details>").Length.Should().Be(
            3, "one section of ours, plus the one quoted inside its YAML — never a second of ours");
        TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(rewritten))!.Objective
            .Should().Be("Reworded");
        rewritten[..rewritten.IndexOf("<details>", StringComparison.Ordinal)].TrimEnd()
            .Should().Be("## Objective\n\nSomething.");
    }

    [Fact]
    public void A_checklist_regenerated_never_reaches_the_one_quoted_inside_the_record()
    {
        TaskRecord record = Record() with
        {
            AgentContext = "## Acceptance criteria\n\n- [ ] a heading the origin's own body carried\n",
        };
        // No checklist of ours above the record — the only one in the body is inside the YAML.
        string body = GitHubIssueBody.WithRecord("## Objective\n\nSomething.", record);

        string rewritten = GitHubIssueBody.WithCriteriaChecklist(body, ["Ours"]);

        TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(rewritten))!.AgentContext
            .Should().Be(record.AgentContext, "the record's YAML is not somewhere a checklist is spliced");
        rewritten.IndexOf("- [ ] Ours", StringComparison.Ordinal)
            .Should().BeLessThan(rewritten.IndexOf("<details>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_body_with_no_record_reads_as_carrying_none()
    {
        GitHubIssueBody.TryReadRecordYaml("## Objective\n\nJust an issue somebody filed.")
            .Should().BeNull();
        GitHubIssueBody.TryReadRecordYaml(null).Should().BeNull();
    }

    [Fact]
    public void A_hand_written_record_section_with_no_fence_ends_at_its_own_closing_tag()
    {
        // A section somebody wrote without a fenced block, with an unrelated code block and another
        // collapsed section further down the body. The record section has to end at its OWN closing
        // tag, not stretch to a fence that is not inside it.
        const string Body =
            "## Objective\n\nSomething.\n\n"
            + $"<details>\n<summary>{GitHubIssueBody.RecordSummary}</summary>\n\nnot fenced\n\n</details>\n\n"
            + "## Repro\n\n```sh\ngh issue view 266\n```\n\n"
            + "<details>\n<summary>Logs</summary>\n\nnoisy\n\n</details>";

        string rewritten = GitHubIssueBody.WithRecord(Body, Record());

        rewritten.Should().Contain("## Repro").And.Contain("gh issue view 266").And.Contain("<summary>Logs</summary>")
            .And.NotContain("not fenced");
        GitHubIssueBody.TryReadRecordYaml(rewritten).Should().Be(Record().ToYaml());
    }

    [Fact]
    public void A_details_section_that_is_not_ours_is_left_alone()
    {
        const string TheirDetails = "<details>\n<summary>Repro steps</summary>\n\nOpen the app.\n\n</details>";

        string body = GitHubIssueBody.WithRecord(TheirDetails, Record());

        body.Should().StartWith(TheirDetails, "somebody else's collapsed section is not the record's");
        GitHubIssueBody.TryReadRecordYaml(body).Should().Be(Record().ToYaml());
    }
}
