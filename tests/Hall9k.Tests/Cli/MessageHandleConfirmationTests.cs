using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k message handle</c>'s own confirmation line names the sender by (task b7d8222e):
/// their label beside the full fingerprint it has always printed — the label never replaces it,
/// unlike <c>h9k message show</c>'s own From line, which keeps only the short form beside it.
/// </summary>
public sealed class MessageHandleConfirmationTests
{
    private const string Fingerprint = "abcdef0123456789";
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ProjectMemberLabels LabelsWith(DisplayName displayName, string? login) => new()
    {
        Id = Guid.NewGuid(),
        Labels = [new ProjectMemberLabel(Fingerprint, [NodeId], displayName, login)],
    };

    [Fact]
    public void Names_the_sender_by_their_display_name_beside_the_full_fingerprint()
    {
        MessageHandleCommand.ConfirmationSender(Fingerprint, LabelsWith(DisplayName.Parse("Brian"), "brianhallmanac"))
            .Should().Be($"Brian ({Fingerprint})");
    }

    [Fact]
    public void Falls_back_to_the_login_with_no_display_name_declared()
    {
        MessageHandleCommand.ConfirmationSender(Fingerprint, LabelsWith(DisplayName.None, "brianhallmanac"))
            .Should().Be($"brianhallmanac ({Fingerprint})");
    }

    [Fact]
    public void Falls_back_to_the_full_fingerprint_twice_with_neither_recorded()
    {
        MessageHandleCommand.ConfirmationSender(Fingerprint, labels: null)
            .Should().Be($"abcdef012345 ({Fingerprint})");
    }

    [Fact]
    public void An_unrecorded_sender_says_so_rather_than_inventing_one()
    {
        MessageHandleCommand.ConfirmationSender(fingerprint: null, labels: null).Should().Be("owner not recorded");
    }
}
