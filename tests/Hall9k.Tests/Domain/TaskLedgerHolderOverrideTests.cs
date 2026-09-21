using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="TaskLedgerHolder.TryOverrideAsync"/> (idea 202383dc, item 4) against a
/// <see cref="FakeLedger"/> — no Postgres, no real git, per Brian's 2026-09-13 testing rule: this
/// is A1's own conditional-write seam, and everything above it drives it through the fake.
/// </summary>
public sealed class TaskLedgerHolderOverrideTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/repos/takeover-test";
    private static readonly LedgerCommitter Committer = new("Test", "test@hall9k.local");
    private static readonly LedgerSigningKey SigningKey = new("/does/not/matter/key");

    [Fact]
    public async Task Overriding_an_absent_holder_writes_the_new_candidate()
    {
        Guid taskId = DomainId.New();
        Guid previousHolderNodeId = DomainId.New();
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("owner-a", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        Guid newHolderNodeId = DomainId.New();
        TaskRecordHolder candidate = new("owner-b", newHolderNodeId, "NEW-NODE", Now);
        HolderOverrideResult result = await TaskLedgerHolder.TryOverrideAsync(
            ledger, RepositoryPath, taskId, candidate, Committer, SigningKey, CancellationToken.None);

        result.Verdict.Should().Be(HolderOverrideVerdict.Overridden);
        result.PreviousHolder!.NodeId.Should().Be(previousHolderNodeId);
        result.CurrentHolder!.NodeId.Should().Be(newHolderNodeId);

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(newHolderNodeId, "the write actually landed against the candidate");
    }

    /// <summary>
    /// The acceptance criterion's own words: "a conditional holder write against the current
    /// holder value through A1, so two overriders cannot both win and the loser sees the new
    /// holder." Both overriders read the identical starting record (the same blob id) before
    /// either one decides anything — <see cref="FrozenFirstReadLedger"/> pins the second
    /// overrider's own first read to that snapshot, exactly what a genuine race looks like: two
    /// operators independently deciding to override the same absent holder before seeing each
    /// other's answer. The first override's write lands normally; the second's own compare-and-
    /// swap against the stale blob it read conflicts, it re-reads for real, and finds a holder
    /// that is neither the one it started against nor itself.
    /// </summary>
    [Fact]
    public async Task Two_overriders_racing_the_same_holder_only_one_wins_and_the_loser_sees_the_winner()
    {
        Guid taskId = DomainId.New();
        Guid previousHolderNodeId = DomainId.New();
        FakeLedger ledger = new();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("owner-a", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6)));

        LedgerFile snapshotBothOverridersSaw = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);

        Guid firstOverriderNodeId = DomainId.New();
        Guid secondOverriderNodeId = DomainId.New();
        TaskRecordHolder firstCandidate = new("owner-b", firstOverriderNodeId, "FIRST-NODE", Now);
        TaskRecordHolder secondCandidate = new("owner-c", secondOverriderNodeId, "SECOND-NODE", Now);

        HolderOverrideResult first = await TaskLedgerHolder.TryOverrideAsync(
            ledger, RepositoryPath, taskId, firstCandidate, Committer, SigningKey, CancellationToken.None);

        FrozenFirstReadLedger secondOverridersView = new(ledger, snapshotBothOverridersSaw);
        HolderOverrideResult second = await TaskLedgerHolder.TryOverrideAsync(
            secondOverridersView, RepositoryPath, taskId, secondCandidate, Committer, SigningKey, CancellationToken.None);

        first.Verdict.Should().Be(HolderOverrideVerdict.Overridden, "the first override reads the record nobody else has touched yet");
        second.Verdict.Should().Be(
            HolderOverrideVerdict.AlreadyOverridden, "the second override's own compare-and-swap conflicts against the first one's write");
        second.CurrentHolder!.NodeId.Should().Be(firstOverriderNodeId, "the loser's own re-read reports back whoever actually won");

        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder!.NodeId.Should().Be(
            firstOverriderNodeId, "only the winner's own write is ever visible in the ledger");
    }

    [Fact]
    public async Task Overriding_with_the_identical_candidate_already_holding_is_idempotent()
    {
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        FakeLedger ledger = new();
        TaskRecordHolder holder = new("owner-a", nodeId, "NODE-A", Now.AddHours(-1));
        await SeedRecordAsync(ledger, taskId, holder);
        int writesBeforeOverride = ledger.Writes.Count;

        HolderOverrideResult result = await TaskLedgerHolder.TryOverrideAsync(
            ledger, RepositoryPath, taskId, holder, Committer, SigningKey, CancellationToken.None);

        result.Verdict.Should().Be(HolderOverrideVerdict.Overridden);
        ledger.Writes.Should().HaveCount(writesBeforeOverride, "the record already named this exact candidate — nothing new was written");
    }

    [Fact]
    public async Task No_record_is_not_a_failure()
    {
        Guid taskId = DomainId.New();
        FakeLedger ledger = new();
        TaskRecordHolder candidate = new("owner-b", DomainId.New(), "NEW-NODE", Now);

        HolderOverrideResult result = await TaskLedgerHolder.TryOverrideAsync(
            ledger, RepositoryPath, taskId, candidate, Committer, SigningKey, CancellationToken.None);

        result.Verdict.Should().Be(HolderOverrideVerdict.NoRecord);
    }

    /// <summary>
    /// The rollback <c>h9k task take --force</c> needs when its own final domain-stream append
    /// loses a race after an override already landed (adversarial pre-PR review, cycle 2):
    /// restoring the previous holder, not releasing to no holder at all, since a null holder is
    /// freely claimable by any node's ordinary dispatch sweep — the exact double-claim hazard
    /// <c>TaskLedgerHolder.TryReleaseAsync</c>'s own callers already guard against.
    /// </summary>
    [Fact]
    public async Task Restoring_after_a_lost_race_writes_the_previous_holder_back_rather_than_clearing_it()
    {
        Guid taskId = DomainId.New();
        Guid previousHolderNodeId = DomainId.New();
        FakeLedger ledger = new();
        TaskRecordHolder previousHolder = new("owner-a", previousHolderNodeId, "OLD-NODE", Now.AddHours(-6));
        await SeedRecordAsync(ledger, taskId, previousHolder);

        Guid overridingNodeId = DomainId.New();
        TaskRecordHolder candidate = new("owner-b", overridingNodeId, "NEW-NODE", Now);
        HolderOverrideResult overrideResult = await TaskLedgerHolder.TryOverrideAsync(
            ledger, RepositoryPath, taskId, candidate, Committer, SigningKey, CancellationToken.None);
        overrideResult.Verdict.Should().Be(HolderOverrideVerdict.Overridden);

        HolderReleaseResult restoreResult = await TaskLedgerHolder.TryRestoreAsync(
            ledger, RepositoryPath, taskId, overridingNodeId, overrideResult.PreviousHolder, Committer, SigningKey,
            CancellationToken.None);

        restoreResult.Verdict.Should().Be(HolderReleaseVerdict.Released);
        LedgerFile record = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), CancellationToken.None);
        TaskRecord.TryParse(record.Content)!.Holder.Should().Be(
            previousHolder, "the rollback restores who held it before this override, not an empty holder anyone could claim");
    }

    [Fact]
    public async Task Restoring_a_record_no_longer_naming_the_overrider_is_not_held()
    {
        Guid taskId = DomainId.New();
        FakeLedger ledger = new();
        Guid someoneElseNodeId = DomainId.New();
        await SeedRecordAsync(ledger, taskId, new TaskRecordHolder("owner-c", someoneElseNodeId, "OTHER-NODE", Now));

        HolderReleaseResult restoreResult = await TaskLedgerHolder.TryRestoreAsync(
            ledger, RepositoryPath, taskId, DomainId.New(), previousHolder: null, Committer, SigningKey, CancellationToken.None);

        restoreResult.Verdict.Should().Be(
            HolderReleaseVerdict.NotHeld, "the record no longer names the overrider — nothing here for this rollback to undo");
    }

    private static async Task SeedRecordAsync(FakeLedger ledger, Guid taskId, TaskRecordHolder? holder)
    {
        TaskRecord record = new(
            taskId, "takeover-test", "chore", "Close me out", ["done"], null, null,
            PreApprovalMode.Off, null, [], null, null, TaskRecordCaps.None, "owner-a-fingerprint",
            new TaskOrigin(DomainId.New(), "ORIGIN-NODE", taskId, "task/close-me-out", Now), holder);
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId),
                record.ToYaml(), null, "seed", Committer, SigningKey),
            CancellationToken.None);
    }

    /// <summary>
    /// An <see cref="ILedger"/> whose very first <see cref="ReadAsync"/> call returns a pinned
    /// snapshot regardless of what <paramref name="inner"/> now holds, then delegates every
    /// following call (including every retry's own re-read) straight through — the seam that
    /// lets one test simulate two overriders who each read the record before either one decided
    /// anything, without any real concurrency.
    /// </summary>
    private sealed class FrozenFirstReadLedger(ILedger inner, LedgerFile frozenSnapshot) : ILedger
    {
        private bool consumed;

        public Task<LedgerFile> ReadAsync(string repositoryPath, string refName, string path, CancellationToken cancellationToken)
        {
            if (!consumed)
            {
                consumed = true;
                return Task.FromResult(frozenSnapshot);
            }

            return inner.ReadAsync(repositoryPath, refName, path, cancellationToken);
        }

        public Task<LedgerWriteOutcome> WriteAsync(LedgerWriteRequest request, CancellationToken cancellationToken) =>
            inner.WriteAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> WriteManyAsync(LedgerManyWriteRequest request, CancellationToken cancellationToken) =>
            inner.WriteManyAsync(request, cancellationToken);

        public Task<LedgerWriteOutcome> DeleteAsync(LedgerDeleteRequest request, CancellationToken cancellationToken) =>
            inner.DeleteAsync(request, cancellationToken);

        public Task<bool> HasAnyAsync(string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.HasAnyAsync(repositoryPath, refName, pathPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerRef>> ListRefsAsync(string repositoryPath, string refPrefix, CancellationToken cancellationToken) =>
            inner.ListRefsAsync(repositoryPath, refPrefix, cancellationToken);

        public Task<IReadOnlyList<LedgerEntry>> ReadAllAsync(
            string repositoryPath, string refName, string pathPrefix, CancellationToken cancellationToken) =>
            inner.ReadAllAsync(repositoryPath, refName, pathPrefix, cancellationToken);
    }
}
