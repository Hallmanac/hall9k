using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>What a task's feedback step asks of the refinement session: what a revision may name, the closing word for a task that should not run, and the prompts each purpose gets.</summary>
public sealed class FeedbackRefinementTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();

    public void Dispose() => _scopedHome.Dispose();

    [Theory]
    [InlineData("---\nobjective: New\ncriteria:\n  - >-\n    One.\n---\nBody\n")]
    [InlineData("---\ncriteria:\n  - Only this.\n---\n")]
    public void A_revision_that_names_only_the_objective_the_criteria_and_the_context_names_nothing_beyond_them(string revision) =>
        FeedbackRevisionRules.FirstKeyBeyondText(revision).Should().BeNull();

    [Theory]
    [InlineData("---\nobjective: New\nmodel: claude-opus-5-5\n---\nBody\n", "model")]
    [InlineData("---\nobjective: New\ntype: spike\n---\nBody\n", "type")]
    [InlineData("---\nobjective: New\nblocked-by:\n  - 01a09285-6a21-7259-a883-c5ebe5482c3e\n---\n", "blocked-by")]
    [InlineData("---\nobjective: New\nstacked-on: 01a09285-6a21-7259-a883-c5ebe5482c3e\n---\n", "stacked-on")]
    [InlineData("---\nobjective: New\nstacked-on-pull-request: 12\n---\n", "stacked-on-pull-request")]
    [InlineData("---\nobjective: New\nepic: 01a09285-6a21-7259-a883-c5ebe5482c3e\n---\n", "epic")]
    [InlineData("---\nobjective: New\nmodel:\n---\nBody\n", "model")]
    [InlineData("---\nobjective: New\nblocked-by: []\n---\n", "blocked-by")]
    [InlineData("---\nobjective: New\nproject: other\n---\n", "project")]
    public void A_revision_that_names_any_key_beyond_the_text_even_with_no_value_is_refused_by_the_first_such_key(string revision, string key) =>
        FeedbackRevisionRules.FirstKeyBeyondText(revision).Should().Be(key);

    [Fact]
    public void The_refinement_report_reads_should_not_run_as_a_fourth_closing_word()
    {
        RefinementReport.TryRead("The notes remove the reason for this task.\n\nREFINEMENT: should-not-run\n").Should().Be(RefinementOutcome.ShouldNotRun);
        RefinementReport.TryRead("REFINEMENT: should not run").Should().Be(RefinementOutcome.ShouldNotRun);
        RefinementReport.TryRead("REFINEMENT: revised").Should().Be(RefinementOutcome.Revised);
    }
    private static TaskDetails Task() => new()
    {
        Id = Guid.Parse("01a09285-6a21-7259-a883-c5ebe5482c3e"),
        Objective = "Page through the results",
        AcceptanceCriteria = ["The list endpoint returns every item"],
        AgentContext = "Read the list endpoint first.",
    };

    private static InjectedTaskNotes Notes(params string[] texts) =>
        new(
            [.. texts.Select(text => new InjectedTaskNote(Guid.Parse("01a199f6-f4c3-75bb-b84a-493f0b340ae2"), false, text, LessonProvenanceMark.AgentOnThisNode))],
            LeftOut: 0, HeldByMark: []);

    private static PlanRefinementPromptFacts Facts(RefinementPurpose purpose, FeedbackStepPromptFacts? feedback, PriorVerdictFacts? prior = null) =>
        new(
            purpose, "/h/demo", "/h/demo/repo/dev", "abc123", new CheckoutRefresh(true, "is up to date"),
            "/runs/s1/refinement-report.md", "/runs/s1/refinement-revision.md", "/runs/s1", null, null, "/h/demo/tasks/t/journal.md", prior,
            FeedbackStep: feedback);

    [Fact]
    public void The_feedback_refinement_prompt_carries_the_notes_limits_the_revision_and_offers_should_not_run()
    {
        string prompt = AgentPromptBuilder.BuildPlanRefinement(
            Task(), new ProjectDetails { Name = "demo", WritingConventions = WritingConventions.Default },
            Facts(RefinementPurpose.FeedbackRefine, new FeedbackStepPromptFacts(Notes("The API returns pages."))));

        prompt.Should().Contain("0b340ae2: The API returns pages.")
            .And.Contain("back to Draft")
            .And.Contain("challenged once")
            .And.Contain("There is no second challenge")
            .And.Contain("must not name a `type`, `blocked-by`, `stacked-on`")
            .And.Contain("REFINEMENT: <revised | unchanged | question | should-not-run>");
    }

    [Fact]
    public void The_judging_prompt_tells_the_session_to_judge_each_change_and_record_a_reason_for_each_declined_one_in_the_journal()
    {
        PriorVerdictFacts prior = new(ChallengeVerdict.PublishAsRevised, 2, 1, 0, "/runs/c1/challenge-report.md");

        string prompt = AgentPromptBuilder.BuildPlanRefinement(
            Task(), new ProjectDetails { Name = "demo", WritingConventions = WritingConventions.Default },
            Facts(RefinementPurpose.JudgeVerdict, new FeedbackStepPromptFacts(Notes("The API returns pages.")), prior));

        prompt.Should().Contain("Judge each must change and each should change")
            .And.Contain("reason for each declined one in the journal at /h/demo/tasks/t/journal.md")
            .And.Contain("/runs/c1/challenge-report.md")
            .And.Contain("no further challenge")
            .And.NotContain("Apply the verdict's must and should changes");
    }

    [Fact]
    public void A_feedback_step_under_a_solution_epic_is_never_told_its_revision_may_set_blocked_by()
    {
        IReadOnlyList<SolutionMemberTask> members = [new(Guid.Parse("01a09285-6a21-7259-a883-c5ebe5482c3f"), "other", "Ship the client", TaskState.Queued)];
        ProjectDetails project = new() { Name = "demo", WritingConventions = WritingConventions.Default };

        string feedback = AgentPromptBuilder.BuildPlanRefinement(
            Task(), project,
            Facts(RefinementPurpose.FeedbackRefine, new FeedbackStepPromptFacts(Notes("The API returns pages."))) with { OtherMemberTasks = members });
        string ordinary = AgentPromptBuilder.BuildPlanRefinement(
            Task(), project, Facts(RefinementPurpose.Refine, feedback: null) with { OtherMemberTasks = members });

        ordinary.Should().Contain("may set `blocked-by` to a listed task", "an ordinary plan's session may still choose the edge");
        feedback.Should().NotContain("may set `blocked-by`").And.NotContain("Ship the client")
            .And.Contain("must not name a `type`, `blocked-by`, `stacked-on`");
    }

    [Fact]
    public void A_judge_verdict_refinement_prompt_offers_no_split_outcome_and_sends_a_split_to_the_question()
    {
        PriorVerdictFacts prior = new(ChallengeVerdict.PublishAsRevised, 2, 1, 0, "/runs/c1/challenge-report.md");

        string prompt = AgentPromptBuilder.BuildPlanRefinement(
            Task(), new ProjectDetails { Name = "demo", WritingConventions = WritingConventions.Default },
            Facts(RefinementPurpose.JudgeVerdict, new FeedbackStepPromptFacts(Notes("The API returns pages.")), prior));

        prompt.Should().NotContain("REFINEMENT: split").And.NotContain("TASK:").And.NotContain("split outcome")
            .And.Contain("can be accepted only by ending with the question outcome");
    }

    [Fact]
    public void An_ordinary_plans_refinement_prompt_is_unchanged_by_the_feedback_step_fragments()
    {
        string prompt = AgentPromptBuilder.BuildPlanRefinement(
            Task(), new ProjectDetails { Name = "demo", WritingConventions = WritingConventions.Default },
            Facts(RefinementPurpose.Refine, feedback: null));

        prompt.Should().NotContain("feedback step").And.NotContain("should-not-run").And.Contain("REFINEMENT: <revised | unchanged | question>");
    }
}
