using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// How a section that carries a picked set words itself. The newest-first wording is pinned by the
/// existing golden fixtures and is not repeated here; these tests are about the one thing that is new,
/// which is that a picked set must not claim to be the newest.
/// </summary>
public sealed class LessonPickerSectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid AnotherNode = DomainId.New();

    [Fact]
    public void A_picked_section_says_it_was_chosen_for_the_task_and_never_that_it_is_the_newest()
    {
        InjectedLessons picked = PickedSection(out int eligible);

        string section = Section(picked);

        section.Should().Contain("chosen for this task rather than taken newest first");
        section.Should().Contain($"That is 2 of the {eligible} lessons eligible for this prompt, and {eligible - 2} are not carried here");
        section.Should().Contain("h9k learn list");
        section.Should().NotContain("newest first, Each");
        section.Should().NotContain("is the newest that fit");
    }

    [Fact]
    public void A_picked_section_reconciles_its_counts_with_the_active_and_eligible_totals()
    {
        InjectedLessons picked = PickedSection(withHeldProvenance: true, out int eligible);

        string section = Section(picked);

        picked.ActiveInScope.Should().Be(eligible + 1);
        section.Should().Contain($"{eligible + 1} lessons are live across this project and its owner")
            .And.Contain($"the {eligible} eligible was held back on provenance");
        section.Should().Contain("Held out of this section on provenance");
    }

    [Fact]
    public void A_pick_that_carries_every_eligible_lesson_still_says_how_many_it_does_not_carry()
    {
        LearningDetails only = Lesson("The only lesson", Now);
        LessonCandidates candidates = LessonInjection.Eligible([only], [], ThisNode, LessonInjectionCaps.Default);

        string section = Section(LessonInjection.SelectPicked(candidates, [only.Id])!);

        section.Should().Contain("That is 1 of the 1 lessons eligible for this prompt, and 0 are not carried here");
    }

    [Fact]
    public void The_newest_first_section_keeps_the_wording_it_always_had()
    {
        List<LearningDetails> lessons = [.. Enumerable.Range(0, 5).Select(index => Lesson($"Claim {index}", Now.AddMinutes(-index)))];
        InjectedLessons newest = LessonInjection.Compose(lessons, [], ThisNode, new LessonInjectionCaps(2, 3000));

        string section = Section(newest);

        section.Should().Contain("newest first. Each one").And.Contain("is the newest that fit rather than");
        section.Should().NotContain("chosen for this task");
    }

    [Fact]
    public void A_build_prompt_carries_a_picked_section_the_same_way_it_carries_any_other()
    {
        InjectedLessons picked = PickedSection(out _);
        TaskDetails task = new() { Id = DomainId.New(), Objective = "Add a thing", AcceptanceCriteria = ["It works"] };
        ProjectDetails project = new() { Id = Project, Name = "hall9k", BaseBranch = "main" };
        string worktree = Directory.CreateTempSubdirectory("hall9k-picked-lessons-").FullName;
        try
        {
            string prompt = AgentPromptBuilder.Build(task, project, "task/1-slug", worktree, lessons: picked);

            Collapsed(prompt).Should().Contain("chosen for this task rather than taken newest first");
            foreach (InjectedLesson lesson in picked.Lessons)
            {
                prompt.Should().Contain(lesson.Line);
            }
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    private static string Section(InjectedLessons lessons)
    {
        StringBuilder builder = new();
        WorkPromptBuilder.AppendRecordedLessons(builder, lessons, DomainId.New());
        return Collapsed(builder.ToString());
    }

    /// <summary>The template hard-wraps its prose, so a phrase is looked for with the line breaks read as single spaces.</summary>
    private static string Collapsed(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static InjectedLessons PickedSection(out int eligible) => PickedSection(withHeldProvenance: false, out eligible);

    private static InjectedLessons PickedSection(bool withHeldProvenance, out int eligible)
    {
        List<LearningDetails> lessons = [.. Enumerable.Range(0, 6).Select(index => Lesson($"Claim {index}", Now.AddMinutes(-index)))];
        if (withHeldProvenance)
        {
            lessons.Add(AgentLesson("Held back", AnotherNode));
        }

        LessonCandidates candidates = LessonInjection.Eligible(lessons, [], ThisNode, new LessonInjectionCaps(2, 10_000));
        eligible = candidates.Eligible.Count;
        return LessonInjection.SelectPicked(candidates, [lessons[5].Id, lessons[3].Id])!;
    }

    private static LearningDetails Lesson(string statement, DateTimeOffset recordedAt) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(Owner),
        RecordedAt = recordedAt,
        Status = LearningStatus.Active,
    };

    private static LearningDetails AgentLesson(string statement, Guid recordedOnNodeId) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = new RecordedProvenance(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended),
        RecordedAt = Now.AddDays(-30),
        RecordedOnNodeId = recordedOnNodeId,
        Status = LearningStatus.Active,
    };
}
