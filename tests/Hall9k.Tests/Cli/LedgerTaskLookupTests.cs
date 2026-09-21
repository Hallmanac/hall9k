using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Task 9eb5b245: <c>h9k task pull</c> has to be typeable from what a human actually has in front
/// of them, which is the SHORT id off a board row, a pull request title or a branch name — the full
/// id lived only on the node that held the task, which is the node they are not sitting at. The
/// ledger's own task records are the one local source that names a task whose stream is not here,
/// and this is the read that finds one. Driven through <see cref="FakeLedger"/> per Brian's
/// 2026-09-13 testing rule: no test outside <c>GitLedgerTests</c> touches a real repository.
/// </summary>
public sealed class LedgerTaskLookupTests
{
    private static readonly Guid TaskId = Guid.Parse("01a0afff-b9e1-74a1-b84a-2b213727884f");

    [Fact]
    public async Task The_short_id_off_a_board_row_resolves_to_the_full_id_and_the_project_whose_ledger_names_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails hall9k = Project("hall9k", "/repo-hall9k");
        await SeedRecordAsync(ledger, hall9k, TaskId, cts.Token);

        IReadOnlyList<LedgerTaskLookup.Match> matches = await LedgerTaskLookup.FindAsync(
            ledger, [hall9k], DomainId.Short(TaskId), cts.Token);

        matches.Should().ContainSingle();
        matches[0].TaskId.Should().Be(TaskId);
        matches[0].Project.Name.Should().Be("hall9k");
    }

    [Fact]
    public async Task A_full_id_and_a_head_fragment_resolve_the_same_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails hall9k = Project("hall9k", "/repo-hall9k");
        await SeedRecordAsync(ledger, hall9k, TaskId, cts.Token);

        (await LedgerTaskLookup.FindAsync(ledger, [hall9k], TaskId.ToString(), cts.Token))
            .Should().ContainSingle();
        (await LedgerTaskLookup.FindAsync(ledger, [hall9k], "01a0afff", cts.Token))
            .Should().ContainSingle("a UUIDv7's head is what a ledger record filename leads with");
    }

    /// <summary>The whole point of reading several projects' ledgers: the caller can default
    /// <c>--project</c> when exactly one of them names the task, and refuse as it always did when
    /// none does.</summary>
    [Fact]
    public async Task Only_the_project_whose_ledger_carries_the_record_matches()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails hall9k = Project("hall9k", "/repo-hall9k");
        ProjectDetails arx = Project("arx-platform", "/repo-arx");
        await SeedRecordAsync(ledger, arx, TaskId, cts.Token);

        IReadOnlyList<LedgerTaskLookup.Match> matches = await LedgerTaskLookup.FindAsync(
            ledger, [hall9k, arx], DomainId.Short(TaskId), cts.Token);

        matches.Should().ContainSingle();
        matches[0].Project.Name.Should().Be("arx-platform");
    }

    [Fact]
    public async Task A_fragment_no_record_carries_matches_nothing_rather_than_guessing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails hall9k = Project("hall9k", "/repo-hall9k");
        await SeedRecordAsync(ledger, hall9k, TaskId, cts.Token);

        (await LedgerTaskLookup.FindAsync(ledger, [hall9k], "deadbeef", cts.Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_fragment_two_records_carry_returns_both_so_the_caller_can_call_it_ambiguous()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails hall9k = Project("hall9k", "/repo-hall9k");
        await SeedRecordAsync(ledger, hall9k, Guid.Parse("01a0afff-b9e1-74a1-b84a-2b213727884f"), cts.Token);
        await SeedRecordAsync(ledger, hall9k, Guid.Parse("01a0afff-0000-74a1-b84a-2b21ffffffff"), cts.Token);

        (await LedgerTaskLookup.FindAsync(ledger, [hall9k], "01a0afff", cts.Token)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_id_with_no_characters_to_match_by_is_refused_rather_than_matching_everything()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        FakeLedger ledger = new();
        ProjectDetails hall9k = Project("hall9k", "/repo-hall9k");
        await SeedRecordAsync(ledger, hall9k, TaskId, cts.Token);

        Func<Task> find = () => LedgerTaskLookup.FindAsync(ledger, [hall9k], "-", cts.Token);

        await find.Should().ThrowAsync<DomainValidationException>();
    }

    private static ProjectDetails Project(string name, string repositoryPath) =>
        new() { Id = DomainId.New(), Name = name, RepositoryPath = repositoryPath };

    /// <summary>The minimum a task record needs to be read back (<c>TaskRecord.TryParse</c>): the
    /// version key, the task id, and a non-blank objective.</summary>
    private static Task SeedRecordAsync(
        FakeLedger ledger, ProjectDetails project, Guid taskId, CancellationToken cancellationToken) =>
        ledger.WriteAsync(
            new LedgerWriteRequest(
                project.RepositoryPath,
                LedgerRefRegistry.Records.RefspecSource,
                LedgerRefRegistry.RecordPath(taskId),
                $"hall9k-task-record: 1\ntask-id: {taskId}\nproject: {project.Name}\nobjective: Ship the thing\n",
                ExpectedBlobId: null,
                "seed task record",
                new LedgerCommitter("Brian Hall", "brian@agelessrx.com"),
                new LedgerSigningKey("fake-key")),
            cancellationToken);
}
