using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskHandoffCommand.ResolveNoteAsync"/> is the DB-free half of h9k task handoff (idea
/// 202383dc, item 3): which of --text/--file wins, and what the note ends up reading. The store
/// round trip — the holder guard, the event, the ledger record, and the nudge — is this command's
/// own integration-tier concern.
/// </summary>
public sealed class TaskHandoffCommandTests
{
    private static TaskHandoffCommand.Settings Settings(string? text = null, string? file = null) => new()
    {
        Id = "28b19893",
        Text = text,
        File = file,
    };

    [Fact]
    public async Task Refuses_when_neither_text_nor_file_is_given()
    {
        Func<Task> act = () => TaskHandoffCommand.ResolveNoteAsync(Settings(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*--text*--file*");
    }

    [Fact]
    public async Task Refuses_when_both_text_and_file_are_given()
    {
        Func<Task> act = () => TaskHandoffCommand.ResolveNoteAsync(
            Settings(text: "Note", file: "/tmp/note.md"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*--text*--file*");
    }

    [Fact]
    public async Task Refuses_whitespace_only_text_as_neither_text_nor_file()
    {
        // Whitespace-only text reads blank (IsNotBlank), so it never reaches the note itself —
        // it is refused the same way omitting --text altogether is.
        Func<Task> act = () => TaskHandoffCommand.ResolveNoteAsync(Settings(text: "   "), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*--text*--file*");
    }

    [Fact]
    public async Task Refuses_a_blank_file_note()
    {
        string path = Path.Combine(Path.GetTempPath(), $"handoff-note-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "   \n\n  ");
        try
        {
            Func<Task> act = () => TaskHandoffCommand.ResolveNoteAsync(Settings(file: path), CancellationToken.None);

            await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*blank*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Trims_a_text_note()
    {
        string note = await TaskHandoffCommand.ResolveNoteAsync(
            Settings(text: "  Migration script drafted but untested.  "), CancellationToken.None);

        note.Should().Be("Migration script drafted but untested.");
    }

    [Fact]
    public async Task Refuses_a_missing_file_with_a_domain_exception()
    {
        string path = Path.Combine(Path.GetTempPath(), $"handoff-note-{Guid.NewGuid():N}-missing.md");

        Func<Task> act = () => TaskHandoffCommand.ResolveNoteAsync(Settings(file: path), CancellationToken.None);

        await act.Should().ThrowAsync<DomainNotFoundException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task Reads_a_file_note()
    {
        string path = Path.Combine(Path.GetTempPath(), $"handoff-note-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "What is done, what is half done, what to watch.\n");
        try
        {
            string note = await TaskHandoffCommand.ResolveNoteAsync(Settings(file: path), CancellationToken.None);

            note.Should().Be("What is done, what is half done, what to watch.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
