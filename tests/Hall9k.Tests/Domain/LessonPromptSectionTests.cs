using System.Reflection;
using FluentAssertions;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The bounded, provenance-marked lesson section a dispatched prompt carries (idea d805fd8b,
/// piece 5; backlog 55), proved with no database and no host identity: the composer is pure, so
/// every rule it holds is checkable against fixture rows (the testing rule, Brian 2026-09-13).
/// </summary>
public sealed class LessonPromptSectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid AnotherNode = DomainId.New();

    [Fact]
    public void Every_line_leads_with_the_id_a_session_cites_and_retires_by()
    {
        LearningDetails lesson = ProjectLesson("sed -i strips CRLF from this repo's files", Now);

        InjectedLessons section = LessonInjection.Compose(
            [lesson], [], ThisNode, LessonInjectionCaps.Default);

        section.Lessons.Should().HaveCount(1);
        section.Lessons[0].Line.Should().StartWith($"- [{DomainId.Short(lesson.Id)}] ")
            .And.Contain("sed -i strips CRLF");
    }

    [Fact]
    public void The_project_lessons_and_the_owner_lessons_are_one_list_newest_first()
    {
        LearningDetails oldest = ProjectLesson("Oldest", Now.AddDays(-3));
        LearningDetails middle = OwnerLesson("Middle", Now.AddDays(-2));
        LearningDetails newest = ProjectLesson("Newest", Now.AddDays(-1));

        InjectedLessons section = LessonInjection.Compose(
            [oldest, newest], [middle], ThisNode, LessonInjectionCaps.Default);

        // Newest first, across both scopes as one list.
        section.Lessons.Select(lesson => lesson.Statement).Should().Equal("Newest", "Middle", "Oldest");
    }

    /// <summary>
    /// The scope is on the line because the two kinds of claim are weighed differently: a habit
    /// that rode in from the owner's other work is the one more likely to be wrong here
    /// (<see cref="KnowledgeScope"/>, "Scope determines travel").
    /// </summary>
    [Fact]
    public void An_owner_scoped_lesson_says_it_travels_everywhere_and_a_project_one_says_it_does_not()
    {
        InjectedLessons section = LessonInjection.Compose(
            [ProjectLesson("A claim about this codebase", Now)],
            [OwnerLesson("A habit of mine", Now.AddMinutes(-1))],
            ThisNode,
            LessonInjectionCaps.Default);

        section.Lessons[0].Line.Should().Contain("(this project;");
        section.Lessons[1].Line.Should().Contain("(yours across every project;");
    }

    [Fact]
    public void A_retired_lesson_is_not_carried_and_is_not_counted_as_held_back()
    {
        LearningDetails retired = ProjectLesson("Already retired", Now);
        retired.Status = LearningStatus.Retired;

        InjectedLessons section = LessonInjection.Compose(
            [retired, ProjectLesson("Still live", Now.AddMinutes(-1))], [], ThisNode, LessonInjectionCaps.Default);

        section.Lessons.Should().HaveCount(1);
        section.ActiveInScope.Should().Be(1, "a retired lesson is not part of the active inventory at all");
        section.HeldForCap.Should().Be(0);
        section.HeldForProvenance.Should().Be(0);
    }

    [Fact]
    public void A_multi_line_statement_collapses_onto_one_line_so_the_bullet_stays_one_bullet()
    {
        InjectedLessons section = LessonInjection.Compose(
            [ProjectLesson("First half\nsecond half", Now)], [], ThisNode, LessonInjectionCaps.Default);

        section.Lessons[0].Statement.Should().Be("First half second half");
        section.Lessons[0].Line.Should().NotContain("\n");
    }

    [Fact]
    public void The_count_cap_keeps_the_newest_and_counts_the_rest_as_held_back()
    {
        LearningDetails[] lessons = [.. Enumerable.Range(0, 10)
            .Select(index => ProjectLesson($"Lesson {index}", Now.AddMinutes(-index)))];

        InjectedLessons section = LessonInjection.Compose(lessons, [], ThisNode, new LessonInjectionCaps(3, 10_000));

        section.Lessons.Select(lesson => lesson.Statement).Should().Equal("Lesson 0", "Lesson 1", "Lesson 2");
        section.ActiveInScope.Should().Be(10);
        section.HeldForCap.Should().Be(7);
        section.TruncatedByCap.Should().BeTrue();
    }

    [Fact]
    public void The_character_cap_holds_a_lesson_back_whole_rather_than_cutting_it_mid_claim()
    {
        LearningDetails first = ProjectLesson(new string('a', 200), Now);
        LearningDetails second = ProjectLesson(new string('b', 200), Now.AddMinutes(-1));
        // The budget is the rendered length of exactly one of these, learned rather than guessed:
        // the line carries the id prefix and the scope and provenance suffix as well as the claim,
        // and a hand-computed figure here would be a second copy of the line format.
        int oneLine = LessonInjection
            .Compose([first], [], ThisNode, LessonInjectionCaps.Default).Lessons[0].Line.Length;

        InjectedLessons section = LessonInjection.Compose(
            [first, second], [], ThisNode, new LessonInjectionCaps(50, oneLine + 1));

        section.Lessons.Should().HaveCount(1, "the second lesson needs another whole line's worth and cannot have it");
        section.Lessons[0].Statement.Should().Be(new string('a', 200), "no lesson is ever truncated part-way");
        section.HeldForCap.Should().Be(1);
    }

    /// <summary>
    /// A character budget smaller than a single lesson's own line yields nothing shown, and says
    /// so rather than going quiet: the section then reads as an announced absence, which is the
    /// one thing a bounded feed must never get wrong.
    /// </summary>
    [Fact]
    public void A_character_cap_below_one_whole_lesson_shows_nothing_and_announces_it()
    {
        InjectedLessons section = LessonInjection.Compose(
            [ProjectLesson("A claim too long for this budget", Now)], [], ThisNode, new LessonInjectionCaps(15, 1));

        section.Lessons.Should().BeEmpty();
        section.HeldForCap.Should().Be(1);
        section.TruncatedByCap.Should().BeTrue();
        section.WorthComposing.Should().BeTrue();
    }

    /// <summary>
    /// The caps are applied only to what the provenance rule already let through. If they ran
    /// first, a run of another node's lessons at the head of the list would consume the budget and
    /// starve the section of the lessons that actually reach a prompt.
    /// </summary>
    [Fact]
    public void Another_nodes_lessons_never_consume_the_cap_budget()
    {
        LearningDetails[] foreign = [.. Enumerable.Range(0, 5)
            .Select(index => AgentLesson($"Foreign {index}", Now.AddMinutes(-index), AnotherNode))];
        LearningDetails mine = AgentLesson("Mine", Now.AddHours(-1), ThisNode);

        InjectedLessons section = LessonInjection.Compose(
            [.. foreign, mine], [], ThisNode, new LessonInjectionCaps(2, 10_000));

        section.Lessons.Select(lesson => lesson.Statement).Should().Equal("Mine");
        section.HeldForProvenance.Should().Be(5);
        section.HeldForCap.Should().Be(0);
    }

    [Fact]
    public void Every_active_lesson_is_accounted_for_under_exactly_one_reason()
    {
        LearningDetails[] lessons =
        [
            AgentLesson("Mine one", Now, ThisNode),
            AgentLesson("Mine two", Now.AddMinutes(-1), ThisNode),
            AgentLesson("Foreign", Now.AddMinutes(-2), AnotherNode),
            ProjectLesson("Recorded with no run named", Now.AddMinutes(-3)),
        ];

        InjectedLessons section = LessonInjection.Compose(lessons, [], ThisNode, new LessonInjectionCaps(2, 10_000));

        (section.Lessons.Count + section.HeldForProvenance + section.HeldForCap).Should()
            .Be(section.ActiveInScope, "a section that loses count of what it left out is a silent truncation");
        section.EligibleForPrompt.Should().Be(
            section.Lessons.Count + section.HeldForCap,
            "the truncation sentence reconciles against what the provenance rule let through, not the inventory");
    }

    /// <summary>
    /// Three different claims share the provenance hold, so the hold is counted under each of
    /// them rather than reported as a total the section then has to attribute to something. A
    /// total attributed to one mark is a claim about lessons nobody observed the provenance of
    /// (AGENTS.md, never guess at unobserved facts; cycle-1 pre-PR review, both lenses).
    /// </summary>
    [Fact]
    public void Each_held_back_lesson_is_counted_under_the_mark_that_actually_held_it()
    {
        LearningDetails noProvenance = ProjectLesson("No provenance at all", Now.AddMinutes(-3));
        noProvenance.Provenance = null;

        InjectedLessons section = LessonInjection.Compose(
            [
                AgentLesson("Theirs one", Now, AnotherNode),
                AgentLesson("Theirs two", Now.AddMinutes(-1), AnotherNode),
                AgentLesson("Node nobody recorded", Now.AddMinutes(-2), Guid.Empty),
                noProvenance,
            ],
            [],
            ThisNode,
            LessonInjectionCaps.Default);

        section.HeldForProvenanceByMark.Should().Equal(
            new HeldLessonCount(LessonProvenanceMark.AgentOnAnotherNode, 2),
            new HeldLessonCount(LessonProvenanceMark.AgentOnUnobservedNode, 1),
            new HeldLessonCount(LessonProvenanceMark.Unknown, 1));
        section.HeldForProvenance.Should().Be(4);
    }

    /// <summary>
    /// The held-back breakdown is derived from <see cref="LessonProvenanceMark.All"/>, so a mark
    /// this platform ships but left off that list would be counted by the composer and then go
    /// unnamed by the section reporting it, silently undercounting the hold. Discovered by
    /// reflection rather than listed here, which is the only way this catches the omission it
    /// exists for.
    /// </summary>
    [Fact]
    public void Every_mark_this_platform_ships_is_in_the_vocabulary_the_breakdown_is_derived_from()
    {
        LessonProvenanceMark[] declared = [.. typeof(LessonProvenanceMark)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(LessonProvenanceMark))
            .Select(field => (LessonProvenanceMark)field.GetValue(null)!)];

        declared.Should().NotBeEmpty("the discovery scan should find this vocabulary's own marks");
        LessonProvenanceMark.All.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void A_mark_that_held_nothing_back_is_not_named_at_all()
    {
        InjectedLessons section = LessonInjection.Compose(
            [AgentLesson("Theirs", Now, AnotherNode)], [], ThisNode, LessonInjectionCaps.Default);

        section.HeldForProvenanceByMark.Should().Equal(
            new HeldLessonCount(LessonProvenanceMark.AgentOnAnotherNode, 1));
    }

    [Fact]
    public void A_project_with_nothing_recorded_composes_no_section_at_all()
    {
        InjectedLessons section = LessonInjection.Compose([], [], ThisNode, LessonInjectionCaps.Default);

        section.Any.Should().BeFalse();
        section.WorthComposing.Should().BeFalse("there is no absence to explain");
    }

    /// <summary>
    /// The case a silent omission would get wrong: nothing to show, but something held back. A
    /// session handed nothing and told nothing concludes the project has learned nothing and stops
    /// looking.
    /// </summary>
    [Fact]
    public void Nothing_shown_but_something_held_back_still_composes_a_section()
    {
        InjectedLessons section = LessonInjection.Compose(
            [AgentLesson("Foreign", Now, AnotherNode)], [], ThisNode, LessonInjectionCaps.Default);

        section.Any.Should().BeFalse();
        section.WorthComposing.Should().BeTrue();
        section.HeldForProvenance.Should().Be(1);
    }

    [Theory]
    [InlineData(null, null, LessonInjectionCaps.DefaultMaxLessons, LessonInjectionCaps.DefaultMaxCharacters)]
    [InlineData(5, 500, 5, 500)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-4, -9, 1, 1)]
    [InlineData(100_000, 10_000_000, LessonInjectionCaps.MaxConfigurableLessons, LessonInjectionCaps.MaxConfigurableCharacters)]
    public void Configured_caps_are_clamped_rather_than_refused_because_this_runs_on_the_dispatch_path(
        int? configuredLessons, int? configuredCharacters, int expectedLessons, int expectedCharacters)
    {
        LessonInjectionCaps caps = LessonInjectionCaps.Resolve(configuredLessons, configuredCharacters);

        caps.MaxLessons.Should().Be(expectedLessons);
        caps.MaxCharacters.Should().Be(expectedCharacters);
    }

    /// <summary>
    /// The mark for a run-less lesson says only that no run was named, and never that a person
    /// recorded it: an agent that skips <c>--task</c> writes the identical provenance, so the
    /// label has to stop at what the event shows (independent pre-PR review, cycle 3, adversarial
    /// lens).
    /// </summary>
    [Fact]
    public void A_lesson_naming_no_run_says_so_and_claims_no_person_whichever_node_holds_it()
    {
        LessonProvenanceMark.Of(RecordedProvenance.FromShell(Owner), AnotherNode, ThisNode)
            .Should().Be(LessonProvenanceMark.NoRunNamed);
        LessonProvenanceMark.NoRunNamed.Label.Should().Be("recorded with no run named");
        LessonProvenanceMark.NoRunNamed.ReachesAPrompt.Should()
            .BeTrue("it is the only mark a lesson a person typed can carry");
    }

    [Fact]
    public void A_lesson_from_a_run_is_marked_by_the_node_that_recorded_it()
    {
        RecordedProvenance fromARun = new(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended);

        LessonProvenanceMark.Of(fromARun, ThisNode, ThisNode).Should().Be(LessonProvenanceMark.AgentOnThisNode);
        LessonProvenanceMark.Of(fromARun, AnotherNode, ThisNode).Should().Be(LessonProvenanceMark.AgentOnAnotherNode);
        LessonProvenanceMark.AgentOnThisNode.ReachesAPrompt.Should().BeTrue();
        LessonProvenanceMark.AgentOnAnotherNode.ReachesAPrompt.Should()
            .BeFalse("held out of prompts until idea 7e403b80's security review rules on it");
    }

    /// <summary>
    /// An attended run is still an agent's own recording. The platform can see that a human was at
    /// the session; it cannot see that the human wrote the sentence, and marking it Human would be
    /// exactly the unobserved-fact guess AGENTS.md forbids.
    /// </summary>
    [Fact]
    public void An_attended_run_on_another_node_is_still_that_nodes_agent()
    {
        RecordedProvenance attended = new(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Attended);

        LessonProvenanceMark.Of(attended, AnotherNode, ThisNode).Should().Be(LessonProvenanceMark.AgentOnAnotherNode);
    }

    [Fact]
    public void An_unreadable_recording_node_is_never_read_as_this_node()
    {
        RecordedProvenance fromARun = new(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended);

        LessonProvenanceMark.Of(fromARun, recordedOnNodeId: null, ThisNode).Should()
            .Be(LessonProvenanceMark.AgentOnUnobservedNode);
        LessonProvenanceMark.Of(fromARun, Guid.Empty, ThisNode).Should()
            .Be(LessonProvenanceMark.AgentOnUnobservedNode);
        LessonProvenanceMark.AgentOnUnobservedNode.ReachesAPrompt.Should().BeFalse();
    }

    /// <summary>
    /// A node that cannot name itself cannot claim a lesson as its own. This is reachable: a fresh
    /// install composes a prompt before its own NodeDetails row is queryable.
    /// </summary>
    [Fact]
    public void A_node_with_no_identity_of_its_own_claims_nothing()
    {
        RecordedProvenance fromARun = new(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended);

        LessonProvenanceMark.Of(fromARun, ThisNode, thisNodeId: Guid.Empty).Should()
            .Be(LessonProvenanceMark.AgentOnUnobservedNode);
    }

    [Fact]
    public void A_lesson_with_no_provenance_at_all_is_unknown_and_reaches_no_prompt()
    {
        LessonProvenanceMark.Of(provenance: null, ThisNode, ThisNode).Should().Be(LessonProvenanceMark.Unknown);
        LessonProvenanceMark.Unknown.ReachesAPrompt.Should().BeFalse();
    }

    private static LearningDetails ProjectLesson(string statement, DateTimeOffset recordedAt) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(Owner),
        RecordedAt = recordedAt,
        Status = LearningStatus.Active,
    };

    private static LearningDetails OwnerLesson(string statement, DateTimeOffset recordedAt) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Owner,
        ScopeId = Owner,
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
