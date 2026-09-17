using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure half of writing a task's record into the ledger: which publish stamp the next write
/// carries. The store-and-ledger-backed half — composing the record from a task, its epic and its
/// dependency edges, and writing it through <c>FakeLedger</c> — lives in
/// <c>Integration.TaskRecordIntegrationTests</c>.
/// </summary>
public sealed class TaskRecordPublicationTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 4, 12, TimeSpan.Zero);

    [Fact]
    public void No_existing_record_is_stamped_with_the_moment_the_first_one_is_written()
    {
        TaskRecordPublication.PublishStamp(existing: null, Now).Should().Be(Now);
    }

    [Fact]
    public void A_record_already_in_the_ledger_keeps_the_stamp_it_carries()
    {
        // A revise rewrites the whole record; it has not republished the task, so moving the stamp
        // would tell another node this copy is newer work than it is.
        TaskRecordPublication.PublishStamp(Record(PublishedAt), Now).Should().Be(PublishedAt);
    }

    /// <summary>
    /// The shape a record whose <c>published</c> field is unreadable has:
    /// <see cref="TaskRecord.TryParse"/> reads that as <see cref="DateTimeOffset.MinValue"/> — no
    /// publish time was observed — and a write that carried the value through would stamp the
    /// ledger <c>published: 0001-01-01 00:00:00Z</c>, claiming an observation nobody made (the same
    /// finding external review caught on PR #276 for the retired issue-block writer).
    /// </summary>
    [Fact]
    public void A_record_with_no_readable_stamp_is_stamped_now_rather_than_with_year_one()
    {
        TaskRecordPublication.PublishStamp(Record(DateTimeOffset.MinValue), Now).Should().Be(Now);
    }

    private static TaskRecord Record(DateTimeOffset publishedAt) => new(
        Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
        "hall9k",
        "feature",
        "A task's record lives in the ledger",
        ["Publishing writes it"],
        null,
        null,
        PreApprovalMode.Off,
        null,
        [],
        null,
        null,
        TaskRecordCaps.None,
        "abc123fingerprint",
        new TaskOrigin(
            Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
            "HALLMANAC-MAC",
            Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
            "task/7325335a-a-published-task-s-record",
            publishedAt),
        null);
}
