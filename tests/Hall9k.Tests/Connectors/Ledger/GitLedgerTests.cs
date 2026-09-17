using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Connectors.Ledger;

/// <summary>
/// <see cref="GitLedger"/> against real throwaway bare repositories (<see cref="LedgerTestRepo"/>)
/// — the one place git is allowed in tests, per Brian's 2026-09-13 rule. Every test below stands
/// up a "hub" (the project's remote) and one or more "node" clones of it (the project's own bare
/// repository, `repo/&lt;name&gt;.git`), and drives <see cref="GitLedger"/> only against a node —
/// never the hub directly, and never a worktree.
/// </summary>
public sealed class GitLedgerTests : IDisposable
{
    private readonly LedgerTestRepo _repo = new();
    private readonly GitLedger _ledger = new(NullLogger<GitLedger>.Instance);
    private readonly LedgerCommitter _committer = new("Ledger Test", "ledger-test@hall9k.local");
    private readonly string _signingKeyPath;
    private readonly LedgerSigningKey _signingKey;

    public GitLedgerTests()
    {
        (_signingKeyPath, _) = GenerateSshKeypair();
        _signingKey = new LedgerSigningKey(_signingKeyPath);
    }

    public void Dispose()
    {
        _repo.Dispose();
        File.Delete(_signingKeyPath);
        File.Delete($"{_signingKeyPath}.pub");
    }

    [Fact]
    public async Task WriteAsync_ThenAFreshClone_CanReadTheWriteBackThroughGitLedger()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string writer = _repo.CloneNode(hub);

        LedgerWriteOutcome outcome = await _ledger.WriteAsync(
            new LedgerWriteRequest(writer, refName, "a.yaml", "content: v1\n", null, "write a", _committer, _signingKey),
            CancellationToken.None);

        outcome.Verdict.Should().Be(LedgerWriteVerdict.Written);
        outcome.CommitId.Should().NotBeNullOrEmpty();

        string reader = _repo.CloneNode(hub);
        LedgerFile read = await _ledger.ReadAsync(reader, refName, "a.yaml", CancellationToken.None);

        read.Exists.Should().BeTrue();
        read.Content.Should().Be("content: v1\n");
    }

    [Fact]
    public async Task WriteAsync_OnASecondCallerAndASecondRef_WorksIndependentlyOfTheFirst()
    {
        string firstRef = UniqueTestRef();
        string secondRef = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        LedgerWriteOutcome first = await _ledger.WriteAsync(
            new LedgerWriteRequest(node, firstRef, "first.yaml", "first caller\n", null, "first", _committer, _signingKey),
            CancellationToken.None);
        LedgerWriteOutcome second = await _ledger.WriteAsync(
            new LedgerWriteRequest(node, secondRef, "second.yaml", "second caller\n", null, "second", _committer, _signingKey),
            CancellationToken.None);

        first.Verdict.Should().Be(LedgerWriteVerdict.Written);
        second.Verdict.Should().Be(LedgerWriteVerdict.Written);

        (await _ledger.ReadAsync(node, firstRef, "first.yaml", CancellationToken.None)).Content.Should().Be("first caller\n");
        (await _ledger.ReadAsync(node, secondRef, "second.yaml", CancellationToken.None)).Content.Should().Be("second caller\n");

        // The two refs are genuinely independent lines of history: writing the second never
        // touched the first's own tip.
        (int firstExit, string firstTip, _) = LedgerTestRepo.RevParseQuiet(node, firstRef);
        firstExit.Should().Be(0);
        firstTip.Trim().Should().Be(first.CommitId);
    }

    [Fact]
    public async Task ReadOrWriteAsync_OnAnUnregisteredRef_Refuses()
    {
        string unregistered = $"refs/hall9k/not-registered-{Guid.NewGuid():N}";
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        Func<Task> read = () => _ledger.ReadAsync(node, unregistered, "x.yaml", CancellationToken.None);
        Func<Task> write = () => _ledger.WriteAsync(
            new LedgerWriteRequest(node, unregistered, "x.yaml", "x\n", null, "x", _committer, _signingKey), CancellationToken.None);

        await read.Should().ThrowAsync<ArgumentException>();
        await write.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WriteAsync_WriteOnlyIfAbsent_LosesWhenThePathAlreadyExists()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        await _ledger.WriteAsync(
            new LedgerWriteRequest(node, refName, "holder.yaml", "node-a\n", null, "claim", _committer, _signingKey),
            CancellationToken.None);

        LedgerWriteOutcome secondClaim = await _ledger.WriteAsync(
            new LedgerWriteRequest(node, refName, "holder.yaml", "node-b\n", null, "claim", _committer, _signingKey),
            CancellationToken.None);

        secondClaim.Verdict.Should().Be(LedgerWriteVerdict.Conflict);
        secondClaim.Current.Should().NotBeNull();
        secondClaim.Current!.Content.Should().Be("node-a\n");
    }

    [Fact]
    public async Task WriteAsync_TwoWritersOnDifferentPaths_NeitherWriteIsLost()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string nodeA = _repo.CloneNode(hub);
        string nodeB = _repo.CloneNode(hub);
        GitLedger ledgerA = new(NullLogger<GitLedger>.Instance);
        GitLedger ledgerB = new(NullLogger<GitLedger>.Instance);

        Task<LedgerWriteOutcome> writeA = ledgerA.WriteAsync(
            new LedgerWriteRequest(nodeA, refName, "a.yaml", "from node a\n", null, "claim a", _committer, _signingKey),
            CancellationToken.None);
        Task<LedgerWriteOutcome> writeB = ledgerB.WriteAsync(
            new LedgerWriteRequest(nodeB, refName, "b.yaml", "from node b\n", null, "claim b", _committer, _signingKey),
            CancellationToken.None);
        LedgerWriteOutcome[] outcomes = await Task.WhenAll(writeA, writeB);

        outcomes[0].Verdict.Should().Be(LedgerWriteVerdict.Written);
        outcomes[1].Verdict.Should().Be(LedgerWriteVerdict.Written);

        string reader = _repo.CloneNode(hub);
        (await _ledger.ReadAsync(reader, refName, "a.yaml", CancellationToken.None)).Content.Should().Be("from node a\n");
        (await _ledger.ReadAsync(reader, refName, "b.yaml", CancellationToken.None)).Content.Should().Be("from node b\n");
    }

    [Fact]
    public async Task WriteAsync_RequireEmptyPrefix_RefusesWhenARivalWriteLandedUnderTheSamePrefixFirst()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string nodeA = _repo.CloneNode(hub);
        string nodeB = _repo.CloneNode(hub);

        GitLedger racing = new(NullLogger<GitLedger>.Instance)
        {
            // A genesis-style write only ever checks HasAnyAsync once, up front — this lands a real
            // rival's own genesis file under the SAME prefix, but a DIFFERENT path, right before
            // this attempt's own push, so ExpectedBlobId alone (which only guards this write's own
            // path) would never catch it. RequireEmptyPrefix is what has to.
            BeforePushForTesting = async (attempt, cancellationToken) =>
            {
                if (attempt == 1)
                {
                    await new GitLedger(NullLogger<GitLedger>.Instance).WriteAsync(
                        new LedgerWriteRequest(
                            nodeB, refName, "members/b.yaml", "root_fingerprint: \"b\"\n", null, "genesis b", _committer, _signingKey,
                            RequireEmptyPrefix: "members/"),
                        cancellationToken);
                }
            },
        };

        LedgerWriteOutcome outcome = await racing.WriteAsync(
            new LedgerWriteRequest(
                nodeA, refName, "members/a.yaml", "root_fingerprint: \"a\"\n", null, "genesis a", _committer, _signingKey,
                RequireEmptyPrefix: "members/"),
            CancellationToken.None);

        outcome.Verdict.Should().Be(LedgerWriteVerdict.Conflict);

        string reader = _repo.CloneNode(hub);
        (await _ledger.ReadAsync(reader, refName, "members/a.yaml", CancellationToken.None)).Exists.Should().BeFalse();
        (await _ledger.ReadAsync(reader, refName, "members/b.yaml", CancellationToken.None)).Exists.Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_RejectedPushOnAnUntouchedPath_ReFetchesAndReapplies()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string nodeA = _repo.CloneNode(hub);
        string nodeC = _repo.CloneNode(hub);

        LedgerFile baseline = await _ledger.WriteFirstAndReRead(nodeA, refName, "a.yaml", "v1\n", _committer, _signingKey);

        GitLedger racing = new(NullLogger<GitLedger>.Instance)
        {
            // Lands a real second writer's commit on the shared hub — touching an unrelated path —
            // right before this attempt's own push, forcing a genuine, deterministic git-level
            // rejection instead of racing real process scheduling.
            BeforePushForTesting = async (attempt, cancellationToken) =>
            {
                if (attempt == 1)
                {
                    await new GitLedger(NullLogger<GitLedger>.Instance).WriteAsync(
                        new LedgerWriteRequest(nodeC, refName, "b.yaml", "other\n", null, "claim b", _committer, _signingKey),
                        cancellationToken);
                }
            },
        };

        LedgerWriteOutcome outcome = await racing.WriteAsync(
            new LedgerWriteRequest(nodeA, refName, "a.yaml", "v2\n", baseline.BlobId, "update a", _committer, _signingKey),
            CancellationToken.None);

        outcome.Verdict.Should().Be(LedgerWriteVerdict.Written);

        string reader = _repo.CloneNode(hub);
        (await _ledger.ReadAsync(reader, refName, "a.yaml", CancellationToken.None)).Content.Should().Be("v2\n");
        (await _ledger.ReadAsync(reader, refName, "b.yaml", CancellationToken.None)).Content.Should().Be("other\n");
    }

    [Fact]
    public async Task WriteAsync_RejectedPushOnAChangedPath_ReportsTheConflictInstead()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string nodeA = _repo.CloneNode(hub);
        string nodeC = _repo.CloneNode(hub);

        LedgerFile baseline = await _ledger.WriteFirstAndReRead(nodeA, refName, "a.yaml", "v1\n", _committer, _signingKey);

        GitLedger racing = new(NullLogger<GitLedger>.Instance)
        {
            BeforePushForTesting = async (attempt, cancellationToken) =>
            {
                if (attempt == 1)
                {
                    // Lands a competing write to the SAME path this attempt is about to push,
                    // right before it pushes.
                    await new GitLedger(NullLogger<GitLedger>.Instance).WriteAsync(
                        new LedgerWriteRequest(nodeC, refName, "a.yaml", "from-c\n", baseline.BlobId, "claim a from c", _committer, _signingKey),
                        cancellationToken);
                }
            },
        };

        LedgerWriteOutcome outcome = await racing.WriteAsync(
            new LedgerWriteRequest(nodeA, refName, "a.yaml", "v2-from-a\n", baseline.BlobId, "update a", _committer, _signingKey),
            CancellationToken.None);

        outcome.Verdict.Should().Be(LedgerWriteVerdict.Conflict);
        outcome.Current.Should().NotBeNull();
        outcome.Current!.Content.Should().Be("from-c\n");
    }

    [Fact]
    public async Task OrdinaryFetch_DoesNotBringTheLedgerRefDown_ButGitLedgersOwnFetchDoes()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string writer = _repo.CloneNode(hub);

        LedgerWriteOutcome written = await _ledger.WriteAsync(
            new LedgerWriteRequest(writer, refName, "a.yaml", "v1\n", null, "write a", _committer, _signingKey),
            CancellationToken.None);
        written.Verdict.Should().Be(LedgerWriteVerdict.Written);

        // A fresh clone plus a plain, ordinary `git fetch origin` (heads-only refspec, exactly as
        // RepoMaterialiser configures a real project's own bare repo) — no ledger code involved.
        string reader = _repo.CloneNode(hub);
        (int fetchExit, _, _) = LedgerTestRepo.OrdinaryFetch(reader);
        fetchExit.Should().Be(0);

        (int revParseExit, _, _) = LedgerTestRepo.RevParseQuiet(reader, $"{refName}^{{commit}}");
        revParseExit.Should().NotBe(0, "an ordinary fetch must never bring a refs/hall9k/ ref down");

        // GitLedger's own explicit refspec still finds it.
        LedgerFile read = await _ledger.ReadAsync(reader, refName, "a.yaml", CancellationToken.None);
        read.Content.Should().Be("v1\n");
    }

    [Fact]
    public async Task WriteAsync_WithASigningKey_ProducesACommitThatVerifiesAgainstThatKey()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        (string privateKeyPath, string publicKey) = GenerateSshKeypair();
        try
        {
            LedgerSigningKey signingKey = new(privateKeyPath);

            LedgerWriteOutcome outcome = await _ledger.WriteAsync(
                new LedgerWriteRequest(node, refName, "a.yaml", "v1\n", null, "signed write", _committer, signingKey),
                CancellationToken.None);

            outcome.Verdict.Should().Be(LedgerWriteVerdict.Written);

            string allowedSignersFile = Path.Combine(Path.GetTempPath(), $"h9k-ledger-allowed-signers-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(allowedSignersFile, $"{_committer.Email} {publicKey}\n");
                (int verifyExit, _, string verifyError) = LedgerTestRepo.VerifyCommit(node, outcome.CommitId!, allowedSignersFile);
                verifyExit.Should().Be(0, $"git verify-commit should confirm the signature: {verifyError}");
            }
            finally
            {
                File.Delete(allowedSignersFile);
            }
        }
        finally
        {
            // The private key is an unencrypted throwaway signing key — deleted here rather than
            // left behind in the system temp directory for every run of this test.
            File.Delete(privateKeyPath);
            File.Delete($"{privateKeyPath}.pub");
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesThePathAsAFreshCommit_AndAFreshCloneNeverSeesItAgain()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string writer = _repo.CloneNode(hub);

        LedgerFile seeded = await _ledger.WriteFirstAndReRead(writer, refName, "a.yaml", "v1\n", _committer, _signingKey);

        LedgerWriteOutcome outcome = await _ledger.DeleteAsync(
            new LedgerDeleteRequest(writer, refName, "a.yaml", seeded.BlobId, "delete a", _committer, _signingKey),
            CancellationToken.None);

        outcome.Verdict.Should().Be(LedgerWriteVerdict.Written);

        string reader = _repo.CloneNode(hub);
        LedgerFile read = await _ledger.ReadAsync(reader, refName, "a.yaml", CancellationToken.None);
        read.Exists.Should().BeFalse("a member removal deletes the file outright, never a tombstone");
    }

    [Fact]
    public async Task DeleteAsync_WhenThePathAlreadyChanged_ReportsTheConflictInstead()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string writer = _repo.CloneNode(hub);

        LedgerFile seeded = await _ledger.WriteFirstAndReRead(writer, refName, "a.yaml", "v1\n", _committer, _signingKey);
        await _ledger.WriteAsync(
            new LedgerWriteRequest(writer, refName, "a.yaml", "v2\n", seeded.BlobId, "update a", _committer, _signingKey),
            CancellationToken.None);

        LedgerWriteOutcome outcome = await _ledger.DeleteAsync(
            new LedgerDeleteRequest(writer, refName, "a.yaml", seeded.BlobId, "delete stale a", _committer, _signingKey),
            CancellationToken.None);

        outcome.Verdict.Should().Be(LedgerWriteVerdict.Conflict);
        outcome.Current!.Content.Should().Be("v2\n");
    }

    [Fact]
    public async Task DeleteAsync_WithNoSigningKey_IsRefused()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        Func<Task> delete = () => _ledger.DeleteAsync(
            new LedgerDeleteRequest(node, refName, "a.yaml", null, "delete a", _committer), CancellationToken.None);

        await delete.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task WriteAsync_WithNoSigningKey_IsRefused()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        Func<Task> write = () => _ledger.WriteAsync(
            new LedgerWriteRequest(node, refName, "a.yaml", "v1\n", null, "write a", _committer),
            CancellationToken.None);

        await write.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsEveryFileUnderThePrefix_EachWithItsOwnPathAndBlobId()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string writer = _repo.CloneNode(hub);

        LedgerWriteOutcome first = await _ledger.WriteAsync(
            new LedgerWriteRequest(writer, refName, "records/one.yaml", "task-id: one\n", null, "one", _committer, _signingKey),
            CancellationToken.None);
        LedgerWriteOutcome second = await _ledger.WriteAsync(
            new LedgerWriteRequest(writer, refName, "records/two.yaml", "task-id: two\n", null, "two", _committer, _signingKey),
            CancellationToken.None);
        // A file outside the prefix — proves ReadAllAsync filters by path prefix rather than
        // returning every file in the tree.
        await _ledger.WriteAsync(
            new LedgerWriteRequest(writer, refName, "other/three.yaml", "task-id: three\n", null, "three", _committer, _signingKey),
            CancellationToken.None);

        first.Verdict.Should().Be(LedgerWriteVerdict.Written);
        second.Verdict.Should().Be(LedgerWriteVerdict.Written);

        string reader = _repo.CloneNode(hub);
        IReadOnlyList<LedgerEntry> entries = await _ledger.ReadAllAsync(reader, refName, "records/", CancellationToken.None);

        entries.Should().HaveCount(2);
        LedgerEntry one = entries.Should().ContainSingle(entry => entry.Path == "records/one.yaml").Subject;
        LedgerEntry two = entries.Should().ContainSingle(entry => entry.Path == "records/two.yaml").Subject;
        one.Content.Should().Be("task-id: one\n");
        two.Content.Should().Be("task-id: two\n");
        one.BlobId.Should().NotBeNullOrEmpty();
        two.BlobId.Should().NotBeNullOrEmpty();
        one.BlobId.Should().NotBe(two.BlobId, "each file's own blob id, not the commit id");

        // The blob id ReadAllAsync reports for a path matches what a targeted ReadAsync reports for
        // that same path — the two primitives read the same tip the same way.
        LedgerFile direct = await _ledger.ReadAsync(reader, refName, "records/one.yaml", CancellationToken.None);
        one.BlobId.Should().Be(direct.BlobId);
    }

    [Fact]
    public async Task ReadAllAsync_OnARefNeverPushedTo_ReturnsEmpty()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        IReadOnlyList<LedgerEntry> entries = await _ledger.ReadAllAsync(node, refName, "records/", CancellationToken.None);

        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAllAsync_OnAnUnregisteredRef_Refuses()
    {
        string unregistered = $"refs/hall9k/not-registered-{Guid.NewGuid():N}";
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);

        Func<Task> readAll = () => _ledger.ReadAllAsync(node, unregistered, "records/", CancellationToken.None);

        await readAll.Should().ThrowAsync<ArgumentException>();
    }

    private static string UniqueTestRef() => LedgerRefRegistry.RegisterExact($"refs/hall9k/ledger/test-{Guid.NewGuid():N}").RefspecSource;

    private static (string PrivateKeyPath, string PublicKey) GenerateSshKeypair()
    {
        string keyPath = Path.Combine(Path.GetTempPath(), $"h9k-ledger-signing-key-{Guid.NewGuid():N}");
        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh-keygen",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add("ed25519");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(keyPath);
        process.StartInfo.ArgumentList.Add("-N");
        process.StartInfo.ArgumentList.Add(string.Empty);
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add("ledger-test");
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keygen failed: {output}{error}");
        }

        return (keyPath, File.ReadAllText($"{keyPath}.pub").Trim());
    }
}

file static class GitLedgerTestExtensions
{
    /// <summary>Seeds a path with an initial write, then reads it back for the blob id a
    /// conflict/re-apply test needs as its baseline ExpectedBlobId.</summary>
    public static async Task<LedgerFile> WriteFirstAndReRead(
        this GitLedger ledger, string repositoryPath, string refName, string path, string content,
        LedgerCommitter committer, LedgerSigningKey signingKey)
    {
        await ledger.WriteAsync(
            new LedgerWriteRequest(repositoryPath, refName, path, content, null, "seed", committer, signingKey),
            CancellationToken.None);
        return await ledger.ReadAsync(repositoryPath, refName, path, CancellationToken.None);
    }
}
