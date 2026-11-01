using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The closing block of task notes: what is a block, which bullets become notes, and what the rest of
/// the message looks like once the block is gone. Pure, like <see cref="HandoffParser"/>.
/// </summary>
public sealed class TaskNotesBlockTests
{
    private const string M = TaskNotesBlock.Marker;

    [Fact]
    public void A_message_with_no_block_comes_back_as_the_very_same_string()
    {
        string message = "Did the work.\n\nHANDOFF:\nAll done.";

        TaskNotesBlock.Result result = TaskNotesBlock.Extract(message);

        result.Clean.Should().BeSameAs(message);
        result.Notes.Should().BeEmpty();
        result.HadBlock.Should().BeFalse();
    }

    [Fact]
    public void Each_bullet_under_the_marker_line_is_a_note_and_the_block_is_removed()
    {
        string message = $"Summary.\n\n{M}\n- first note\n* second note\n\nHANDOFF:\nDone.";

        TaskNotesBlock.Result result = TaskNotesBlock.Extract(message);

        result.Notes.Should().Equal("first note", "second note");
        result.Clean.Should().Be("Summary.\n\nHANDOFF:\nDone.");
    }

    [Fact]
    public void Blank_lines_between_the_bullets_belong_to_the_block_and_the_first_other_line_ends_it()
    {
        string message = $"{M}\n\n- one\n\n- two\nNot a bullet\n- three";

        TaskNotesBlock.Result result = TaskNotesBlock.Extract(message);

        result.Notes.Should().Equal("one", "two");
        result.Clean.Should().Be("Not a bullet\n- three", "the line that ended the block stays part of the message");
    }

    [Fact]
    public void At_most_five_notes_are_read()
    {
        string message = $"{M}\n" + string.Join('\n', Enumerable.Range(1, 8).Select(index => $"- n{index}"));

        TaskNotesBlock.Extract(message).Notes.Should().Equal("n1", "n2", "n3", "n4", "n5");
    }

    [Fact]
    public void A_bullet_over_a_thousand_characters_is_cut_and_the_note_says_so()
    {
        string message = $"{M}\n- {new string('a', 1500)}";

        string note = TaskNotesBlock.Extract(message).Notes.Single();

        note.Length.Should().BeLessThanOrEqualTo(1000);
        note.Should().EndWith("[cut: over 1,000 characters]");
    }

    [Fact]
    public void Only_the_last_block_records_but_every_block_is_removed()
    {
        string message = $"{M}\n- quoted\n\nreal answer\n\n{M}\n- actual";

        TaskNotesBlock.Result result = TaskNotesBlock.Extract(message);

        result.Notes.Should().Equal("actual");
        result.Clean.Should().Be("\nreal answer").And.NotContain("quoted").And.NotContain("actual");
    }

    [Fact]
    public void Text_on_the_marker_line_records_nothing()
    {
        TaskNotesBlock.Extract($"{M} remember the rebuild").Notes.Should().BeEmpty();
        TaskNotesBlock.Extract($"{M} remember the rebuild\n- bullet").Notes.Should().Equal("bullet");
    }

    [Fact]
    public void A_marker_with_nothing_under_it_records_nothing_and_is_still_removed()
    {
        TaskNotesBlock.Result result = TaskNotesBlock.Extract($"Say the {M} line.\n{M}\nthen text");

        result.Notes.Should().BeEmpty();
        result.Clean.Should().Be($"Say the {M} line.\nthen text");
    }

    [Theory]
    [InlineData("task notes:")]
    [InlineData("Task Notes:")]
    [InlineData("NOTES:")]
    public void The_marker_is_matched_with_its_case_exact(string marker)
    {
        string message = $"{marker}\n- not a note";

        TaskNotesBlock.Result result = TaskNotesBlock.Extract(message);

        result.Notes.Should().BeEmpty();
        result.Clean.Should().Be(message);
    }

    [Fact]
    public void A_block_after_the_handoff_marker_is_removed_before_the_handoff_is_read()
    {
        string message = $"Summary.\n\n{HandoffParser.Marker}\nDid it.\n\n{M}\n- a note";

        string? handoff = HandoffParser.Parse(TaskNotesBlock.Extract(message).Clean);

        handoff.Should().Be("Did it.");
    }

    [Fact]
    public void Bold_or_prose_lines_that_merely_start_with_a_star_are_not_bullets()
    {
        TaskNotesBlock.Extract($"{M}\n**bold** text\n- real").Notes.Should().BeEmpty();
    }
}
