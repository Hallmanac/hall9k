using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What <c>h9k orchestrator feed --since</c> accepts (idea 89471598, piece 2): a duration back
/// from now, an instant, and a refusal for anything else rather than a silent "everything".
/// </summary>
public sealed class OrchestratorFeedSinceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("45m", 45)]
    [InlineData("6h", 6 * 60)]
    [InlineData("3d", 3 * 24 * 60)]
    [InlineData("2w", 2 * 7 * 24 * 60)]
    [InlineData("  6H  ", 6 * 60)]
    public void A_duration_reads_back_from_now(string value, int minutesBack) =>
        OrchestratorFeedSince.Parse(value, Now).Should().Be(Now.AddMinutes(-minutesBack));

    [Fact]
    public void An_instant_reads_as_itself() =>
        OrchestratorFeedSince.Parse("2026-09-19T10:30:00+00:00", Now)
            .Should().Be(new DateTimeOffset(2026, 9, 19, 10, 30, 0, TimeSpan.Zero));

    [Fact]
    public void A_duration_past_the_start_of_the_calendar_clamps_rather_than_throwing() =>
        OrchestratorFeedSince.Parse("2147483647w", Now).Should().Be(DateTimeOffset.MinValue);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("-3d")]
    [InlineData("3y")]
    [InlineData("d3")]
    public void Anything_else_is_refused_rather_than_read_as_everything(string value)
    {
        Action act = () => OrchestratorFeedSince.Parse(value, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*--since*");
    }

    [Fact]
    public void A_refusal_never_echoes_a_control_character_back_at_the_terminal()
    {
        Action act = () => OrchestratorFeedSince.Parse("6\u001b[31mh", Now);

        act.Should().Throw<DomainValidationException>().Which.Message.Should().NotContain("\u001b");
    }
}
