using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.Integration;
using Marten;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// The second and third of the three daemon fetches of a run base branch —
/// <c>EnsureRebasedBeforeFinalPassAsync</c>'s own unstacked leg and
/// <c>DispatchRebaseRecoverySessionAsync</c> (security review idea 6be68ee2, process-injection
/// finding 2) — live in their own class rather than beside <see cref="ReviewEngineGitArgumentTests"/>'s
/// own git-fake-only tests, for two reasons neither of those tests needs to work around.
/// <list type="bullet">
/// <item>Both now check <c>EnsureCurrentGenerationAsync</c>'s live generation fence before this
/// task's own base-branch check runs (self-review finding: a stale generation must still stop the
/// run regardless of what its base branch looks like, so the fence has to run first), and that
/// fence reads a real store. A real <see cref="PostgresFixture"/>, left empty for both tests below,
/// is enough for it to read "no such run or task" and proceed.</item>
/// <item>Neither method reads the injected <c>gitProcessRunner</c> seam for its own fetch — both
/// still use a literal <c>ExternalProcess.RunnerWithDeadline</c>, deliberately: routing them
/// through the seam instead broke roughly thirty of <c>ReviewEngineTests</c>'s own tests, whose
/// shared engine-building helper defaults that seam to a "never invoked" fake and relies on these
/// same two methods reaching a REAL git binary against a real seeded origin (self-review finding,
/// round one). So the proof here is not "the fake runner sees no call" — there is no fake runner in
/// the path at all — it is that the refusal's own exact message is what a <see cref="ListLogger{T}"/>
/// or the run's own recorded <c>FailureReason</c> actually holds, which no other branch of either
/// method produces.</item>
/// </list>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ReviewEngineGitArgumentRebaseGateTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static ReviewEngine NewEngine(DocumentStore store, ILogger<ReviewEngine> logger) => new(
        store,
        executor: null!,
        processManager: null!,
        verification: null!,
        Options.Create(new DaemonOptions()),
        logger,
        worktrees: null!,
        processRunner: null!,
        gitProcessRunner: (_, _, _, _) => throw new InvalidOperationException(
            "neither method under test reads this seam for its own fetch — untouched means it stayed unread"),
        stackedParents: null!,
        launchHold: null!,
        inspector: null!,
        closeout: null!);

    [Theory]
    [InlineData("+refs/heads/main:refs/heads/injected")]
    [InlineData("--upload-pack=x")]
    public async Task EnsureRebasedBeforeFinalPassAsync_refuses_a_hostile_base_branch_without_ever_calling_git(
        string hostileBranch)
    {
        ListLogger<ReviewEngine> logger = new();
        string worktreePath = Directory.CreateTempSubdirectory("hall9k-final-pass-rebase-").FullName;
        try
        {
            ReviewEngine engine = NewEngine(postgres.Store, logger);
            ReviewEngine.ReviewContext context = new(
                RunId: DomainId.New(),
                TaskId: DomainId.New(),
                Run: new RunDetails { WorktreePath = worktreePath, BaseBranch = hostileBranch },
                Task: new TaskDetails(),
                Project: new ProjectDetails { RepositoryPath = "/repo", BaseBranch = "main" },
                PriorRulings: [],
                PriorHumanDirectedInteractions: [],
                PriorBoundaryApprovals: [],
                PriorHumanFixes: []);

            ReviewEngine.RebaseGateOutcome outcome = await engine.EnsureRebasedBeforeFinalPassAsync(
                context, new RunAggregate(), CancellationToken.None);

            // The same "not this run's fault" outcome a fetch or read failure on this path already
            // returns — closeout's own mechanical rebase still covers whatever staleness this
            // skipped rebase left behind.
            outcome.Should().Be(ReviewEngine.RebaseGateOutcome.Proceed);

            // Proceed alone is not proof: RebasePreflightAsync's own worktree/branch/status checks
            // return the identical outcome for reasons that have nothing to do with this task. The
            // refusal's own exact wording is what only this check produces — no other branch of
            // this method ever writes "is not a legal branch name".
            logger.Lines.Should().Contain(line => line.Contains("is not a legal branch name"));
        }
        finally
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }

    [Theory]
    [InlineData("+refs/heads/main:refs/heads/injected")]
    [InlineData("--upload-pack=x")]
    public async Task DispatchRebaseRecoverySessionAsync_refuses_a_hostile_base_branch_without_ever_calling_git(
        string hostileBranch)
    {
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();
        string worktreePath = Directory.CreateTempSubdirectory("hall9k-rebase-recovery-").FullName;
        try
        {
            ReviewEngine engine = NewEngine(postgres.Store, NullLogger<ReviewEngine>.Instance);
            ReviewEngine.ReviewContext context = new(
                RunId: runId,
                TaskId: taskId,
                Run: new RunDetails { WorktreePath = worktreePath, BaseBranch = "main" },
                Task: new TaskDetails(),
                Project: new ProjectDetails { RepositoryPath = "/repo", BaseBranch = "main" },
                PriorRulings: [],
                PriorHumanDirectedInteractions: [],
                PriorBoundaryApprovals: [],
                PriorHumanFixes: []);

            // The hostile value lives on run.BaseBranch — DispatchRebaseRecoverySessionAsync reads
            // run.BaseBranchOr(context.Project.BaseBranch), never context.BaseBranch — for the same
            // reason RunLauncher's own doc names: a checkpoint-originated track can have moved the
            // run's recorded base since context.Run was snapshotted.
            RunAggregate run = new();
            run.Apply(new RunDispatched(
                runId, taskId, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), worktreePath,
                "task/some-branch", ExecutorMode.Subscription, DateTimeOffset.UtcNow, BaseBranch: hostileBranch));

            bool dispatched = await engine.DispatchRebaseRecoverySessionAsync(
                context, run, humanGuidance: null, assessmentGuidance: null, baseCommit: null,
                precedesFirstReviewCycle: false, CancellationToken.None);

            dispatched.Should().BeFalse("a hostile base branch must never earn a dispatched rebase-recovery session");

            await using IQuerySession query = postgres.Store.QuerySession();
            RunDetails? run2 = await query.LoadAsync<RunDetails>(runId, CancellationToken.None);
            run2.Should().NotBeNull("FailAsync records the refusal on the run, even with no task stream behind it");
            run2!.State.IsTerminal.Should().BeTrue("the run is failed rather than left stuck with no explanation");
            run2.FailureReason.Should().Contain("is not a legal branch name",
                "the recorded reason names this exact check, not some other cause the worktree-missing branch would also produce");
        }
        finally
        {
            Directory.Delete(worktreePath, recursive: true);
        }
    }
}
