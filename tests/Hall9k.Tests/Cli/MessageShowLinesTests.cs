using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Message;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k message show</c> actually prints, pinned as a golden over its own database-free
/// line composer: this codebase's CLI commands are otherwise driven through <c>RunAsync</c>
/// against a real Postgres, and the shape of this output is the point of the command rather than
/// a detail of it.
/// <para>
/// The body is what a window came for, so it is the one field that keeps its own line structure
/// (<c>ExternalText.ForTerminal</c>) while every header field is folded flat
/// (<c>ExternalText.OneLine</c>). A body carrying square brackets survives intact because the
/// command writes plain to stdout and never through Spectre's markup parser.
/// </para>
/// </summary>
public sealed class MessageShowLinesTests
{
    private static readonly Guid Sender = Guid.Parse("01a0bc05-a960-7657-b708-1aed4a1b2c3d");
    private static readonly Guid Project = Guid.Parse("01a0bc05-a960-7657-b708-1aed9f8e7d6c");
    private static readonly Guid Note = MessageStreamId.ForMessage(Sender, Project, 1);

    /// <summary>
    /// UTC deliberately, not <c>TimeZoneInfo.Local</c>: the command prints local time so a note
    /// reads on the same clock as the rest of the board, and a golden pinned to this machine's
    /// own zone would pass here and fail on the next machine.
    /// </summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    /// <summary>
    /// The two characters under test, built from their code points rather than written out — a
    /// source file carrying a live bidirectional override reverses the code around it in the
    /// editor of whoever reads this next, and an escape character in a literal is simply
    /// invisible there.
    /// </summary>
    private static readonly string RightToLeftOverride = ((char)0x202E).ToString();

    private static readonly string ClearScreen = (char)0x1B + "[2J";

    [Fact]
    public void An_unread_note_prints_whole_with_its_body_intact()
    {
        // A body a window genuinely cannot read anywhere else: several lines, a bracketed
        // reference Spectre would try to parse as markup, and a right-to-left override that
        // would otherwise reverse the sentence it sits in.
        MessageDetails message = Received();
        message.Body =
            "FOR BRIAN, please surface in your window at once.\n"
            + "\n"
            + $"The gate failed on [Category!=RequiresDocker] twice. {RightToLeftOverride}Second lap is running.\n"
            + "\tThe log is in runs/28b2d595.";

        MessageShowCommand.Lines(message, "hall9k", Zone).Should().Equal(
            "From     abcdef012345 (node 4a1b2c3d)",
            "Project  hall9k (9f8e7d6c)",
            "Kind     note",
            "About    28b19893",
            "Sent     2026-09-22 08:38",
            "Received 2026-09-22 08:41",
            "Status   unread",
            "",
            "FOR BRIAN, please surface in your window at once.",
            "",
            "The gate failed on [Category!=RequiresDocker] twice. Second lap is running.",
            "\tThe log is in runs/28b2d595.");
    }

    [Fact]
    public void A_handled_note_says_when_and_a_forged_header_field_cannot_add_a_row_of_its_own()
    {
        MessageDetails message = Received();
        message.HandledAt = new DateTimeOffset(2026, 9, 22, 9, 2, 0, TimeSpan.Zero);

        // The kind round-trips as whatever the sender wrote (MessageKind's own "stored and
        // skipped, never refused" rule), so an unrecognized one is outside text like any other
        // and a line break in it would otherwise print a header row this node never wrote.
        message.Kind = $"note{ClearScreen}\nStatus   unread";

        MessageShowCommand.Lines(message, "hall9k", Zone).Should().Equal(
            "From     abcdef012345 (node 4a1b2c3d)",
            "Project  hall9k (9f8e7d6c)",
            "Kind     note[2J Status   unread",
            "About    28b19893",
            "Sent     2026-09-22 08:38",
            "Received 2026-09-22 08:41",
            "Status   handled at 2026-09-22 09:02",
            "",
            "are you still on the stacked pair?");
    }

    /// <summary>
    /// A handoff's body is empty by design — the note travels on the task's own stream and the
    /// envelope is only the nudge — and a note with no about-task carries no About row at all.
    /// Saying so beats printing nothing, which reads as a command that failed quietly.
    /// </summary>
    [Fact]
    public void A_note_with_no_body_and_no_about_task_says_so_rather_than_printing_a_blank()
    {
        MessageDetails message = Received();
        message.About = null;
        message.Kind = MessageKind.Handoff.Value;
        message.Body = string.Empty;

        MessageShowCommand.Lines(message, "hall9k", Zone).Should().Equal(
            "From     abcdef012345 (node 4a1b2c3d)",
            "Project  hall9k (9f8e7d6c)",
            "Kind     handoff",
            "Sent     2026-09-22 08:38",
            "Received 2026-09-22 08:41",
            "Status   unread",
            "",
            "(no body)");
    }

    /// <summary>
    /// Nothing about a sender is guessed at: a message that arrived without an owner fingerprint
    /// says so rather than inventing one, and a project this node holds no row for is named by
    /// the id it does have. Both are states a replicated message really reaches.
    /// </summary>
    [Fact]
    public void An_unrecorded_sender_and_an_unknown_project_are_named_honestly()
    {
        MessageDetails message = Received();
        message.FromOwnerFingerprint = null;

        MessageShowCommand.Lines(message, projectName: null, Zone).Take(2).Should().Equal(
            "From     owner not recorded (node 4a1b2c3d)",
            "Project  9f8e7d6c (no project of that id on this node)");
    }

    /// <summary>
    /// A note queued before messages were project-scoped (idea 202383dc, M2) carries
    /// <see cref="Guid.Empty"/> rather than a project, which is not an id to shorten and print.
    /// </summary>
    [Fact]
    public void A_note_from_before_project_scoping_names_no_project()
    {
        MessageDetails message = Received();
        message.ProjectId = Guid.Empty;

        MessageShowCommand.Lines(message, projectName: null, Zone)[1].Should()
            .Be("Project  none recorded (queued before messages were project-scoped)");
    }

    private static MessageDetails Received() => new()
    {
        Id = Note,
        FromNodeId = Sender,
        Seq = 1,
        ProjectId = Project,
        QueuedAt = new DateTimeOffset(2026, 9, 22, 8, 38, 0, TimeSpan.Zero),
        SentAt = new DateTimeOffset(2026, 9, 22, 8, 38, 0, TimeSpan.Zero),
        ReceivedAt = new DateTimeOffset(2026, 9, 22, 8, 41, 0, TimeSpan.Zero),
        FromOwnerFingerprint = "abcdef0123456789",
        To = "project",
        About = "28b19893",
        Kind = MessageKind.Note.Value,
        Body = "are you still on the stacked pair?",
    };
}
