using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="AgentPromptBuilder.BuildStackAssessment"/> — the read-only assessment prompt (task:
/// a stacked checkpoint that would park for a human on a git shape first dispatches a read-only
/// assessment run). Loaded from <c>.claude/templates/agent-prompt-builder/stack-assessment.md</c>
/// through the same <c>PromptTemplates</c> machinery every other prompt in this file exercises, so
/// a leftover <c>{{Placeholder}}</c> here is exactly the defect it would be anywhere else: a
/// fragment the builder forgot to fill.
/// </summary>
[Collection("Hall9kHome")]
public sealed class StackAssessmentPromptTests
{
    [Fact]
    public void Every_placeholder_is_filled_and_the_read_only_and_trailer_contract_survive()
    {
        string prompt = AgentPromptBuilder.BuildStackAssessment(
            DomainId.New(), "task/child-branch", "task/parent-branch", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 42, "main", "ReplayConflict",
            "A mechanical git rebase --onto attempt conflicted.");

        Regex.Matches(prompt, "{{[A-Za-z0-9_]+}}").Should().BeEmpty(
            "every placeholder BuildStackAssessment declares must be substituted, never left literal");

        prompt.Should().Contain("task/child-branch").And.Contain("task/parent-branch")
            .And.Contain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            .And.Contain("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            .And.Contain("#42").And.Contain("main").And.Contain("ReplayConflict")
            .And.Contain("A mechanical git rebase --onto attempt conflicted.");

        prompt.Should().Contain("read-only", "the run must be told plainly it may not rebase, push, or commit");
        prompt.Should().Contain("Do not rebase, push, or commit");

        prompt.Should().Contain(AgentPromptBuilder.StackAssessmentVerdictMarker);
        prompt.Should().Contain(AgentPromptBuilder.StackAssessmentBoundaryMarker);
        prompt.Should().Contain(AgentPromptBuilder.StackAssessmentOntoMarker);
        prompt.Should().Contain(AgentPromptBuilder.StackAssessmentEvidenceMarker);
        prompt.Should().Contain("aligned | replay | undecidable");
    }

    [Fact]
    public void No_stacked_parent_and_no_pull_request_yet_still_fills_every_placeholder()
    {
        string prompt = AgentPromptBuilder.BuildStackAssessment(
            DomainId.New(), "task/solo-branch", parentBranch: string.Empty, recordedForkPoint: string.Empty,
            attemptedOntoCommit: string.Empty, pullRequestNumber: null, pullRequestBase: string.Empty,
            parkKind: "UndecidableBoundary", parkText: "The mandatory final pass's own pre-flight rebase conflicted.");

        Regex.Matches(prompt, "{{[A-Za-z0-9_]+}}").Should().BeEmpty(
            "a blank optional value still has a filled placeholder, never a leftover token");
        prompt.Should().Contain("task/solo-branch");
        prompt.Should().Contain("(none — this is the pre-final-pass rebase, not a stacked checkpoint)");
        prompt.Should().Contain("(not yet opened)");
    }
}
