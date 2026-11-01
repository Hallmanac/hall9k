using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>What <c>h9k task show</c> prints for a task's lesson pick: every field of the record, and nothing a session wrote.</summary>
public sealed class TaskShowLessonPickTests
{
    [Fact]
    public void Every_field_of_the_pick_is_printed()
    {
        Guid picked = DomainId.New();
        Guid alsoPicked = DomainId.New();
        Guid newest = DomainId.New();
        LessonPickRecorded pick = new(
            DomainId.New(), LessonPickerMode.Shadow, LessonPickOutcome.Picked, [picked, alsoPicked], [newest],
            EligibleCount: 44, DroppedIdCount: 2, AgentModel.Sonnet, TimeSpan.FromSeconds(12.34),
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

        string text = string.Join('\n', TaskShowCommand.LessonPickLines(pick));

        text.Should().Contain("Mode shadow").And.Contain("outcome picked");
        text.Should().Contain("Eligible lessons 44").And.Contain("ids dropped from the answer 2");
        text.Should().Contain($"Picked (2): {DomainId.Short(picked)}, {DomainId.Short(alsoPicked)}");
        text.Should().Contain($"Newest-first would have carried (1): {DomainId.Short(newest)}");
        text.Should().Contain($"Model {AgentModel.Sonnet.Value}").And.Contain("took 12.3s");
    }

    [Fact]
    public void A_pick_that_chose_nothing_says_none_rather_than_printing_an_empty_list()
    {
        LessonPickRecorded pick = new(
            DomainId.New(), LessonPickerMode.On, LessonPickOutcome.TimedOut, [], [], 0, 0, AgentModel.Sonnet,
            TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);

        string text = string.Join('\n', TaskShowCommand.LessonPickLines(pick));

        text.Should().Contain("outcome timed-out").And.Contain("Picked (0): none").And.Contain("carried (0): none");
    }
}
