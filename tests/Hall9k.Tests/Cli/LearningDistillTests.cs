using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Distillation as a human-authored act (idea d805fd8b, piece 5; backlog 55): what the task
/// <c>h9k learn distill</c> writes actually asks for, and the standing guarantee that no daemon
/// code path ever authors one on its own judgment.
/// </summary>
public sealed class LearningDistillTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    private static readonly ResolvedKnowledgeScope ProjectScope = new(KnowledgeScope.Project, Project, "hall9k");

    [Fact]
    public void The_objective_names_the_scope_the_count_and_the_merge_and_cite_boundary()
    {
        string objective = LearningDistillCommand.Objective(ProjectScope, lessonCount: 31);

        objective.Should().Contain("hall9k").And.Contain("31");
        objective.Should().Contain("merging and citing only");
    }

    /// <summary>
    /// Merge-and-cite lives in the acceptance criteria rather than only in the context, because
    /// the criteria are what a review pass grades against. Advice buried in context is advice a
    /// session can drift from with nothing to catch it.
    /// </summary>
    [Fact]
    public void The_acceptance_contract_states_merge_and_cite_the_citation_verb_and_the_retirement()
    {
        IReadOnlyList<string> criteria = LearningDistillCommand.Criteria();

        criteria.Should().HaveCountGreaterThan(1);
        string all = string.Join("\n", criteria);
        all.Should().Contain("--distilled-from", "the criterion names the verb that actually enforces the citation");
        all.Should().Contain("refuses a distilled lesson that cites nothing");
        all.Should().Contain("h9k learn retire", "a merged source left live rides in a prompt twice");
        all.Should().Contain("record nothing this project has not already learned");
    }

    private const string ExpectedExitCriterion =
        "Every lesson merged away is retired citing its survivor, and the findings document accounts for every "
        + "lesson in the inventory as merged into a named new lesson, retired with a reason, left alone, or not "
        + "reached; or it says no merge was warranted.";

    /// <summary>
    /// A distillation ends with findings and no commits, so it is a research spike (no gates, no
    /// pull request) rather than a Research task, whose pull request opening failed on the empty branch.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_task_a_distillation_authors_is_a_research_spike_with_the_stated_exit_criterion(bool ownerScoped)
    {
        ResolvedKnowledgeScope scope = ownerScoped
            ? new ResolvedKnowledgeScope(KnowledgeScope.Owner, Owner, "you, across every project")
            : ProjectScope;
        LearningDetails[] active = [Lesson("One claim"), Lesson("Another claim")];

        TaskAdded added = LearningDistillCommand.Author(
            DomainId.New(), ProjectScope, scope, active, ownerScoped, Now, Owner);

        added.Type.Should().Be(TaskType.Spike);
        added.SpikeKind.Should().Be(SpikeKind.Research);
        added.ExitCriterion.Should().Be(ExpectedExitCriterion);
        added.ProjectId.Should().Be(Project, "the task is tracked in the project even when the lessons are the owner's");
        added.Objective.Should().Be(LearningDistillCommand.Objective(scope, active.Length));
        added.AcceptanceCriteria.Should().Equal(LearningDistillCommand.Criteria());
        added.StartsAsDraft.Should().BeTrue();
    }

    [Fact]
    public void The_spike_prompt_for_a_distillation_carries_every_merge_and_cite_rule()
    {
        LearningDetails[] active = [Lesson("One claim"), Lesson("Another claim")];
        TaskAdded added = LearningDistillCommand.Author(
            DomainId.New(), ProjectScope, ProjectScope, active, ownerScoped: false, Now, Owner);
        TaskDetails task = new()
        {
            Id = added.Id,
            Type = added.Type,
            Objective = added.Objective,
            AcceptanceCriteria = [.. added.AcceptanceCriteria],
            AgentContext = added.AgentContext,
            SpikeKind = added.SpikeKind ?? SpikeKind.Unknown,
            ExitCriterion = added.ExitCriterion,
        };

        // The shipped context is hard-wrapped prose, so compare with line breaks folded to spaces.
        string prompt = Regex.Replace(AgentPromptBuilder.BuildSpike(task), @"\s+", " ");
        string fixPrompt = Regex.Replace(AgentPromptBuilder.BuildSpikeFix(task, "A source was not retired"), @"\s+", " ");

        prompt.Should().Contain("--distilled-from", "only merges that cite their sources are recorded");
        prompt.Should().Contain($"--task {added.Id}", "a recorded lesson is credited to this run, not left looking human-typed");
        prompt.Should().Contain("record nothing this project has not already learned");
        prompt.Should().Contain("h9k learn retire", "each merged source is retired citing its survivor");
        prompt.Should().Contain("Absorbed into");
        prompt.Should().Contain("A lesson that says one thing nothing else says is left exactly as it is");
        prompt.Should().Contain("\"What was observed\"", "the account of the inventory lives in the findings document");
        prompt.Should().Contain("is this spike's work and is not the code a research spike is told not to write");

        // The fix lap records and retires against the live store with no review after it, so it needs the same rules.
        fixPrompt.Should().Contain("--distilled-from");
        fixPrompt.Should().Contain($"--task {added.Id}");
        fixPrompt.Should().Contain("Absorbed into");
    }

    [Fact]
    public void Every_criterion_is_one_line_so_a_task_card_renders_it_as_one_bullet()
    {
        foreach (string criterion in LearningDistillCommand.Criteria())
        {
            criterion.Should().NotContain("\n");
        }
    }

    [Fact]
    public void The_context_labels_its_inventory_as_a_snapshot_and_names_the_live_command()
    {
        LearningDetails lesson = Lesson("A claim earlier runs established");

        string context = LearningDistillCommand.Context(
            DomainId.New(),
            ProjectScope, [lesson], ownerScoped: false, "hall9k", Now);

        context.Should().Contain("h9k learn list --project hall9k", "the live set is a command, not this snapshot");
        context.Should().Contain("Snapshot taken 2026-09-21 12:00:00Z");
        context.Should().Contain($"- [{DomainId.Short(lesson.Id)}] A claim earlier runs established");
    }

    [Fact]
    public void An_owner_scoped_distillation_points_at_the_owner_list_rather_than_a_projects()
    {
        string context = LearningDistillCommand.Context(
            DomainId.New(),
            new ResolvedKnowledgeScope(KnowledgeScope.Owner, Owner, "you, across every project"),
            [Lesson("A habit of mine")], ownerScoped: true, "hall9k", Now);

        context.Should().Contain("h9k learn list --owner");
    }

    [Fact]
    public void A_snapshot_longer_than_the_bound_says_how_many_it_is_showing()
    {
        LearningDetails[] inventory = [.. Enumerable
            .Range(0, LearningDistillCommand.SnapshotLessons + 5)
            .Select(index => Lesson($"Lesson {index}"))];

        string context = LearningDistillCommand.Context(
            DomainId.New(),
            ProjectScope, inventory, ownerScoped: false, "hall9k", Now);

        context.Should().Contain($"{inventory.Length} active");
        context.Should().Contain($"the first {LearningDistillCommand.SnapshotLessons} shown");
        context.Should().NotContain($"- [{DomainId.Short(inventory[^1].Id)}]");
    }

    [Fact]
    public void The_context_says_a_held_out_lesson_is_read_only_for_this_task()
    {
        string context = LearningDistillCommand.Context(
            DomainId.New(),
            ProjectScope, [Lesson("One claim")], ownerScoped: false, "hall9k", Now);

        context.Should().Contain("another node");
        context.Should().Contain("7e403b80", "merging a held-out lesson into an injected one would route around the hold");
    }

    /// <summary>
    /// The standing guarantee, checked against the source rather than asserted in prose: nothing
    /// in the daemon authors a distillation. Read as text because the property is the absence of a
    /// call, and no runtime test can observe an absence across every code path.
    /// <para>
    /// Same shape as <c>StackedBaseBranchGuardTests</c>'s own source sweep, and for the same
    /// reason: the invariant is about what the code does not do, and a future edit that adds a
    /// sweep deciding a project's lessons need merging should fail here rather than ship.
    /// </para>
    /// </summary>
    [Fact]
    public void No_daemon_code_path_authors_a_distillation()
    {
        string daemon = Path.Combine(PublishTestSupport.FindRepositoryRoot(), "src", "Hall9k.Daemon");
        Directory.Exists(daemon).Should().BeTrue();

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(daemon, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Contains("LearningDistillCommand", StringComparison.Ordinal)
                    || line.Contains("RecordDistilled", StringComparison.Ordinal)
                    || line.Contains("DistilledFrom", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(daemon, file)}:{index + 1}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "distillation is authored by a human running h9k learn distill and published by a human; a daemon "
            + "reference to it would mean something decided on its own that a project's lessons needed merging");
    }

    [Fact]
    public void Status_says_nothing_while_a_projects_lessons_still_fit_in_a_prompt()
    {
        StatusCommand.LessonsOverCapLine("hall9k", activeLessons: 15, maxLessons: 15).Should().BeNull();
        StatusCommand.LessonsOverCapLine("hall9k", activeLessons: 0, maxLessons: 15).Should().BeNull();
    }

    /// <summary>
    /// Over the cap, the board names the lever and the shortfall. It never authors the task
    /// itself: that is the whole line between what the platform does and what a person decides.
    /// </summary>
    [Fact]
    public void Status_names_the_distillation_lever_once_a_project_is_over_the_prompt_cap()
    {
        string? line = StatusCommand.LessonsOverCapLine("hall9k", activeLessons: 34, maxLessons: 15);

        line.Should().NotBeNull();
        line.Should().Contain("34 active lessons").And.Contain("over the 15");
        line.Should().Contain("19 never reach one");
        line.Should().Contain("h9k learn distill --project hall9k");
        line.Should().Contain("h9k learn list", "retiring what is done is the other lever");
    }

    private static LearningDetails Lesson(string statement) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(Owner),
        RecordedAt = Now,
        Status = LearningStatus.Active,
    };
}
