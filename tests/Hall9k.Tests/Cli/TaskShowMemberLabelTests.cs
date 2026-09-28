using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure rendering rules <c>h9k task show</c> composes its "Assigned to", "Taken over", and
/// "Take granted ... to owner" rows from (task 21c8f2f3) — extracted out of
/// <c>TaskShowCommand.AssigneeMarkupAsync</c> and its two sibling lines so the resolution rules
/// are a unit test rather than only an integration one, the same split
/// <c>TaskShowCommand.ComposeHumanThreadReplyDrafts</c> already uses for its own rendering.
/// </summary>
public sealed class TaskShowMemberLabelTests
{
    private const string Fingerprint = "c8f5c85900da1234567890abcdef1234567890abcdef1234567890abcdef12";
    private static readonly Guid ForeignNodeId = DomainId.New();

    private static MemberLabelLookup LabelsFor(DisplayName displayName, string? login) => new(
        new ProjectMemberLabels
        {
            Id = DomainId.New(),
            Labels = [new ProjectMemberLabel(Fingerprint, [ForeignNodeId], displayName, login)],
        });

    [Fact]
    public void A_foreign_assignee_this_install_has_no_local_record_of_shows_the_projects_display_name()
    {
        MemberLabelLookup labels = LabelsFor(DisplayName.Parse("Windows"), "windows-login");

        string markup = TaskShowCommand.AssigneeMarkup(
            DomainId.New(), owner: null, Fingerprint, trueOwner: null, labels);

        markup.Should().Contain("Windows").And.Contain(Fingerprint[..12])
            .And.Contain("this node has no local record of the declared owner id");
    }

    [Fact]
    public void A_foreign_assignee_with_neither_a_name_nor_a_login_shows_the_short_fingerprint_once()
    {
        MemberLabelLookup labels = LabelsFor(DisplayName.None, null);

        string markup = TaskShowCommand.AssigneeMarkup(
            DomainId.New(), owner: null, Fingerprint, trueOwner: null, labels);

        markup.Should().Contain(Fingerprint[..12]);
        // Deduplicated: the label IS the short fingerprint, so it must not read "abc123 (abc123)".
        markup.Should().NotContain($"{Fingerprint[..12]} ({Fingerprint[..12]})");
    }

    [Fact]
    public void This_machines_own_owner_keeps_the_local_name_when_the_fingerprint_matches()
    {
        OwnerDetails owner = new() { Id = DomainId.New(), Name = "Brian", RootFingerprint = Fingerprint };
        MemberLabelLookup labels = LabelsFor(DisplayName.Parse("Somebody Else"), null);

        string markup = TaskShowCommand.AssigneeMarkup(owner.Id, owner, Fingerprint, trueOwner: null, labels);

        markup.Should().Be("Brian");
    }

    [Fact]
    public void An_owner_root_rewrite_still_reads_as_the_trust_bearing_alarm_verbatim()
    {
        OwnerDetails owner = new() { Id = DomainId.New(), Name = "Brian", RootFingerprint = "some-other-fingerprint" };
        MemberLabelLookup labels = LabelsFor(DisplayName.Parse("Windows"), null);

        string markup = TaskShowCommand.AssigneeMarkup(owner.Id, owner, Fingerprint, trueOwner: null, labels);

        markup.Should().Contain($"known by fingerprint {Fingerprint} only")
            .And.Contain("whose own root fingerprint does not match this assignment's")
            .And.NotContain("Windows");
    }

    [Fact]
    public void A_takeover_by_another_member_names_that_members_own_label()
    {
        MemberLabelLookup labels = LabelsFor(DisplayName.Parse("Windows"), null);

        string by = TaskShowCommand.TakenOverByLabel(
            takenOverByOwnerId: DomainId.New(), takenOverByOwnerRootFingerprint: Fingerprint, labels);

        by.Should().Be("Windows");
    }

    [Fact]
    public void A_takeover_projected_before_the_fingerprint_field_existed_falls_back_to_the_short_owner_id()
    {
        Guid takerOwnerId = DomainId.New();

        string by = TaskShowCommand.TakenOverByLabel(
            takenOverByOwnerId: takerOwnerId, takenOverByOwnerRootFingerprint: null, MemberLabelLookup.Empty);

        by.Should().Be(DomainId.Short(takerOwnerId));
    }

    [Fact]
    public void A_grant_names_the_grantees_own_label()
    {
        MemberLabelLookup labels = LabelsFor(DisplayName.Parse("Windows"), null);

        string to = TaskShowCommand.GrantedToOwnerMarkup(
            lastGrantedToOwnerId: DomainId.New(), lastGrantedToOwnerFingerprint: Fingerprint, labels);

        to.Should().Be("Windows");
    }
}
