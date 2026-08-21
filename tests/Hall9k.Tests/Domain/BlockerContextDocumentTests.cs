using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one context document both the agent's prompt and h9k task show are built from
/// (Decisions Log #36): handoffs where there are handoffs, the blocker's own intent where
/// there are not, and the depth-one rule stated to the agent that has to live under it.
/// </summary>
public sealed class BlockerContextDocumentTests
{
    [Fact]
    public void A_task_with_no_blockers_renders_nothing_rather_than_an_empty_section()
    {
        BlockerContextDocument.Render([]).Should().BeNull(
            "an absent section beats a section announcing that it has nothing to say");
    }

    [Fact]
    public void A_captured_handoff_travels_verbatim()
    {
        Guid blockerId = DomainId.New();
        string document = BlockerContextDocument.Render(
        [
            new BlockerHandoff(
                blockerId, "Ship the run projection", ["projection replays"], TaskState.Done,
                HandoffOutcome.Captured,
                "The projection is Inline; a stopped stream keeps its last document shape."),
        ])!;

        document.Should().Contain("Ship the run projection");
        document.Should().Contain("The projection is Inline");
        document.Should().Contain(blockerId.ToString("N")[^8..], "the blocker is traceable back to its task");
    }

    [Fact]
    public void A_blocker_with_no_handoff_falls_back_to_its_objective_and_criteria()
    {
        string document = BlockerContextDocument.Render(
        [
            new BlockerHandoff(
                DomainId.New(), "Ship the schema", ["the migration applies", "the column is indexed"],
                TaskState.Done, HandoffOutcome.NotAuthored, null),
        ])!;

        document.Should().Contain("Ship the schema");
        document.Should().Contain("- the migration applies");
        document.Should().Contain("- the column is indexed");
        document.Should().Contain(HandoffOutcome.NotAuthored.Describe(),
            "the reason there is no handoff is printed, never left as a blank to interpret");
    }

    [Fact]
    public void A_summary_recorded_against_a_non_captured_outcome_is_not_trusted_as_one()
    {
        // Defensive: HasSummary is the gate, so a stream that somehow carries text against an
        // absence outcome falls back rather than presenting the text as a handoff.
        string document = BlockerContextDocument.Render(
        [
            new BlockerHandoff(
                DomainId.New(), "Ship the gate", ["the gate runs"], TaskState.Done,
                HandoffOutcome.NotCaptured, "text nobody observed as a handoff"),
        ])!;

        document.Should().NotContain("text nobody observed as a handoff");
        document.Should().Contain("- the gate runs");
    }

    [Fact]
    public void The_document_tells_the_agent_the_depth_is_one_and_why()
    {
        string document = BlockerContextDocument.Render(
        [
            new BlockerHandoff(
                DomainId.New(), "Ship the schema", ["applies"], TaskState.Done,
                HandoffOutcome.Captured, "Watch the nullable column."),
        ])!;

        document.Should().StartWith(BlockerContextDocument.Heading);
        document.Should().Contain("IMMEDIATE blockers only");
        document.Should().Contain("missing dependency edge",
            "a needed two-hop fact is evidence of a missing edge, and the agent is the one who can report it");
    }

    [Fact]
    public void Blockers_are_numbered_in_declared_order()
    {
        string document = BlockerContextDocument.Render(
        [
            new BlockerHandoff(DomainId.New(), "First", [], TaskState.Done, HandoffOutcome.Captured, "one"),
            new BlockerHandoff(DomainId.New(), "Second", [], TaskState.Done, HandoffOutcome.Captured, "two"),
        ])!;

        document.IndexOf("### 1. First", StringComparison.Ordinal).Should().BeGreaterThan(-1);
        document.IndexOf("### 2. Second", StringComparison.Ordinal)
            .Should().BeGreaterThan(document.IndexOf("### 1. First", StringComparison.Ordinal));
    }
}
