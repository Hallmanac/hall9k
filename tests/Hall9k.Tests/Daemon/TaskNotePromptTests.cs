using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The notes section as the prompt families actually carry it, driven through the real builders: the
/// eight work prompts that read notes carry it, every prompt outside that list is byte-identical with
/// a note on record, and the wording teaches the verb, the lesson alternative and the permanence.
/// </summary>
public sealed class TaskNotePromptTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ThisNode = DomainId.New();
    private const string NoteText = "The Marten projection rebuilds lazily, so a test must force a rebuild";

    private readonly string _worktreePath =
        Path.Combine(Path.GetTempPath(), $"hall9k-notes-{Guid.NewGuid():N}");

    public TaskNotePromptTests() => Directory.CreateDirectory(_worktreePath);

    public void Dispose() => Directory.Delete(_worktreePath, recursive: true);

    public static TheoryData<string> ReadingFamilies() =>
        ["fresh build", "follow-up", "changes-requested lap", "checks follow-up", "rebase follow-up",
         "pre-final-pass rebase", "settling-gate repair", "review fix"];

    [Theory]
    [MemberData(nameof(ReadingFamilies))]
    public void Every_reading_family_carries_the_section_with_each_note_on_one_line(string family)
    {
        string prompt = BuildReading(family, WithNotes(NoteText));

        prompt.Should().Contain("Notes the sessions on this task left each other");
        prompt.Should().Contain($"- this task: {NoteText} (recorded by an agent run on this node)");
        prompt.Should().Contain("colleague's", "the section says these are notes rather than instructions");
    }

    [Theory]
    [MemberData(nameof(ReadingFamilies))]
    public void Every_reading_family_teaches_the_verb_the_lesson_alternative_and_permanence_with_or_without_notes(string family)
    {
        foreach (InjectedTaskNotes notes in new[] { WithNotes(NoteText), InjectedTaskNotes.None })
        {
            string prompt = BuildReading(family, notes);

            prompt.Should().Contain($"h9k task note {TheTask.Id} \"<text>\"");
            prompt.Should().Contain("`h9k learn` instead");
            prompt.Should().Contain("permanent and public");
            prompt.Should().Contain("secrets, credentials, personal data, customer details, or the name of another project");
        }
    }

    [Theory]
    [MemberData(nameof(ReadingFamilies))]
    public void No_reading_family_grows_a_section_when_the_caller_has_no_notes_to_hand_it(string family)
    {
        BuildReading(family, notes: null).Should().NotContain("Notes the sessions on this task left each other");
    }

    [Theory]
    [InlineData("fresh build", true)]
    [InlineData("follow-up", true)]
    [InlineData("review fix", true)]
    [InlineData("changes-requested lap", false)]
    [InlineData("checks follow-up", false)]
    [InlineData("rebase follow-up", false)]
    [InlineData("pre-final-pass rebase", false)]
    [InlineData("settling-gate repair", false)]
    public void Only_a_fresh_build_a_follow_up_and_a_review_fix_ask_for_the_closing_block(string family, bool asks)
    {
        string prompt = BuildReading(family, WithNotes(NoteText));

        if (asks)
        {
            prompt.Should().Contain($"    {TaskNotesBlock.Marker}");
        }
        else
        {
            prompt.Should().NotContain(TaskNotesBlock.Marker);
        }
    }

    [Fact]
    public void An_attended_prompt_and_a_delegated_contractors_prompt_do_not_ask_for_the_block()
    {
        string attended = WorkPromptBuilder.Build(
            TheTask, Project(), "task/1-slug", _worktreePath, isInteractive: true, taskNotes: WithNotes(NoteText));
        string contractor = WorkPromptBuilder.Build(
            TheTask, Project(), "task/1-slug", _worktreePath, isDelegatedContractor: true,
            delegationNote: "do the thing", taskNotes: WithNotes(NoteText));

        attended.Should().Contain("Notes the sessions on this task left each other").And.NotContain(TaskNotesBlock.Marker);
        contractor.Should().Contain("Notes the sessions on this task left each other").And.NotContain(TaskNotesBlock.Marker);
        attended.Should().Contain("h9k task note");
        contractor.Should().Contain("h9k task note");
    }

    [Fact]
    public void No_prompt_shows_a_sample_bullet_under_the_marker()
    {
        string prompt = BuildReading("fresh build", WithNotes(NoteText));
        string[] lines = prompt.ReplaceLineEndings("\n").Split('\n');
        int marker = Array.FindIndex(lines, line => line.Trim() == TaskNotesBlock.Marker);

        marker.Should().BeGreaterThan(0);
        lines.Skip(marker + 1).Take(3).Where(line => line.Trim().Length > 0)
            .Should().NotContain(line => line.TrimStart().StartsWith('-') || line.TrimStart().StartsWith('*'),
                "a session that quotes its instructions must record nothing");
    }

    [Fact]
    public void A_section_that_left_notes_out_says_how_many_and_that_a_person_can_read_them()
    {
        InjectedTaskNotes notes = new([Note("kept")], LeftOut: 3, []);

        string prompt = BuildReading("fresh build", notes);

        prompt.Should().Contain("3 more notes did not fit");
        prompt.Should().Contain($"h9k task notes {TheTask.Id}");
    }

    [Fact]
    public void A_section_that_held_notes_counts_them_in_the_marks_own_words()
    {
        InjectedTaskNotes notes = new(
            [Note("kept")], 0,
            [new HeldLessonCount(LessonProvenanceMark.ReplicatedFromOutsideFleet, 2),
             new HeldLessonCount(LessonProvenanceMark.AgentOnUnobservedNode, 1)]);

        string prompt = BuildReading("fresh build", notes);

        prompt.Should().Contain(
            "Held out of this section on provenance: 3 notes: 2 replicated from a node outside this owner's fleet; "
            + "1 recorded by an agent run on a node nobody recorded.");
    }

    [Fact]
    public void A_task_with_no_notes_says_so_rather_than_composing_an_empty_list()
    {
        string prompt = BuildReading("fresh build", InjectedTaskNotes.None);

        prompt.Should().Contain("No notes are recorded on this task yet");
    }

    [Fact]
    public void A_control_character_in_a_note_never_reaches_the_prompt()
    {
        string prompt = BuildReading("fresh build", WithNotes("clear\u001b[2J the screen‮"));

        prompt.Should().NotContain("\u001b").And.NotContain("‮");
    }

    /// <summary>
    /// The reviewing and gating prompts are composed with a note on record on the task, and none of
    /// them is different for it. They have no notes parameter at all, so this guards the other way a
    /// note could leak: a builder that starts reading the task's own fields.
    /// </summary>
    [Fact]
    public void Every_prompt_outside_the_reading_list_is_byte_identical_with_a_note_on_record()
    {
        TaskDetails plain = Task();
        TaskDetails noted = Task();
        noted.Id = plain.Id;
        noted.Notes.Add(new TaskNoteEntry(
            new TaskNoteRecorded(
                noted.Id, DomainId.New(), NoteText,
                new RecordedProvenance(DomainId.New(), DomainId.New(), noted.Id, HumanAttendance.Unattended),
                TaskNoteSource.Verb, Now),
            ThisNode, null, null));

        foreach ((string name, Func<TaskDetails, string> compose) in OutsideTheList())
        {
            string without = compose(plain);
            string with = compose(noted);
            with.Should().Be(without, $"the {name} prompt must not change when its task holds a note");
            with.Should().NotContain(NoteText, $"the {name} prompt must not carry note text");
            with.Should().NotContain("h9k task note", $"the {name} prompt must not teach the verb");
        }
    }

    private IEnumerable<(string Name, Func<TaskDetails, string> Compose)> OutsideTheList()
    {
        ProjectDetails project = Project();
        yield return ("conformance review", task => AgentPromptBuilder.BuildReview(
            task, project, "task/1-slug", cycle: 1, ReviewLens.Conformance));
        yield return ("adversarial review", task => AgentPromptBuilder.BuildReview(
            task, project, "task/1-slug", cycle: 1, ReviewLens.Adversarial));
        yield return ("final-pass review", task => AgentPromptBuilder.BuildReview(
            task, project, "task/1-slug", cycle: 2, ReviewLens.Conformance, ReviewMode.FinalFullPass));
        yield return ("verify review", task => AgentPromptBuilder.BuildReviewVerify(
            task, project, "task/1-slug", cycle: 2, ReviewLens.CycleLenses, "prior findings", "prior fix position",
            sinceSha: null, ReviewMode.Discovery, priorCycleSinceSha: null));
        yield return ("verdict reprompt", _ => AgentPromptBuilder.BuildReviewVerdictReprompt(project, cycle: 1));
        yield return ("pr-review conformance", task => AgentPromptBuilder.BuildPrReviewLens(
            task, project, "task/1-slug", ReviewLens.Conformance, "main"));
        yield return ("pr-review adversarial", task => AgentPromptBuilder.BuildPrReviewLens(
            task, project, "task/1-slug", ReviewLens.Adversarial, "main"));
        yield return ("stack replay", task => AgentPromptBuilder.BuildStackReplay(
            task, project, "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative, "main", "abc1234", "def5678"));
        yield return ("budget retry", AgentPromptBuilder.BuildBudgetRetry);
        yield return ("session error retry", AgentPromptBuilder.BuildSessionErrorRetry);
    }

    private string BuildReading(string family, InjectedTaskNotes? notes) => family switch
    {
        "fresh build" => AgentPromptBuilder.Build(
            TheTask, Project(), "task/1-slug", _worktreePath, taskNotes: notes),
        "follow-up" => AgentPromptBuilder.BuildFollowUp(
            TheTask, Project(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative, taskNotes: notes),
        "changes-requested lap" => AgentPromptBuilder.BuildReviewRequestedChanges(
            TheTask, Project(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative, taskNotes: notes),
        "checks follow-up" => AgentPromptBuilder.BuildFixChecks(
            TheTask, Project(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative, taskNotes: notes),
        "rebase follow-up" => AgentPromptBuilder.BuildRebase(
            TheTask, Project(), "task/1-slug", "https://github.test/pull/1", CommitStyle.Narrative, taskNotes: notes),
        "pre-final-pass rebase" => AgentPromptBuilder.BuildPreFinalPassRebase(
            TheTask, Project(), "task/1-slug", CommitStyle.Narrative, "https://github.test/pull/1", taskNotes: notes),
        "settling-gate repair" => AgentPromptBuilder.BuildSettlingGateRepair(
            TheTask, Project(), "task/1-slug", CommitStyle.Narrative, "https://github.test/pull/1",
            "main", "abc1234", "def5678", rebaseWasRecovered: false, "the gate output", taskNotes: notes),
        "review fix" => AgentPromptBuilder.BuildReviewFix(
            TheTask, Project(), "task/1-slug", "findings", cycle: 1, taskNotes: notes),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unknown prompt family"),
    };

    private static InjectedTaskNote Note(string text) =>
        new(TheTask.Id, IsOwn: true, text, LessonProvenanceMark.AgentOnThisNode);

    private static InjectedTaskNotes WithNotes(string text) => new([Note(text)], 0, []);

    private static readonly TaskDetails TheTask = Task();

    private static TaskDetails Task() => new()
    {
        Id = DomainId.New(),
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static ProjectDetails Project() => new()
    {
        Id = DomainId.New(),
        Name = "hall9k",
        BaseBranch = "main",
    };
}
