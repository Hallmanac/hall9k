using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Xunit;

namespace Hall9k.Tests.Connectors.Ledger;

/// <summary>
/// <see cref="LedgerAppendOnlyRefFetcher"/> against real throwaway bare repositories
/// (<see cref="LedgerTestRepo"/>) — the one place git is allowed in tests (Brian's 2026-09-13 rule).
/// Every scenario below builds the hub's own history directly with raw git plumbing (never a
/// "writer" clone, never a branch, never GitHub): what this class actually cares about is what
/// origin's ref currently names and what this node last verified, not how either commit got there.
/// <para>
/// <c>[Collection("RealProcessSpawn")]</c> (README.md, "<c>[Collection("RealProcessSpawn")]</c>"),
/// the same fence <see cref="Hall9k.Tests.Connectors.Ledger.GitLedgerTests"/> already carries: every
/// scenario here spawns several real <c>git</c> subprocesses (independent pre-PR review, cycle 1,
/// conformance lens, low).
/// </para>
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class LedgerAppendOnlyRefFetcherTests : IDisposable
{
    private readonly LedgerTestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task FetchAsync_ARewoundRef_IsRefused_AndTheGoodTipStaysReachable()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);

        string commitA = await BuildCommitAsync(hub, "a.yaml", "v1\n", []);
        await SetRefAsync(hub, refName, commitA);
        (await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None)).Tip.Should().Be(commitA);

        string commitB = await BuildCommitAsync(hub, "a.yaml", "v2\n", [commitA]);
        await SetRefAsync(hub, refName, commitB);
        (await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None)).Tip.Should().Be(commitB);

        // The rewind: origin moves backward to a commit this node already verified past.
        await SetRefAsync(hub, refName, commitA);
        LedgerAppendOnlyFetchResult refused = await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None);

        refused.WasRefused.Should().BeTrue();
        refused.Tip.Should().Be(commitB, "this node keeps reading the last tip it actually verified, not the rewound one");
        RevParse(reader, $"{commitB}^{{commit}}").ExitCode.Should().Be(0, "the good tip stays locally reachable");
        RevParse(reader, refName).StandardOutput.Trim().Should().Be(commitB, "the live ref itself never moved backward");
    }

    [Fact]
    public async Task FetchAsync_ASideMergeWhoseFirstParentIsTheOldTip_IsRefused_EvenThoughItIsADescendant()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);

        string commitA = await BuildCommitAsync(hub, "a.yaml", "v1\n", []);
        await SetRefAsync(hub, refName, commitA);
        await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None);

        // The "current" tip: legitimate forward progress past the old one (a revocation, say).
        string commitCurrent = await BuildCommitAsync(hub, "a.yaml", "v2\n", [commitA]);
        await SetRefAsync(hub, refName, commitCurrent);
        (await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None)).Tip.Should().Be(commitCurrent);

        // A merge whose first parent is the OLD tip and second parent is the CURRENT one: a genuine
        // descendant of the current tip (merge-base --is-ancestor would call it a fast-forward), but
        // a --first-parent replay from it never walks through commitCurrent at all.
        string merge = await BuildCommitAsync(hub, "a.yaml", "v1\n", [commitA, commitCurrent]);
        RevList(hub, "--first-parent", merge).StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .Should().NotContain(commitCurrent, "the whole point of this shape is that first-parent replay skips the current tip");
        await SetRefAsync(hub, refName, merge);

        LedgerAppendOnlyFetchResult refused = await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None);

        refused.WasRefused.Should().BeTrue("the verified tip is not on the fetched commit's own first-parent chain");
        refused.Tip.Should().Be(commitCurrent);
    }

    [Fact]
    public async Task FetchAsync_ARemoteRefConfirmedGone_IsReportedMissing_AndTheLocalCopyIsKept()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);

        string commitA = await BuildCommitAsync(hub, "a.yaml", "v1\n", []);
        await SetRefAsync(hub, refName, commitA);
        await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None);

        DeleteHubRef(hub, refName);

        LedgerAppendOnlyFetchResult result = await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None);

        result.WasRefused.Should().BeTrue();
        result.Tip.Should().Be(commitA, "the local copy is kept and read rather than treated as though nothing was ever there");
        RevParse(reader, refName).StandardOutput.Trim().Should().Be(commitA, "the live ref is never deleted on a confirmed-missing remote ref");
    }

    [Fact]
    public async Task FetchAsync_NoVerifiedRefYet_SeedsFromTheLocalLiveRef()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);

        // The reader already has local content for this ref (as if it had been fetched before this
        // integrity check ever existed), and origin has since moved further ahead.
        string commitLocal = await BuildCommitAsync(reader, "a.yaml", "v1\n", []);
        await SetRefAsync(reader, refName, commitLocal);

        string commitOrigin = await BuildCommitAsync(hub, "a.yaml", "v2\n", []);
        await SetRefAsync(hub, refName, commitOrigin);

        LedgerAppendOnlyFetchResult result = await LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, refName, CancellationToken.None);

        result.WasRefused.Should().BeFalse();
        result.Tip.Should().Be(commitLocal, "the trust anchor seeds from what this node already had, never from a fresh fetch on the same call");
        RevParse(reader, $"refs/hall9k-verified/{VerifiedPathFor(refName)}").StandardOutput.Trim().Should().Be(commitLocal);
    }

    [Fact]
    public async Task FetchAsync_ConcurrentCalls_UseDistinctStagingNames_AndDoNotCollide()
    {
        string refName = UniqueTestRef();
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);

        string commitA = await BuildCommitAsync(hub, "a.yaml", "v1\n", []);
        await SetRefAsync(hub, refName, commitA);

        // A genuinely asynchronous runner (each git invocation actually yields to the thread pool,
        // rather than completing synchronously via Task.FromResult) — without this, the three
        // FetchAsync calls below run one after another regardless of Task.WhenAll, proving nothing
        // about concurrent staging names ever colliding (independent pre-PR review, cycle 1,
        // adversarial lens, low).
        ProcessRunner concurrentGitRunner = (_, arguments, workingDirectory, cancellationToken) =>
            Task.Run(
                () =>
                {
                    (int exitCode, string standardOutput, string standardError) =
                        LedgerTestRepo.RunGit(workingDirectory, [.. arguments]);
                    return new ProcessResult(exitCode, standardOutput, standardError);
                },
                cancellationToken);

        LedgerAppendOnlyFetchResult[] results = await Task.WhenAll(
            LedgerAppendOnlyRefFetcher.FetchAsync(concurrentGitRunner, reader, refName, CancellationToken.None),
            LedgerAppendOnlyRefFetcher.FetchAsync(concurrentGitRunner, reader, refName, CancellationToken.None),
            LedgerAppendOnlyRefFetcher.FetchAsync(concurrentGitRunner, reader, refName, CancellationToken.None));

        results.Should().OnlyContain(result => !result.WasRefused && result.Tip == commitA);
        ListRefs(reader, "refs/hall9k-staging/").StandardOutput.Should().BeEmpty("every staging ref is removed once its own call finishes");
    }

    [Fact]
    public async Task FetchAsync_OnTheMessagesPrefixRef_Throws()
    {
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);
        string messagesRef = $"{Hall9k.Connectors.Ledger.LedgerRefRegistry.MessagesPrefix.RefspecSource}{Guid.NewGuid():N}";

        Func<Task> fetch = () => LedgerAppendOnlyRefFetcher.FetchAsync(GitRunner, reader, messagesRef, CancellationToken.None);

        await fetch.Should().ThrowAsync<ArgumentException>(
            "a node's own outbox is squashed and force-pushed with a lease by design and must never be flagged by this check");
    }

    [Fact]
    public async Task FetchPrefixAsync_AGenuineFetchFailure_ThrowsWithNothingLiveMoved()
    {
        string prefix = LedgerRefRegistry.RegisterPrefix($"refs/hall9k/ledger/test-prefix-{Guid.NewGuid():N}/").RefspecSource;
        string hub = _repo.CreateHub();
        string reader = _repo.CloneNode(hub);
        string keptSuffix = Guid.NewGuid().ToString("N");

        // Established first, through a real, successful FetchPrefixAsync call — the standing state
        // the genuine failure below must leave completely untouched.
        string commitA = await BuildCommitAsync(hub, "a.yaml", "v1\n", []);
        await SetRefAsync(hub, $"{prefix}{keptSuffix}", commitA);
        await LedgerAppendOnlyRefFetcher.FetchPrefixAsync(GitRunner, reader, prefix, [keptSuffix], CancellationToken.None);
        string liveBefore = RevParse(reader, $"{prefix}{keptSuffix}").StandardOutput.Trim();
        string verifiedBefore = RevParse(reader, $"refs/hall9k-verified/{VerifiedPathFor(prefix)}{keptSuffix}").StandardOutput.Trim();

        // Breaks this reader's own remote so the NEXT fetch fails for real (an unreachable path,
        // never "nothing to fetch") — a genuine failure fetching the whole prefix must throw before
        // any per-suffix decision runs, so a partial failure never leaves one live or verified ref
        // moved while a sibling's own fetch never even landed (independent pre-PR review, cycle 1,
        // adversarial lens, low: the original version of this test pointed at a repository path that
        // did not exist at all, which never reaches a real fetch attempt and proves nothing about a
        // partial failure).
        LedgerTestRepo.RunGit(reader, "remote", "set-url", "origin", Path.Combine(Path.GetTempPath(), $"hall9k-unreachable-{Guid.NewGuid():N}"));

        string newSuffix = Guid.NewGuid().ToString("N");
        await SetRefAsync(hub, $"{prefix}{newSuffix}", commitA);
        Func<Task> fetch = () => LedgerAppendOnlyRefFetcher.FetchPrefixAsync(
            GitRunner, reader, prefix, [keptSuffix, newSuffix], CancellationToken.None);

        await fetch.Should().ThrowAsync<LedgerFetchFailedException>();
        RevParse(reader, $"{prefix}{keptSuffix}").StandardOutput.Trim().Should().Be(
            liveBefore, "the live ref an earlier, successful fetch already established must survive a later, unrelated failure untouched");
        RevParse(reader, $"refs/hall9k-verified/{VerifiedPathFor(prefix)}{keptSuffix}").StandardOutput.Trim().Should().Be(verifiedBefore);
        RevParse(reader, $"{prefix}{newSuffix}").ExitCode.Should().NotBe(
            0, "the suffix that only ever existed on origin must never land locally when the whole prefix fetch failed");
    }

    private static string VerifiedPathFor(string refName) => refName["refs/hall9k/".Length..];

    private static string UniqueTestRef() => LedgerRefRegistry.RegisterExact($"refs/hall9k/ledger/test-{Guid.NewGuid():N}").RefspecSource;

    private static readonly ProcessRunner GitRunner = (_, arguments, workingDirectory, cancellationToken) =>
    {
        (int exitCode, string standardOutput, string standardError) = LedgerTestRepo.RunGit(workingDirectory, [.. arguments]);
        return Task.FromResult(new ProcessResult(exitCode, standardOutput, standardError));
    };

    private static Task SetRefAsync(string repositoryPath, string refName, string commit) =>
        RunOrThrowAsync(repositoryPath, ["update-ref", refName, commit]);

    private static void DeleteHubRef(string repositoryPath, string refName) =>
        LedgerTestRepo.RunGit(repositoryPath, "update-ref", "-d", refName);

    private static (int ExitCode, string StandardOutput, string StandardError) RevParse(string repositoryPath, string reference) =>
        LedgerTestRepo.RevParseQuiet(repositoryPath, reference);

    private static (int ExitCode, string StandardOutput, string StandardError) RevList(
        string repositoryPath, string flag, string commit) =>
        LedgerTestRepo.RunGit(repositoryPath, "rev-list", flag, commit);

    private static (int ExitCode, string StandardOutput, string StandardError) ListRefs(string repositoryPath, string prefix) =>
        LedgerTestRepo.RunGit(repositoryPath, "for-each-ref", "--format=%(refname)", $"{prefix}*");

    private static async Task<string> BuildCommitAsync(
        string repositoryPath, string path, string content, IReadOnlyList<string> parents)
    {
        string tempIndex = Path.Combine(Path.GetTempPath(), $"h9k-fetcher-test-index-{Guid.NewGuid():N}");
        try
        {
            if (parents.Count > 0)
            {
                await RunOrThrowAsync(repositoryPath, ["read-tree", parents[0]], indexFile: tempIndex);
            }

            string blobId = (await RunCaptureOrThrowAsync(
                repositoryPath, ["hash-object", "-w", "--stdin"], indexFile: tempIndex, standardInput: content)).Trim();
            await RunOrThrowAsync(
                repositoryPath, ["update-index", "--add", "--cacheinfo", $"100644,{blobId},{path}"], indexFile: tempIndex);
            string treeId = (await RunCaptureOrThrowAsync(repositoryPath, ["write-tree"], indexFile: tempIndex)).Trim();

            List<string> arguments =
            [
                "-c", "user.name=Fetcher Test",
                "-c", "user.email=fetcher-test@hall9k.local",
                "commit-tree", treeId,
            ];
            foreach (string parent in parents)
            {
                arguments.Add("-p");
                arguments.Add(parent);
            }

            arguments.Add("-m");
            arguments.Add("test commit");
            return (await RunCaptureOrThrowAsync(repositoryPath, arguments)).Trim();
        }
        finally
        {
            try
            {
                File.Delete(tempIndex);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp index; nothing downstream reads it again.
            }
        }
    }

    private static Task RunOrThrowAsync(
        string repositoryPath, IReadOnlyList<string> arguments, string? indexFile = null) =>
        RunCaptureOrThrowAsync(repositoryPath, arguments, indexFile);

    private static async Task<string> RunCaptureOrThrowAsync(
        string repositoryPath, IReadOnlyList<string> arguments, string? indexFile = null, string? standardInput = null)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryPath);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (indexFile is not null)
        {
            process.StartInfo.Environment["GIT_INDEX_FILE"] = indexFile;
        }

        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {repositoryPath}: {error}");
        }

        return output;
    }
}
