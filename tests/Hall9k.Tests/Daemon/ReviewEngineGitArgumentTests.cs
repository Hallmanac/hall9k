using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Security review idea 6be68ee2, process-injection finding 2: <c>ReviewContext.BaseBranch</c> —
/// this run's own recorded <c>RunDispatched.BaseBranch</c>, a stacked child's parent branch — is
/// what <see cref="ReviewEngine"/>'s own stranded-delta read hands git as a fetch argument. Proven
/// over a fake <see cref="ProcessRunner"/> injected through the same <c>gitProcessRunner</c> seam
/// <see cref="ReviewEngine"/> already uses for its assessment-driven git calls, never a real
/// repository.
/// </summary>
public sealed class ReviewEngineGitArgumentTests
{
    private static ReviewEngine NewEngine(ProcessRunner gitProcessRunner) => new(
        store: null!,
        executor: null!,
        processManager: null!,
        verification: null!,
        Options.Create(new DaemonOptions()),
        NullLogger<ReviewEngine>.Instance,
        new LockOnlyWorktreeManager(),
        processRunner: null!,
        gitProcessRunner,
        stackedParents: null!,
        launchHold: null!,
        inspector: null!,
        closeout: null!);

    private static ReviewEngine.ReviewContext NewContext(string worktreePath, string baseBranch) => new(
        RunId: Guid.NewGuid(),
        TaskId: Guid.NewGuid(),
        Run: new RunDetails { WorktreePath = worktreePath, BaseBranch = baseBranch },
        Task: new TaskDetails(),
        Project: new ProjectDetails { RepositoryPath = "/repo", BaseBranch = "main" },
        PriorRulings: [],
        PriorHumanDirectedInteractions: [],
        PriorBoundaryApprovals: [],
        PriorHumanFixes: []);

    /// <summary>Reads only the lock <see cref="ReviewEngine.CaptureStrandedDeltaAsync"/> needs; every other member throws if touched.</summary>
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

    [Fact]
    public async Task CaptureStrandedDeltaAsync_puts_double_dash_before_a_legal_base_branch_on_the_fetch()
    {
        List<IReadOnlyList<string>> calls = [];
        ProcessRunner git = (fileName, arguments, workingDirectory, cancellationToken) =>
        {
            calls.Add(arguments);
            fileName.Should().Be("git");
            return Task.FromResult(arguments[0] switch
            {
                "fetch" => new ProcessResult(0, string.Empty, string.Empty),
                // Empty output: git cherry ran clean, nothing stranded — the simplest terminal reply.
                "cherry" => new ProcessResult(0, string.Empty, string.Empty),
                _ => throw new InvalidOperationException($"unexpected git subcommand: {arguments[0]}"),
            });
        };

        string worktreePath = Directory.CreateTempSubdirectory("hall9k-stranded-delta-").FullName;
        try
        {
            ReviewEngine engine = NewEngine(git);
            ReviewEngine.ReviewContext context = NewContext(worktreePath, "task/legitimate-parent-branch");

            ReviewEngine.StrandedDeltaCapture capture = await engine.CaptureStrandedDeltaAsync(context, CancellationToken.None);

            capture.CaptureFailed.Should().BeFalse();
            calls.Should().ContainSingle(call => call[0] == "fetch").Subject.Should().Equal(
                "fetch", "origin", "--", "task/legitimate-parent-branch");
        }
        finally
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }

    /// <summary>
    /// <c>--</c> alone does not stop a refspec-shaped value from being read as one — the predicate
    /// is the actual defence, proven here by a git fake that throws on any call: a hostile base
    /// branch must never reach the fetch, `--` or not.
    /// </summary>
    [Theory]
    [InlineData("+refs/heads/main:refs/heads/injected")]
    [InlineData("--upload-pack=x")]
    public async Task CaptureStrandedDeltaAsync_refuses_a_hostile_base_branch_without_ever_calling_git(string hostileBranch)
    {
        ProcessRunner git = (_, _, _, _) => throw new InvalidOperationException(
            "a hostile base branch must be refused before it ever reaches a git argument");

        string worktreePath = Directory.CreateTempSubdirectory("hall9k-stranded-delta-").FullName;
        try
        {
            ReviewEngine engine = NewEngine(git);
            ReviewEngine.ReviewContext context = NewContext(worktreePath, hostileBranch);

            ReviewEngine.StrandedDeltaCapture capture = await engine.CaptureStrandedDeltaAsync(context, CancellationToken.None);

            // Treated the same as any other unread delta: not confirmed empty, so the caller keeps
            // the worktree and branch around rather than deleting the one place an unread commit
            // could still live (AGENTS.md's never-guess rule).
            capture.CaptureFailed.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }
}
