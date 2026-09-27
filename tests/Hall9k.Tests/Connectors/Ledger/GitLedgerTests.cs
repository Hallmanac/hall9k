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
    private readonly GitLedgerCommitReader _commitReader = new();
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

    /// <summary>
    /// <see cref="GitLedgerCommitReader.ReadCommitsTouchingPathAsync"/>'s own owner-walk seam
    /// starts here — folded into this class rather than kept in one of its own (independent pre-PR
    /// review, cycle 5, conformance lens, low: <see cref="LedgerTestRepo"/> and the real
    /// <c>ssh-keygen</c> fixture this seam needs are already this class' own, per Brian's
    /// 2026-09-13 rule that no test outside <see cref="GitLedgerTests"/> and the message-transport
    /// chain reader's own tests touches a real repository). <c>PromptAddendaSweepEngineTests</c>
    /// (both the unit and the RequiresDocker integration tier) drive
    /// <c>PromptAddendaSweepEngine</c>'s own owner test through a hand-built
    /// <c>FakeLedgerCommitReader</c>, which can only ever return whatever history a test author
    /// writes by hand — it cannot expose a real git behaviour nobody thought to fake.
    /// <para>
    /// This first case: a member writes X, then the owner re-asserts the identical bytes X with
    /// their own signing key. The owner's own commit is TREESAME to its parent for this path (git
    /// computes the exact same tree entry for byte-identical content), so `git log -- path` — what
    /// this seam used before the cycle-3 fix — prunes it unconditionally, and a caller walking that
    /// pruned history could never see the owner's own reissue at all. Reproduced directly against
    /// real git here rather than only through a hand-built fake.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReadCommitsTouchingPathAsync_SeesAnOwnerReissueOfContentAMemberAlreadyWrote()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);
        (string ownerKeyPath, string ownerPublicKey) = GenerateSshKeypair();
        try
        {
            LedgerSigningKey ownerSigningKey = new(ownerKeyPath);

            await _ledger.WriteAsync(
                new LedgerWriteRequest(node, refName, "a.yaml", "shared content\n", null, "member write", _committer, ownerSigningKey),
                CancellationToken.None);
            LedgerFile memberTip = await _ledger.ReadAsync(node, refName, "a.yaml", CancellationToken.None);

            LedgerWriteOutcome reissue = await _ledger.WriteAsync(
                new LedgerWriteRequest(
                    node, refName, "a.yaml", "shared content\n", memberTip.BlobId, "owner reissue", _committer, ownerSigningKey),
                CancellationToken.None);
            reissue.Verdict.Should().Be(LedgerWriteVerdict.Written, "identical content is still a distinct, real commit");

            IReadOnlyList<LedgerPathCommit> commits = await _commitReader.ReadCommitsTouchingPathAsync(
                node, refName, "a.yaml",
                (rawCommitBytes, cancellationToken) => _commitReader.IsSignedByAsync(node, rawCommitBytes, ownerPublicKey, cancellationToken),
                CancellationToken.None);

            commits.Should().ContainSingle(
                "the walk must stop at the reissue commit — the first one the predicate accepts — never reading "
                + "the member's own older commit behind it")
                .Which.CommitSha.Should().Be(reissue.CommitId);
            commits[0].Content.Should().Be("shared content\n");
        }
        finally
        {
            File.Delete(ownerKeyPath);
            File.Delete($"{ownerKeyPath}.pub");
        }
    }

    /// <summary>The mirror with no predicate ever accepting: the whole real history comes back,
    /// including the TREESAME reissue commit — proving the fix is dropping the pathspec, not merely
    /// an early-stop side effect that happens to hide the defect.</summary>
    [Fact]
    public async Task ReadCommitsTouchingPathAsync_WithNothingEverAuthorized_ReturnsTheWholeHistoryIncludingATreesameCommit()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);
        (string keyPath, _) = GenerateSshKeypair();
        try
        {
            LedgerSigningKey signingKey = new(keyPath);

            LedgerWriteOutcome first = await _ledger.WriteAsync(
                new LedgerWriteRequest(node, refName, "a.yaml", "shared content\n", null, "first write", _committer, signingKey),
                CancellationToken.None);
            LedgerFile tip = await _ledger.ReadAsync(node, refName, "a.yaml", CancellationToken.None);
            LedgerWriteOutcome reissue = await _ledger.WriteAsync(
                new LedgerWriteRequest(
                    node, refName, "a.yaml", "shared content\n", tip.BlobId, "reissue", _committer, signingKey),
                CancellationToken.None);

            IReadOnlyList<LedgerPathCommit> commits = await _commitReader.ReadCommitsTouchingPathAsync(
                node, refName, "a.yaml", (_, _) => Task.FromResult(false), CancellationToken.None);

            commits.Select(commit => commit.CommitSha).Should().Equal(reissue.CommitId, first.CommitId);
        }
        finally
        {
            File.Delete(keyPath);
            File.Delete($"{keyPath}.pub");
        }
    }

    /// <summary>
    /// The laundering defect the cycle-3 fix above introduced (independent pre-PR review, cycle 5,
    /// conformance and adversarial lenses, both high): dropping the pathspec entirely made every
    /// commit's own tree for `path` matter, not just commits that actually wrote it, so an owner's
    /// later, wholly unrelated write to a SECOND path — built, as every ledger write is, on top of
    /// whatever the ref's tip already held for every other path — used to read as the owner
    /// re-vouching for a member's own unauthorized overwrite of the FIRST path, purely because that
    /// overwrite was still sitting in the second commit's own inherited tree. Reproduced end to end
    /// against real git: an owner write, a member overwrite of the same path, then an unrelated
    /// owner write to a second path — the walk for the first path must still land on the member's
    /// own unauthorized commit (and, walking past it, the owner's real one), never on the second
    /// path's commit.
    /// </summary>
    [Fact]
    public async Task ReadCommitsTouchingPathAsync_AnOwnerWriteToADifferentPathNeverLaundersAMembersOverwriteOfThisOne()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string node = _repo.CloneNode(hub);
        (string ownerKeyPath, string ownerPublicKey) = GenerateSshKeypair();
        (string memberKeyPath, _) = GenerateSshKeypair();
        try
        {
            LedgerSigningKey ownerSigningKey = new(ownerKeyPath);
            LedgerSigningKey memberSigningKey = new(memberKeyPath);

            LedgerWriteOutcome ownerWrite = await _ledger.WriteAsync(
                new LedgerWriteRequest(node, refName, "work.md", "owner content\n", null, "owner sets work", _committer, ownerSigningKey),
                CancellationToken.None);
            LedgerFile ownerTip = await _ledger.ReadAsync(node, refName, "work.md", CancellationToken.None);

            LedgerWriteOutcome memberOverwrite = await _ledger.WriteAsync(
                new LedgerWriteRequest(
                    node, refName, "work.md", "member overwrite\n", ownerTip.BlobId, "member overwrites work",
                    _committer, memberSigningKey),
                CancellationToken.None);

            await _ledger.WriteAsync(
                new LedgerWriteRequest(
                    node, refName, "review-lap.md", "owner content\n", null, "owner sets review-lap", _committer, ownerSigningKey),
                CancellationToken.None);

            IReadOnlyList<LedgerPathCommit> commits = await _commitReader.ReadCommitsTouchingPathAsync(
                node, refName, "work.md",
                (rawCommitBytes, cancellationToken) => _commitReader.IsSignedByAsync(node, rawCommitBytes, ownerPublicKey, cancellationToken),
                CancellationToken.None);

            commits.Select(commit => commit.CommitSha).Should().Equal(
                [memberOverwrite.CommitId, ownerWrite.CommitId],
                "the owner's later write to review-lap.md never touched work.md, so it must never appear here — "
                + "laundering it in would let it vouch for the member's own unauthorized overwrite as though the "
                + "owner had re-asserted it");
            commits[0].Content.Should().Be("member overwrite\n");
        }
        finally
        {
            File.Delete(ownerKeyPath);
            File.Delete($"{ownerKeyPath}.pub");
            File.Delete(memberKeyPath);
            File.Delete($"{memberKeyPath}.pub");
        }
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
