using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Xunit;

namespace Hall9k.Tests.Connectors.Ledger;

/// <summary>
/// <see cref="GitLedgerCommitReader"/> against a real throwaway bare repository
/// (<see cref="LedgerTestRepo"/>) — the one place git is allowed in tests, per Brian's 2026-09-13
/// rule. <c>PromptAddendaSweepEngineTests</c> (both the unit and the RequiresDocker integration
/// tier) drive <c>PromptAddendaSweepEngine</c>'s own owner test through a hand-built
/// <c>FakeLedgerCommitReader</c>, which can only ever return whatever history a test author writes
/// by hand — it cannot expose a real git behaviour nobody thought to fake. This class is what
/// actually proves <see cref="GitLedgerCommitReader.ReadCommitsTouchingPathAsync"/> sees a signed
/// reissue of already-current content against real git (independent pre-PR review, cycle 3, both
/// lenses, high): no test anywhere else in this suite touches real git for this seam at all.
/// </summary>
public sealed class GitLedgerCommitReaderTests : IDisposable
{
    private readonly LedgerTestRepo _repo = new();
    private readonly GitLedger _ledger = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitLedger>.Instance);
    private readonly GitLedgerCommitReader _commitReader = new();
    private readonly LedgerCommitter _committer = new("Ledger Test", "ledger-test@hall9k.local");

    public void Dispose() => _repo.Dispose();

    /// <summary>
    /// A member writes X, then the owner re-asserts the identical bytes X with their own signing
    /// key. The owner's own commit is TREESAME to its parent for this path (git computes the exact
    /// same tree entry for byte-identical content), so <c>git log -- path</c> — what this class
    /// used before the fix — prunes it unconditionally, and a caller walking that pruned history
    /// could never see the owner's own reissue at all. Reproduced directly against real git here
    /// rather than only through a hand-built fake.
    /// </summary>
    [Fact]
    public async Task ReadCommitsTouchingPathAsync_SeesAnOwnerReissueOfContentAMemberAlreadyWrote()
    {
        string refName = LedgerRefRegistry.RegisterExact($"refs/hall9k/ledger/test-{Guid.NewGuid():N}").RefspecSource;
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
        string refName = LedgerRefRegistry.RegisterExact($"refs/hall9k/ledger/test-{Guid.NewGuid():N}").RefspecSource;
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

    private static (string PrivateKeyPath, string PublicKey) GenerateSshKeypair()
    {
        string keyPath = Path.Combine(Path.GetTempPath(), $"h9k-commit-reader-signing-key-{Guid.NewGuid():N}");
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
        process.StartInfo.ArgumentList.Add("commit-reader-test");
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
