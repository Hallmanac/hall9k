using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure half of writing the record into a published task's issue: which publish stamp the next
/// write carries. The store-backed half — composing the record from a task, its epic and its
/// dependency edges, and rewriting the issue body — lives in
/// <c>Integration.TaskRecordIntegrationTests</c>.
/// </summary>
public sealed class TaskRecordPublicationTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 4, 12, TimeSpan.Zero);

    [Fact]
    public void An_issue_with_no_record_is_stamped_with_the_moment_the_first_one_is_written()
    {
        TaskRecordPublication.PublishStamp("## Objective\n\nSomething a person filed.", Now)
            .Should().Be(Now);
        TaskRecordPublication.PublishStamp(null, Now).Should().Be(Now);
    }

    [Fact]
    public void A_record_already_on_the_issue_keeps_the_stamp_it_carries()
    {
        string body = GitHubIssueBody.WithRecord("## Objective\n\nThe objective.", Record(PublishedAt));

        // A revise rewrites the block; it has not republished the task, so moving the stamp would
        // tell the next install to read this copy as newer work than it is.
        TaskRecordPublication.PublishStamp(body, Now).Should().Be(PublishedAt);
    }

    /// <summary>
    /// The shape a hand-written block has: a record whose <c>published</c> line is absent or
    /// unreadable. <see cref="TaskRecord.TryParse"/> reads that as
    /// <see cref="DateTimeOffset.MinValue"/> — no publish time was observed — and a write that
    /// carried the value through would put <c>published: 0001-01-01 00:00:00Z</c> on the issue,
    /// claiming an observation nobody made (external review on PR #276).
    /// </summary>
    [Fact]
    public void A_record_with_no_readable_stamp_is_stamped_now_rather_than_with_year_one()
    {
        string written = GitHubIssueBody.WithRecord("## Objective\n\nThe objective.", Record(PublishedAt));
        string missing = written.Replace(
            $"published: {Stamp(PublishedAt)}\n", string.Empty, StringComparison.Ordinal);
        string unreadable = written.Replace(
            $"published: {Stamp(PublishedAt)}", "published: sometime last week", StringComparison.Ordinal);

        missing.Should().NotBe(written, "the fixture has to actually remove the line it means to");
        unreadable.Should().NotBe(written);

        TaskRecordPublication.PublishStamp(missing, Now).Should().Be(Now);
        TaskRecordPublication.PublishStamp(unreadable, Now).Should().Be(Now);
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
        + "Z";

    private static TaskRecord Record(DateTimeOffset publishedAt) => new(
        "hall9k",
        "feature",
        "A published task's issue carries the whole task record",
        ["Publishing writes the block"],
        null,
        null,
        PreApprovalMode.Off,
        [],
        0,
        null,
        null,
        TaskRecordCaps.None,
        new TaskOrigin(
            Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
            "HALLMANAC-MAC",
            Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
            "task/7325335a-a-published-task-s-github-issu",
            publishedAt));
}
