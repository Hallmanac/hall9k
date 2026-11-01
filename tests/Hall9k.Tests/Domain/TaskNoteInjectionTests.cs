using FluentAssertions;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The pure half of task notes: which notes a prompt's section carries, in what order, under which
/// caps, and which the trust rule holds out. No store: the selection is a function of the entries and
/// the reading node's identity, as <see cref="LessonInjection"/> is.
/// </summary>
public sealed class TaskNoteInjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid TheTask = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid FleetNode = DomainId.New();
    private static readonly Guid OtherNode = DomainId.New();

    [Fact]
    public void A_tasks_own_notes_come_first_newest_first_then_its_siblings_labelled_with_their_short_id()
    {
        Guid sibling = DomainId.New();
        TaskNoteEntry ownOld = Entry("own old", Now.AddMinutes(-30));
        TaskNoteEntry ownNew = Entry("own new", Now.AddMinutes(-10));
        TaskNoteEntry siblingNew = Entry("sibling new", Now.AddMinutes(-1));
        TaskNoteEntry siblingOld = Entry("sibling old", Now.AddMinutes(-60));

        IReadOnlyList<TaskNoteView> views = TaskNoteInjection.Classify(
            TheTask, [ownOld, ownNew], [(sibling, siblingOld), (sibling, siblingNew)], ThisNode, localFleet: null);
        InjectedTaskNotes section = TaskNoteInjection.Select(views);

        section.Notes.Select(note => note.Text).Should().Equal("own new", "own old", "sibling new", "sibling old");
        section.Notes[0].Line.Should().StartWith("- this task: own new");
        section.Notes[2].Line.Should().StartWith($"- task {DomainId.Short(sibling)}: sibling new");
        section.LeftOut.Should().Be(0);
    }

    [Fact]
    public void The_section_stops_at_fifteen_notes_and_counts_what_it_left_out()
    {
        TaskNoteEntry[] notes = [.. Enumerable.Range(0, 20).Select(index => Entry($"n{index}", Now.AddMinutes(-index)))];

        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, notes, [], ThisNode, null));

        section.Notes.Should().HaveCount(15);
        section.LeftOut.Should().Be(5);
        section.Notes[0].Text.Should().Be("n0", "the newest survive the cap");
    }

    [Fact]
    public void The_section_stops_at_four_thousand_characters_of_note_text()
    {
        TaskNoteEntry[] notes = [.. Enumerable.Range(0, 6).Select(index => Entry(new string('x', 900), Now.AddMinutes(-index)))];

        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, notes, [], ThisNode, null));

        section.Notes.Should().HaveCount(4, "four 900-character notes fit in 4,000 characters and a fifth does not");
        section.Notes.Sum(note => note.Text.Length).Should().BeLessThanOrEqualTo(TaskNoteLimits.MaxPromptCharacters);
        section.LeftOut.Should().Be(2);
    }

    [Fact]
    public void A_note_is_collapsed_onto_one_line()
    {
        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, [Entry("first\nsecond\r\nthird")], [], ThisNode, null));

        section.Notes.Single().Line.Should().NotContain("\n").And.Contain("first second third");
    }

    [Fact]
    public void A_note_replicated_from_outside_the_fleet_is_held_and_counted_under_the_marks_own_words()
    {
        TaskNoteEntry mine = Entry("mine");
        TaskNoteEntry foreign = Entry("foreign", recordedOn: OtherNode, receivedFrom: OtherNode);
        TaskNoteEntry unobserved = Entry("nobody observed", recordedOnUnobserved: true);

        IReadOnlyList<TaskNoteView> views = TaskNoteInjection.Classify(
            TheTask, [mine, foreign, unobserved], [], ThisNode, new HashSet<Guid> { ThisNode, FleetNode });
        InjectedTaskNotes section = TaskNoteInjection.Select(views);

        section.Notes.Select(note => note.Text).Should().Equal("mine");
        section.HeldForProvenance.Should().Be(2);
        section.HeldByMark.Select(held => held.Mark).Should().Equal(
            LessonProvenanceMark.AgentOnUnobservedNode, LessonProvenanceMark.ReplicatedFromOutsideFleet);
        views.Single(view => view.Entry.Note.Text == "foreign").ReachesAPrompt.Should().BeFalse();
    }

    [Fact]
    public void A_note_from_a_node_in_the_owners_own_fleet_reaches_a_prompt()
    {
        TaskNoteEntry fromFleet = Entry("from my other machine", recordedOn: FleetNode, receivedFrom: FleetNode);

        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, [fromFleet], [], ThisNode, new HashSet<Guid> { ThisNode, FleetNode }));

        section.Notes.Single().Mark.Should().Be(LessonProvenanceMark.AgentOnFleetNode);
    }

    [Fact]
    public void An_unreadable_fleet_holds_every_replicated_note_and_leaves_a_native_one_alone()
    {
        TaskNoteEntry native = Entry("native");
        TaskNoteEntry replicated = Entry("replicated", recordedOn: FleetNode, receivedFrom: FleetNode);

        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, [native, replicated], [], ThisNode, localFleet: null));

        section.Notes.Select(note => note.Text).Should().Equal("native");
        section.HeldByMark.Single().Mark.Should().Be(LessonProvenanceMark.ReplicatedFromOutsideFleet);
    }

    [Fact]
    public void Held_notes_use_none_of_the_budget_so_a_run_of_them_cannot_starve_the_section()
    {
        TaskNoteEntry[] foreign = [.. Enumerable.Range(0, 30)
            .Select(index => Entry("foreign " + index, Now.AddMinutes(index), recordedOn: OtherNode, receivedFrom: OtherNode))];
        TaskNoteEntry mine = Entry("mine", Now.AddMinutes(-90));

        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, [.. foreign, mine], [], ThisNode, new HashSet<Guid> { ThisNode }));

        section.Notes.Select(note => note.Text).Should().Equal("mine");
        section.LeftOut.Should().Be(0);
    }

    [Fact]
    public void A_native_note_with_no_node_recorded_because_this_node_cannot_name_itself_is_held()
    {
        InjectedTaskNotes section = TaskNoteInjection.Select(
            TaskNoteInjection.Classify(TheTask, [Entry("mine")], [], Guid.Empty, null));

        section.Any.Should().BeFalse();
        section.HeldByMark.Single().Mark.Should().Be(LessonProvenanceMark.AgentOnUnobservedNode);
    }

    [Fact]
    public void Blank_text_and_text_over_a_thousand_characters_are_refused_naming_the_limit_and_one_thousand_is_accepted()
    {
        Action blank = () => TaskNoteDecider.Record(TheTask, "   ", Provenance(), null, null, Now);
        Action tooLong = () => TaskNoteDecider.Record(TheTask, new string('a', 1001), Provenance(), null, null, Now);

        blank.Should().Throw<DomainValidationException>();
        tooLong.Should().Throw<DomainValidationException>().WithMessage("*1000*");
        TaskNoteDecider.Record(TheTask, new string('a', 1000), Provenance(), null, null, Now)
            .Text.Should().HaveLength(1000);
    }

    [Fact]
    public void Closing_block_notes_get_the_same_ids_when_the_same_message_is_read_twice()
    {
        Guid run = DomainId.New();

        IReadOnlyList<TaskNoteRecorded> first = TaskNoteDecider.FromClosingBlock(
            TheTask, run, ["one", "two"], Provenance(run), null, null, Now);
        IReadOnlyList<TaskNoteRecorded> second = TaskNoteDecider.FromClosingBlock(
            TheTask, run, ["one", "two"], Provenance(run), null, null, Now.AddMinutes(5));

        first.Select(note => note.NoteId).Should().Equal(second.Select(note => note.NoteId));
        first.Select(note => note.NoteId).Distinct().Should().HaveCount(2);
        first.Should().OnlyContain(note => note.Source == TaskNoteSource.ClosingBlock);
    }

    private static RecordedProvenance Provenance(Guid? run = null) =>
        new(Owner, run ?? DomainId.New(), TheTask, HumanAttendance.Unattended);

    private static TaskNoteEntry Entry(
        string text, DateTimeOffset? at = null, Guid? recordedOn = null, Guid? receivedFrom = null,
        bool recordedOnUnobserved = false)
    {
        // Recorded on this node unless a node is named, or nobody observed one.
        Guid? node = recordedOnUnobserved ? null : recordedOn ?? ThisNode;
        return new TaskNoteEntry(
            new TaskNoteRecorded(
                TheTask, DomainId.New(), text, Provenance(), TaskNoteSource.Verb, at ?? Now),
            node, receivedFrom, receivedFrom);
    }
}
