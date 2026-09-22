using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The id a received note's feed line opens with is the note's own, not something that merely
/// resembles it. <c>OrchestratorFeedDescription.Of</c> is handed an event's data and never the
/// stream it came off, so the arm recomputes the id from the three fields
/// <see cref="MessageReceived"/> itself carries — and that recomputation is only worth anything
/// if it lands on the identical value <c>h9k message show</c> resolves, which is what this pins.
/// <para>
/// The wording and the clip around it are <see cref="OrchestratorFeedRendererTests"/>'s golden;
/// this is only about the id.
/// </para>
/// </summary>
public sealed class OrchestratorFeedMessageLineTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 8, 38, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("01a0bc05-a960-7657-b708-1aed4a1b2c3d", "01a0bc05-a960-7657-b708-1aed9f8e7d6c", 1L)]
    [InlineData("01a0bc05-a960-7657-b708-1aed4a1b2c3d", "01a0bc05-a960-7657-b708-1aed9f8e7d6c", 4207L)]
    [InlineData("0b8f8e2e-9e2b-4f2a-8c2e-2f8b8e2e9e2b", "01a0bc05-a960-7657-b708-1aed37b5ec69", 2L)]
    public void A_notes_line_opens_with_the_short_id_of_its_own_stream(string sender, string project, long seq)
    {
        Guid fromNodeId = Guid.Parse(sender);
        Guid projectId = Guid.Parse(project);

        string line = OrchestratorFeedDescription.Of(Note(fromNodeId, projectId, seq, "please surface this at once"))!;

        string expected = DomainId.Short(MessageStreamId.ForMessage(fromNodeId, projectId, seq));
        line.Should().Be($"{expected} a message from abcdef012345: please surface this at once");
    }

    /// <summary>
    /// A handoff is the one received kind whose line carries no id, because it carries no body
    /// either: the note travels on the task's own stream and the envelope is only the nudge, so
    /// there is nothing for <c>h9k message show</c> to print that this line has clipped.
    /// </summary>
    [Fact]
    public void A_handoff_nudge_still_names_only_its_sender()
    {
        MessageReceived note = Note(
            Guid.Parse("01a0bc05-a960-7657-b708-1aed4a1b2c3d"),
            Guid.Parse("01a0bc05-a960-7657-b708-1aed9f8e7d6c"),
            1,
            string.Empty);
        MessageReceived handoff = note with { Kind = MessageKind.Handoff.Value };

        OrchestratorFeedDescription.Of(handoff).Should()
            .Be("abcdef012345 says a task's handoff note changed");
    }

    private static MessageReceived Note(Guid fromNodeId, Guid projectId, long seq, string body) => new(
        FromNodeId: fromNodeId,
        Seq: seq,
        SentAt: At,
        FromOwnerFingerprint: "abcdef0123456789",
        To: "project",
        About: null,
        Kind: MessageKind.Note.Value,
        Body: body,
        ReceivedAt: At,
        ProjectId: projectId);
}
