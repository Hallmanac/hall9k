using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Security review idea 6be68ee2, process-injection finding 2: a stacked child's parent branch
/// (a local parent's own branch, or a remote parent's declared head) reaches
/// <see cref="StackedParentWatch"/> without ever passing through <c>BranchNameTemplate.Render</c>,
/// so it is checked at the point it becomes a git argument, over a fake <see cref="ProcessRunner"/>
/// rather than a real repository — the same convention <c>ForceWithLeasePusherTests</c> already
/// established for this file's own kind of seam.
/// </summary>
public sealed class StackedParentWatchGitArgumentTests
{
    private const string RepositoryPath = "/repo";

    /// <summary>Every git call this test's fakes receive, for asserting the exact argv git would see.</summary>
    private static ProcessRunner RecordingGit(List<IReadOnlyList<string>> calls, Func<IReadOnlyList<string>, ProcessResult> respond) =>
        (fileName, arguments, workingDirectory, cancellationToken) =>
        {
            calls.Add(arguments);
            fileName.Should().Be("git");
            return Task.FromResult(respond(arguments));
        };

    [Fact]
    public async Task ReadRemoteBranchHeadAsync_puts_double_dash_before_a_legal_branch_on_the_fetch()
    {
        List<IReadOnlyList<string>> calls = [];
        ProcessRunner git = RecordingGit(calls, arguments => arguments[0] switch
        {
            "fetch" => new ProcessResult(0, string.Empty, string.Empty),
            "rev-parse" => new ProcessResult(0, "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2\n", string.Empty),
            _ => throw new InvalidOperationException($"unexpected git subcommand: {arguments[0]}"),
        });

        StackedParentWatch.ParentHeadRead read = await StackedParentWatch.ReadRemoteBranchHeadAsync(
            git, RepositoryPath, "task/legitimate-branch", CancellationToken.None);

        read.Lookup.Should().Be(StackedParentWatch.ParentHeadLookup.Resolved);
        calls.Should().ContainSingle(call => call[0] == "fetch").Subject.Should().Equal(
            "fetch", "origin", "--", "task/legitimate-branch");
    }

    /// <summary>
    /// <c>--</c> alone does not stop a refspec-shaped value (<c>+refs/heads/main:refs/heads/injected</c>
    /// still reads as a refspec after it) — the predicate is the actual defence, proven here by a git
    /// fake that throws on any call at all: a hostile value must never reach the fetch, `--` or not.
    /// </summary>
    [Theory]
    [InlineData("+refs/heads/main:refs/heads/injected")]
    [InlineData("--upload-pack=x")]
    [InlineData("task/has space")]
    public async Task ReadRemoteBranchHeadAsync_refuses_a_hostile_branch_without_ever_calling_git(string hostileBranch)
    {
        ProcessRunner git = (_, _, _, _) => throw new InvalidOperationException(
            "a hostile branch must be refused before it ever reaches a git argument");

        StackedParentWatch.ParentHeadRead read = await StackedParentWatch.ReadRemoteBranchHeadAsync(
            git, RepositoryPath, hostileBranch, CancellationToken.None);

        read.Lookup.Should().Be(StackedParentWatch.ParentHeadLookup.ReadFailed);
        read.Detail.Should().Contain("not a legal branch name");
    }

    /// <summary>Reads only the head <see cref="StackedParentWatch.ObserveAsync"/> needs; every other member throws if touched.</summary>
    private sealed class LockOnlyWorktreeManager : IWorktreeManager
    {
        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class NoOpLock : IAsyncDisposable
    {
        public static readonly NoOpLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Never actually called in this scenario (the parent has not merged) — throws if it ever is.</summary>
    private sealed class ThrowingRemoteParentReader : IRemoteParentReader
    {
        public Task<RemoteParentRead> ReadAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// The resolver-level proof: a hostile <c>RunDispatched.BaseBranch</c> — a stacked child's
    /// declared parent branch — never reaches a fetch at all, and <see cref="StackedParentWatch.ObserveAsync"/>
    /// degrades to <see cref="StackedParentVerdict.Unobservable"/> rather than throwing or guessing.
    /// The fake git below answers every OTHER call (the pull request's own immutable head, which is
    /// int-derived and therefore safe) so the pipeline runs to a real terminal verdict instead of
    /// stopping at the first unhandled call, which would prove nothing about this one refusal.
    /// </summary>
    [Fact]
    public async Task ObserveAsync_refuses_a_hostile_base_branch_and_reports_unobservable_rather_than_guessing()
    {
        const string hostileBaseBranch = "+refs/heads/main:refs/heads/injected";
        List<IReadOnlyList<string>> calls = [];
        ProcessRunner git = RecordingGit(calls, arguments => arguments[0] switch
        {
            "fetch" => new ProcessResult(0, string.Empty, string.Empty),
            "rev-parse" => new ProcessResult(0, "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2\n", string.Empty),
            "merge-base" => new ProcessResult(1, string.Empty, string.Empty),
            "update-ref" => new ProcessResult(0, string.Empty, string.Empty),
            _ => throw new InvalidOperationException($"unexpected git subcommand: {arguments[0]}"),
        });

        StackedParentWatch watch = new(
            new LockOnlyWorktreeManager(), new ThrowingRemoteParentReader(), NullLogger<StackedParentWatch>.Instance, git);

        ProjectDetails project = new() { RepositoryPath = RepositoryPath, BaseBranch = "main" };
        StackedParentDeclaration parent = new(
            TaskId: null, PullRequestNumber: 42, RemoteParentState.Open, RemoteHeadBranch: "irrelevant", RemoteBaseBranch: "main");
        RunDetails childRun = new() { Id = Guid.NewGuid(), Branch = "task/child", BaseBranch = hostileBaseBranch };

        StackedParentObservation observation =
            await watch.ObserveAsync(query: null!, project, parent, childRun, CancellationToken.None);

        observation.Verdict.Should().Be(StackedParentVerdict.Unobservable);
        calls.Should().NotContain(
            call => call.Contains(hostileBaseBranch),
            "the hostile branch must never reach a git argument, fetch or otherwise");
    }
}
