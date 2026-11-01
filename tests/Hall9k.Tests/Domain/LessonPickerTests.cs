using FluentAssertions;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The lesson picker's pure rules, with no database: which lessons it is offered, how its answer is
/// read, how a picked set is held to the caps and to what the composing node may carry, and which
/// settings and records let a set into a prompt at all.
/// </summary>
public sealed class LessonPickerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid AnotherNode = DomainId.New();

    [Fact]
    public void The_picker_is_offered_every_eligible_lesson_including_the_ones_only_the_caps_hold_back()
    {
        List<LearningDetails> lessons = [.. Enumerable.Range(0, 6).Select(index => Lesson($"Claim {index}", Now.AddMinutes(-index)))];

        LessonCandidates candidates = LessonInjection.Eligible(
            lessons, [], ThisNode, new LessonInjectionCaps(2, 10_000));

        candidates.Eligible.Should().HaveCount(6, "the caps apply to the selection, never to what the picker is offered");
        candidates.NewestFirst.Lessons.Should().HaveCount(2);
        candidates.NewestFirst.HeldForCap.Should().Be(4);
    }

    [Fact]
    public void A_lesson_held_back_on_provenance_is_offered_to_no_picker_prompt()
    {
        LearningDetails mine = Lesson("Mine", Now);
        LearningDetails foreign = AgentLesson("Foreign", Now.AddMinutes(-1), AnotherNode);

        LessonCandidates candidates = LessonInjection.Eligible([mine, foreign], [], ThisNode, LessonInjectionCaps.Default);

        candidates.Eligible.Select(lesson => lesson.Id).Should().Equal(mine.Id);
        candidates.HeldForProvenanceByMark.Should().ContainSingle()
            .Which.Mark.Should().Be(LessonProvenanceMark.AgentOnAnotherNode);
    }

    [Theory]
    [InlineData("aaaaaaaa bbbbbbbb", true)]
    [InlineData("aaaaaaaa,bbbbbbbb", true)]
    [InlineData("aaaaaaaa, bbbbbbbb\ncccccccc", true)]
    [InlineData("AAAAAAAA", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("aaaaaaaa because it covers projections", false)]
    [InlineData("[aaaaaaaa]", false)]
    [InlineData("aaaaaaa", false)]
    [InlineData("aaaaaaaaa", false)]
    [InlineData("gggggggg", false)]
    [InlineData("Picked: aaaaaaaa", false)]
    public void An_answer_is_usable_only_when_it_is_nothing_but_eight_character_ids(string answer, bool usable)
    {
        LessonPickAnswer.Read(answer, []).Usable.Should().Be(usable);
    }

    [Fact]
    public void An_id_that_names_no_offered_lesson_or_repeats_one_is_dropped_and_counted()
    {
        InjectedLesson first = Offered("First");
        InjectedLesson second = Offered("Second");

        LessonPickAnswerReading reading = LessonPickAnswer.Read(
            $"{first.ShortId} 00000000 {second.ShortId} {first.ShortId} {first.ShortId.ToUpperInvariant()}",
            [first, second]);

        reading.Usable.Should().BeTrue();
        reading.LessonIds.Should().Equal(first.Id, second.Id);
        reading.DroppedIdCount.Should().Be(3, "one unknown id and two repeats of the first");
    }

    [Fact]
    public void A_short_id_two_offered_lessons_share_names_no_single_lesson_and_is_dropped()
    {
        Guid sibling = Guid.Parse("11111111-1111-1111-1111-1111abcdabcd");
        InjectedLesson a = new(Guid.Parse("00000000-0000-0000-0000-0000abcdabcd"), "A", KnowledgeScope.Project, LessonProvenanceMark.NoRunNamed);
        InjectedLesson b = new(sibling, "B", KnowledgeScope.Project, LessonProvenanceMark.NoRunNamed);
        a.ShortId.Should().Be(b.ShortId);

        LessonPickAnswerReading reading = LessonPickAnswer.Read(a.ShortId, [a, b]);

        reading.LessonIds.Should().BeEmpty();
        reading.DroppedIdCount.Should().Be(1);
    }

    [Fact]
    public void A_picked_set_keeps_the_pickers_order_not_the_dates()
    {
        List<LearningDetails> lessons = [.. Enumerable.Range(0, 5).Select(index => Lesson($"Claim {index}", Now.AddMinutes(-index)))];
        LessonCandidates candidates = LessonInjection.Eligible(lessons, [], ThisNode, new LessonInjectionCaps(3, 10_000));

        InjectedLessons? picked = LessonInjection.SelectPicked(
            candidates, [lessons[4].Id, lessons[0].Id, lessons[2].Id]);

        picked.Should().NotBeNull();
        picked!.Lessons.Select(lesson => lesson.Id).Should().Equal(lessons[4].Id, lessons[0].Id, lessons[2].Id);
        picked.ChosenForTask.Should().BeTrue();
    }

    [Fact]
    public void A_picked_set_never_exceeds_the_count_cap()
    {
        List<LearningDetails> lessons = [.. Enumerable.Range(0, 5).Select(index => Lesson($"Claim {index}", Now.AddMinutes(-index)))];
        LessonCandidates candidates = LessonInjection.Eligible(lessons, [], ThisNode, new LessonInjectionCaps(2, 10_000));

        InjectedLessons? picked = LessonInjection.SelectPicked(candidates, lessons.Select(lesson => lesson.Id));

        picked!.Lessons.Should().HaveCount(2);
        picked.HeldForCap.Should().Be(3, "every eligible lesson the section does not carry is counted");
    }

    [Fact]
    public void A_picked_lesson_that_does_not_fit_whole_is_left_out_whole_and_the_next_one_is_still_tried()
    {
        LearningDetails small = Lesson("Short", Now);
        LearningDetails huge = Lesson(new string('x', 400), Now.AddMinutes(-1));
        LearningDetails alsoSmall = Lesson("Also short", Now.AddMinutes(-2));
        LessonCandidates candidates = LessonInjection.Eligible(
            [small, huge, alsoSmall], [], ThisNode, new LessonInjectionCaps(15, 300));

        InjectedLessons? picked = LessonInjection.SelectPicked(candidates, [small.Id, huge.Id, alsoSmall.Id]);

        picked!.Lessons.Select(lesson => lesson.Id).Should().Equal(small.Id, alsoSmall.Id);
        picked.Lessons.Sum(lesson => lesson.Line.Length).Should().BeLessThanOrEqualTo(300);
    }

    [Fact]
    public void Newest_first_still_stops_at_the_first_lesson_that_does_not_fit()
    {
        LearningDetails small = Lesson("Short", Now);
        LearningDetails huge = Lesson(new string('x', 400), Now.AddMinutes(-1));
        LearningDetails alsoSmall = Lesson("Also short", Now.AddMinutes(-2));

        InjectedLessons section = LessonInjection.Compose(
            [small, huge, alsoSmall], [], ThisNode, new LessonInjectionCaps(15, 300));

        section.Lessons.Select(lesson => lesson.Id).Should().Equal(small.Id);
    }

    [Fact]
    public void A_picked_id_that_is_no_longer_eligible_drops_out_and_when_none_remain_the_prompt_is_newest_first()
    {
        LearningDetails retired = Lesson("Retired since", Now.AddMinutes(-3));
        retired.Status = LearningStatus.Retired;
        LearningDetails foreignNow = AgentLesson("Held back since", Now.AddMinutes(-2), AnotherNode);
        LearningDetails stays = Lesson("Still here", Now.AddMinutes(-1));
        LearningDetails newest = Lesson("Newest", Now);
        LessonCandidates candidates = LessonInjection.Eligible(
            [retired, foreignNow, stays, newest], [], ThisNode, LessonInjectionCaps.Default, pickerMode: LessonPickerMode.On);

        InjectedLessons partial = candidates.For(Pick(LessonPickerMode.On, retired.Id, foreignNow.Id, stays.Id));
        InjectedLessons none = candidates.For(Pick(LessonPickerMode.On, retired.Id, foreignNow.Id));

        partial.Lessons.Select(lesson => lesson.Id).Should().Equal(stays.Id);
        partial.ChosenForTask.Should().BeTrue();
        none.ChosenForTask.Should().BeFalse();
        none.Lessons.Select(lesson => lesson.Id).Should().Equal(newest.Id, stays.Id);
    }

    [Theory]
    [InlineData("on", "on", "picked", true)]
    [InlineData("shadow", "shadow", "picked", false)]
    [InlineData("on", "shadow", "picked", false)]
    [InlineData("shadow", "on", "picked", false)]
    [InlineData("off", "on", "picked", false)]
    [InlineData("on", "on", "timed-out", false)]
    [InlineData("on", "on", "empty-pick", false)]
    public void A_recorded_set_reaches_a_prompt_only_when_picked_under_on_and_the_setting_is_still_on(
        string current, string pickedUnder, string outcome, bool carried)
    {
        LearningDetails older = Lesson("Older", Now.AddMinutes(-1));
        LearningDetails newer = Lesson("Newer", Now);
        LessonCandidates candidates = LessonInjection.Eligible(
            [older, newer], [], ThisNode, LessonInjectionCaps.Default, pickerMode: LessonPickerMode.FromInput(current));

        InjectedLessons section = candidates.For(Pick(LessonPickerMode.FromInput(pickedUnder), LessonPickOutcome.FromValue(outcome), older.Id));

        section.ChosenForTask.Should().Be(carried);
        section.Lessons.Select(lesson => lesson.Id).Should().Equal(carried ? [older.Id] : [newer.Id, older.Id]);
    }

    [Theory]
    [InlineData(null, "shadow")]
    [InlineData("", "shadow")]
    [InlineData("off", "off")]
    [InlineData("ON", "on")]
    [InlineData(" shadow ", "shadow")]
    [InlineData("sometimes", "shadow")]
    public void The_configured_word_resolves_with_shadow_as_the_default_and_the_reading_of_anything_unrecognized(
        string? configured, string expected)
    {
        LessonPickerMode.Resolve(configured).Value.Should().Be(expected);
    }

    [Fact]
    public void Only_a_pick_that_started_a_session_counts_as_one()
    {
        LessonPickOutcome.StartFailed.SpawnedSession.Should().BeFalse();
        LessonPickOutcome.All.Where(outcome => outcome != LessonPickOutcome.StartFailed)
            .Should().OnlyContain(outcome => outcome.SpawnedSession);
        LessonPickOutcome.All.Select(outcome => outcome.Value).Should().Equal(
            "picked", "start-failed", "session-error", "no-result", "timed-out", "unusable-answer", "empty-pick");
    }

    [Fact]
    public void A_task_keeps_the_first_pick_it_was_given()
    {
        Guid taskId = DomainId.New();
        TaskDetailsProjection projection = new();
        TaskDetails view = new() { Id = taskId };
        LessonPickRecorded first = Pick(LessonPickerMode.Shadow, LessonPickOutcome.TimedOut) with { Id = taskId };
        LessonPickRecorded second = Pick(LessonPickerMode.On, LessonPickOutcome.Picked, DomainId.New()) with { Id = taskId };

        projection.Apply(new FakeEvent<LessonPickRecorded>(first), view);
        projection.Apply(new FakeEvent<LessonPickRecorded>(second), view);

        view.LessonPick.Should().BeSameAs(first);
    }

    private static InjectedLesson Offered(string statement) =>
        new(DomainId.New(), statement, KnowledgeScope.Project, LessonProvenanceMark.NoRunNamed);

    private static LessonPickRecorded Pick(LessonPickerMode mode, params Guid[] picked) =>
        Pick(mode, LessonPickOutcome.Picked, picked);

    private static LessonPickRecorded Pick(LessonPickerMode mode, LessonPickOutcome outcome, params Guid[] picked) =>
        new(DomainId.New(), mode, outcome, picked, [], 0, 0, AgentModel.Sonnet, TimeSpan.FromSeconds(2), Now);

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

    private static LearningDetails AgentLesson(string statement, DateTimeOffset recordedAt, Guid recordedOnNodeId) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = new RecordedProvenance(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended),
        RecordedAt = recordedAt,
        RecordedOnNodeId = recordedOnNodeId,
        Status = LearningStatus.Active,
    };
}
