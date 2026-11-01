using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>What <c>h9k task notes</c> prints for a person: ordering, marks, held notes, and safe text.</summary>
public sealed class TaskNotesCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TheTask = DomainId.New();
    private static readonly Guid ThisNode = DomainId.New();
    private static readonly Guid Teammate = DomainId.New();

    [Fact]
    public void Notes_print_newest_first_with_id_time_author_session_source_and_mark_then_the_epics_siblings()
    {
        Guid sibling = DomainId.New();
        TaskNoteEntry older = Entry("older", Now.AddHours(-2), sessionName: "abc12345-build");
        TaskNoteEntry newer = Entry("newer", Now.AddHours(-1));
        TaskNoteEntry other = Entry("on a sibling", Now);
        IReadOnlyList<TaskNoteView> views = TaskNoteInjection.Classify(
            TheTask, [older, newer], [(sibling, other)], ThisNode, null);

        string output = string.Join('\n', TaskNotesCommand.Lines(TheTask, views, localFleet: null));

        output.IndexOf("newer", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("older", StringComparison.Ordinal));
        output.IndexOf("older", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("on a sibling", StringComparison.Ordinal));
        output.Should().Contain(newer.Note.NoteId.ToString());
        output.Should().Contain("session: abc12345-build");
        output.Should().Contain("session: no session");
        output.Should().Contain("source: h9k task note");
        output.Should().Contain("recorded with no run named");
        output.Should().Contain($"task {DomainId.Short(sibling)}");
    }

    [Fact]
    public void A_held_note_is_shown_and_marked_held()
    {
        TaskNoteEntry foreign = Entry("from outside the fleet", Now, receivedFrom: Teammate);
        IReadOnlyList<TaskNoteView> views = TaskNoteInjection.Classify(TheTask, [foreign], [], ThisNode, null);

        string output = string.Join('\n', TaskNotesCommand.Lines(TheTask, views, localFleet: null));

        output.Should().Contain("from outside the fleet").And.Contain("HELD");
        output.Should().Contain("replicated from a node outside this owner's fleet");
    }

    [Fact]
    public void A_replicated_note_names_its_author_the_way_a_replicated_handoff_note_does_not_by_the_unresolvable_owner_id()
    {
        TaskNoteEntry foreign = Entry("theirs", Now, receivedFrom: Teammate);
        IReadOnlyList<TaskNoteView> views = TaskNoteInjection.Classify(TheTask, [foreign], [], ThisNode, null);

        string output = string.Join('\n', TaskNotesCommand.Lines(TheTask, views, localFleet: null));

        output.Should().Contain("node " + DomainId.Short(Teammate));
        output.Should().NotContain("owner " + DomainId.Short(foreign.Note.Provenance.RecordedByOwnerId));
    }

    [Fact]
    public void A_control_character_or_markup_in_a_note_acts_on_no_terminal()
    {
        TaskNoteEntry hostile = Entry("clear\u001b[2J screen [red]markup[/] ‮ reversed", Now);
        IReadOnlyList<TaskNoteView> views = TaskNoteInjection.Classify(TheTask, [hostile], [], ThisNode, null);

        string output = string.Join('\n', TaskNotesCommand.Lines(TheTask, views, null));

        output.Should().NotContain("\u001b").And.NotContain("‮");
        output.Should().Contain("[[red]]markup[[/]]", "markup in a note is escaped, never interpreted");
    }

    [Fact]
    public void A_task_with_no_notes_says_so_and_names_the_verb()
    {
        string output = string.Join('\n', TaskNotesCommand.Lines(TheTask, [], null));

        output.Should().Contain("No notes recorded").And.Contain("h9k task note");
    }

    private static TaskNoteEntry Entry(
        string text, DateTimeOffset at, string? sessionName = null, Guid? receivedFrom = null) =>
        new(
            new TaskNoteRecorded(
                TheTask, DomainId.New(), text, RecordedProvenance.FromShell(DomainId.New()), TaskNoteSource.Verb, at,
                SessionName: sessionName),
            receivedFrom ?? ThisNode, receivedFrom, receivedFrom);
}
