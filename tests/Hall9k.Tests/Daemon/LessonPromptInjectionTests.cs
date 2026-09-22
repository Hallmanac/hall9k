using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The lesson section as the four prompt families actually carry it (idea d805fd8b, piece 5;
/// backlog 55): implementation, follow-up, review and fix. Driven through the real builders rather
/// than through the composer alone, because the criterion is about what a dispatched session is
/// handed, and a section the composer produced but no builder appended would satisfy the composer's
/// own tests and reach nobody.
/// </summary>
public sealed class LessonPromptInjectionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid AnotherNode = DomainId.New();

    private readonly string _worktreePath =
        Path.Combine(Path.GetTempPath(), $"hall9k-lessons-{Guid.NewGuid():N}");

    public LessonPromptInjectionTests() => Directory.CreateDirectory(_worktreePath);

    public void Dispose() => Directory.Delete(_worktreePath, recursive: true);

    /// <summary>
    /// Every prompt a session gets for building, following up, reviewing or fixing on this project.
    /// Named per family rather than asserted once, so a builder that stops appending the section is
    /// a failure that names which one.
    /// <para>
    /// Only the two coverage theories below run across all of these. What the section SAYS is
    /// composed once, by <see cref="WorkPromptBuilder.AppendRecordedLessons"/>, which every family
    /// reaches through; running the wording assertions per family re-proved one function's prose
    /// twelve times through a seam that adds nothing once those two have shown each builder calls
    /// it, so they run against one family instead (test-growth check 1, Brian 2026-09-18, raised
    /// by cycle-1's conformance lens).
    /// </para>
    /// </summary>
    public static TheoryData<string> PromptFamilies() =>
        ["implementation", "follow-up", "failing-checks follow-up", "changes-requested follow-up",
         "rebase follow-up", "stack-replay follow-up", "conformance review", "adversarial review",
         "verify review", "review fix", "pre-final-pass rebase", "settling-gate repair"];

    /// <summary>The family the wording assertions drive, since the prose they read is the same in all twelve.</summary>
    private const string OneFamily = "implementation";

    [Theory]
    [MemberData(nameof(PromptFamilies))]
    public void Every_family_carries_the_section_with_each_lesson_prefixed_by_its_id(string family)
    {
        LearningDetails lesson = ShellLesson("On Windows, sed -i strips CRLF from this repo's files");
        InjectedLessons lessons = Compose([lesson], LessonInjectionCaps.Default);

        string prompt = Build(family, lessons);

        prompt.Should().Contain("What this project's earlier runs already learned");
        prompt.Should().Contain($"- [{DomainId.Short(lesson.Id)}] On Windows, sed -i strips CRLF");
        prompt.Should().Contain("h9k learn retire <id> --reason",
            "a session that finds a lesson wrong needs the verb, not just the claim");
    }

    /// <summary>
    /// The recording verb names this task, so a dispatched session's own lesson carries its run
    /// as provenance. Handed the bare verb, it recorded the identical provenance a person typing
    /// at a shell does, since no dispatched session carries a run-naming environment variable:
    /// the claim was then labelled as having named no run and rode into every later prompt on the
    /// same terms as a human's (independent pre-PR review, cycle 3, adversarial lens).
    /// </summary>
    [Fact]
    public void The_recording_verb_names_this_task_so_the_lesson_it_writes_carries_its_own_run()
    {
        InjectedLessons lessons = Compose([ShellLesson("One claim")], LessonInjectionCaps.Default);

        string prompt = Build(OneFamily, lessons);

        prompt.Should().Contain($"h9k learn \"<what you learned>\" --task {TheTask.Id}");
    }

    [Theory]
    [MemberData(nameof(PromptFamilies))]
    public void No_family_grows_a_section_when_the_caller_has_no_lessons_to_hand_it(string family)
    {
        string prompt = Build(family, lessons: null);

        prompt.Should().NotContain("What this project's earlier runs already learned");
    }

    /// <summary>
    /// Truncation is announced, and the announcement names the counts, both caps, and the command
    /// that shows the rest. A capped feed that goes quiet teaches a session that what it was handed
    /// is everything there is, which is worse than handing it nothing.
    /// </summary>
    [Fact]
    public void Truncation_is_announced_rather_than_silent_and_names_h9k_learn_list()
    {
        LearningDetails[] inventory = [.. Enumerable.Range(0, 9)
            .Select(index => ShellLesson($"Lesson {index}", Now.AddMinutes(-index)))];
        InjectedLessons lessons = Compose(inventory, new LessonInjectionCaps(4, 10_000));

        string prompt = Build(OneFamily, lessons);

        prompt.Should().Contain("That is 4 of the 9 lessons eligible for this prompt, and 5 did not fit.");
        prompt.Should().Contain("4 lessons and 10000 characters", "the section names both caps it was held to");
        prompt.Should().Contain("h9k learn list");
        prompt.Should().Contain("- [" + DomainId.Short(inventory[0].Id) + "]", "the newest survive the cap");
        prompt.Should().NotContain("- [" + DomainId.Short(inventory[8].Id) + "]");
        prompt.Should().NotContain("held back on provenance rather than by either cap",
            "the provenance rule held nothing back here, so the section claims no hold that never happened");
    }

    /// <summary>
    /// The three numbers a truncated section quotes have to add up, and the one they add up to is
    /// the eligible set rather than the active inventory: a reader who totals shown plus did-not-fit
    /// against the active count would otherwise find lessons unaccounted for in that sentence
    /// (cycle-1 pre-PR review, adversarial lens).
    /// </summary>
    [Fact]
    public void A_truncated_section_reconciles_its_own_numbers_when_provenance_held_some_back_too()
    {
        LearningDetails[] eligible = [.. Enumerable.Range(0, 4)
            .Select(index => ShellLesson($"Mine {index}", Now.AddMinutes(-index)))];
        LearningDetails[] foreign = [.. Enumerable.Range(0, 3)
            .Select(index => AgentLesson($"Theirs {index}", AnotherNode))];
        InjectedLessons lessons = Compose([.. eligible, .. foreign], new LessonInjectionCaps(2, 10_000));

        string prompt = Build(OneFamily, lessons);

        prompt.Should().Contain("That is 2 of the 4 lessons eligible for this prompt, and 2 did not fit.");
        prompt.Should().Contain("7 lessons are live across this project and its owner");
        prompt.Should().Contain("between that and the 4 eligible was held back on provenance rather than by either cap.");
    }

    [Fact]
    public void An_untruncated_section_says_nothing_about_caps_at_all()
    {
        InjectedLessons lessons = Compose([ShellLesson("One claim")], LessonInjectionCaps.Default);

        string prompt = Build(OneFamily, lessons);

        prompt.Should().NotContain("did not fit");
        prompt.Should().NotContain("Held out of this section on provenance");
    }

    /// <summary>
    /// The light security pass Brian asked for while the distributed-team functionality is built:
    /// another node's agent-recorded lesson is rendered for a reader and never written into a
    /// session's instructions, pending idea 7e403b80's in-depth review.
    /// </summary>
    [Fact]
    public void Another_nodes_agent_recorded_lesson_never_reaches_a_prompt_and_the_hold_is_stated()
    {
        LearningDetails foreign = AgentLesson("Something an agent elsewhere concluded", AnotherNode);
        LearningDetails mine = AgentLesson("Something this node's own run learned", ThisNode);
        InjectedLessons lessons = Compose([foreign, mine], LessonInjectionCaps.Default);

        string prompt = Build(OneFamily, lessons);

        prompt.Should().NotContain("Something an agent elsewhere concluded");
        prompt.Should().Contain("Something this node's own run learned");
        prompt.Should().Contain(
            "Held out of this section on provenance: one lesson recorded by an agent run on another node.");
        prompt.Should().Contain("7e403b80", "the hold names the review that will settle it");
    }

    /// <summary>
    /// Three different claims share the provenance hold, and a section that reported the total
    /// under one of them would be telling a session something nobody observed — the guess
    /// AGENTS.md forbids outright (cycle-1 pre-PR review, both lenses).
    /// </summary>
    [Fact]
    public void The_hold_is_counted_under_each_reason_rather_than_all_under_another_node()
    {
        LearningDetails[] inventory =
        [
            AgentLesson("Theirs", AnotherNode),
            AgentLesson("Node nobody recorded", recordedOnNodeId: null),
            NoProvenanceLesson("No provenance at all"),
        ];
        InjectedLessons lessons = Compose(inventory, LessonInjectionCaps.Default);

        string prompt = Build(OneFamily, lessons);

        prompt.Should().Contain(
            "Held out of this section on provenance: 3 lessons: 1 recorded by an agent run on another "
            + "node; 1 recorded by an agent run on a node nobody recorded; 1 recorded with no provenance "
            + "at all.",
            "the breakdown names all three reasons, in the vocabulary's own order");
    }

    [Fact]
    public void A_section_holding_everything_back_still_says_so_rather_than_reading_as_an_empty_store()
    {
        InjectedLessons lessons = Compose(
            [AgentLesson("Only another node's", AnotherNode)], LessonInjectionCaps.Default);

        string prompt = Build(OneFamily, lessons);

        prompt.Should().Contain("What this project's earlier runs already learned");
        prompt.Should().Contain("do not read the absence as");
        prompt.Should().Contain("Held out of this section on provenance: one lesson");
    }

    [Fact]
    public void Each_line_says_how_far_its_lesson_travels_and_who_recorded_it()
    {
        InjectedLessons lessons = LessonInjection.Compose(
            [AgentLesson("A claim about this codebase", ThisNode)],
            [OwnerLesson("A habit of mine")],
            ThisNode,
            LessonInjectionCaps.Default);

        string prompt = Build(OneFamily, lessons);

        prompt.Should().Contain("A claim about this codebase (this project; recorded by an agent run on this node)");
        prompt.Should().Contain("A habit of mine (yours across every project; recorded with no run named)");
    }

    /// <summary>
    /// The adversarial lens is blind to the task's objective and acceptance criteria, and a
    /// recorded lesson is neither: it is what earlier runs learned about this codebase and this
    /// machinery, which is the surrounding knowledge a defect hunt needs to recognise a defect. The
    /// same reasoning the settled park rulings already ride into both lenses on.
    /// </summary>
    [Fact]
    public void The_adversarial_lens_gets_the_lessons_without_getting_the_objective()
    {
        InjectedLessons lessons = Compose([ShellLesson("A claim earlier runs established")], LessonInjectionCaps.Default);

        string prompt = Build("adversarial review", lessons);

        prompt.Should().Contain("A claim earlier runs established");
        prompt.Should().NotContain(SomeTask().Objective, "withholding the intent is this lens's whole mechanism");
    }

    private static InjectedLessons Compose(IReadOnlyList<LearningDetails> projectLessons, LessonInjectionCaps caps) =>
        LessonInjection.Compose(projectLessons, [], ThisNode, caps);

    private string Build(string family, InjectedLessons? lessons) => family switch
    {
        "implementation" => AgentPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, lessons: lessons),
        "follow-up" => AgentPromptBuilder.BuildFollowUp(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative,
            lessons: lessons),
        "failing-checks follow-up" => AgentPromptBuilder.BuildFixChecks(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative,
            lessons: lessons),
        "changes-requested follow-up" => AgentPromptBuilder.BuildReviewRequestedChanges(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative,
            lessons: lessons),
        // The other two arms of RunLauncher's own follow-up switch. Named here rather than left to
        // the three above, because "the fix landed on one of two arms that needed it" is the class
        // that has cost this project two full review laps in one afternoon.
        "rebase follow-up" => AgentPromptBuilder.BuildRebase(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative,
            lessons: lessons),
        "stack-replay follow-up" => AgentPromptBuilder.BuildStackReplay(
            SomeTask(), SomeProject(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative,
            "main", "abc1234", "def5678", lessons: lessons),
        "conformance review" => AgentPromptBuilder.BuildReview(
            SomeTask(), SomeProject(), "task/1-slug", cycle: 1, ReviewLens.Conformance, lessons: lessons),
        "adversarial review" => AgentPromptBuilder.BuildReview(
            SomeTask(), SomeProject(), "task/1-slug", cycle: 1, ReviewLens.Adversarial, lessons: lessons),
        "verify review" => AgentPromptBuilder.BuildReviewVerify(
            SomeTask(), SomeProject(), "task/1-slug", cycle: 2, ReviewLens.CycleLenses,
            "prior findings", "prior fix position", sinceSha: null, ReviewMode.Discovery,
            priorCycleSinceSha: null, lessons: lessons),
        "review fix" => AgentPromptBuilder.BuildReviewFix(
            SomeTask(), SomeProject(), "task/1-slug", "findings", cycle: 1, lessons: lessons),
        // The two repair laps ReviewEngine dispatches in the fix role. Fix-shaped sessions on this
        // task's own branch, so they belong to the same family as the review fix above.
        "pre-final-pass rebase" => AgentPromptBuilder.BuildPreFinalPassRebase(
            SomeTask(), SomeProject(), "task/1-slug", CommitStyle.Narrative,
            "https://github.test/pull/1", lessons: lessons),
        "settling-gate repair" => AgentPromptBuilder.BuildSettlingGateRepair(
            SomeTask(), SomeProject(), "task/1-slug", CommitStyle.Narrative, "https://github.test/pull/1",
            "main", "abc1234", "def5678", rebaseWasRecovered: false, "the gate output", lessons: lessons),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown prompt family"),
    };

    /// <summary>
    /// One task for the whole class rather than a fresh one per call, so a test can assert on the
    /// id the section's own recording verb names. Every builder here reads it and none mutates it.
    /// </summary>
    private static readonly TaskDetails TheTask = new()
    {
        Id = DomainId.New(),
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static TaskDetails SomeTask() => TheTask;

    private static ProjectDetails SomeProject() => new()
    {
        Id = Project,
        Name = "hall9k",
        BaseBranch = "main",
    };

    private static LearningDetails ShellLesson(string statement, DateTimeOffset? recordedAt = null) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(Owner),
        RecordedAt = recordedAt ?? Now,
        Status = LearningStatus.Active,
    };

    private static LearningDetails OwnerLesson(string statement) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Owner,
        ScopeId = Owner,
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(Owner),
        RecordedAt = Now.AddMinutes(-1),
        Status = LearningStatus.Active,
    };

    private static LearningDetails AgentLesson(string statement, Guid? recordedOnNodeId) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = new RecordedProvenance(Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended),
        RecordedAt = Now,
        RecordedOnNodeId = recordedOnNodeId,
        Status = LearningStatus.Active,
    };

    /// <summary>A row whose stream carries no provenance to read at all, which is the third of the three held marks.</summary>
    private static LearningDetails NoProvenanceLesson(string statement) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = statement,
        Provenance = null,
        RecordedAt = Now,
        Status = LearningStatus.Active,
    };
}
