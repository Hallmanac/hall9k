using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.PrReviewPreflight;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Auto-pr-review's second trigger (idea 2f079bcd): a GitHub comment mentioning the install's own
/// login on a pull request in a registered project mints or extends the same pr-review task a
/// review request does. Every test here scripts its own repository, distinct from every other test
/// file's, for the reason <see cref="PrReviewTaskEngineTests"/>'s own class doc gives: this suite
/// shares one Postgres database across its test methods, and the dedup queries key on the
/// canonical external reference alone, unscoped by project.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class AutoPrReviewMentionEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------
    // Fakes and seeding — deliberately this file's own rather than shared with
    // PrReviewTaskEngineTests, mirroring how RunLauncherTests and PrReviewTaskEngineTests each
    // already roll their own rather than share a fixture library.
    // -------------------------------------------------------------------------------------

    private static bool IsRepositoryHostRead(IReadOnlyList<string> arguments) =>
        arguments.Count > 1 && arguments[0] == "repo" && arguments[1] == "view";

    /// <summary>
    /// The membership gate's own visibility read (security review idea 6be68ee2, finding 1):
    /// <c>gh repo view &lt;repo&gt; --json isPrivate</c>, once per project per sweep. Shaped like
    /// <see cref="IsRepositoryHostRead"/>'s own <c>repo view</c>, so every scripted runner below
    /// answers it before that refusal ever sees it. <see cref="MentionScriptedGh"/> defaults its own
    /// <c>isPrivate</c> parameter to true, keeping every existing mention test's own behaviour
    /// exactly what it was before this gate existed; the one test exercising the gate's own park
    /// passes <c>isPrivate: false</c> and a <c>pullRequestAuthor</c> instead.
    /// </summary>
    private static bool IsVisibilityRead(IReadOnlyList<string> arguments) => arguments.Contains("isPrivate");

    private static bool AsksAbout(IReadOnlyList<string> arguments, string repository)
    {
        int repoIndex = arguments.ToList().IndexOf("--repo");
        return repoIndex >= 0
            && repoIndex + 1 < arguments.Count
            && string.Equals(arguments[repoIndex + 1], repository, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A gh that answers for exactly one repository: no review-requested pull requests at all
    /// (isolating the mentions search), one open pull request mentioning <paramref name="login"/>,
    /// and <paramref name="comments"/> as the comments <c>FindMentionCommentsAsync</c>'s own
    /// GraphQL query would find on it. The two GraphQL shapes (the review-request timeline and the
    /// mention comments) are told apart by a substring of the query text itself, since both travel
    /// as a plain <c>-f query=...</c> argument.
    /// </summary>
    private static ProcessRunner MentionScriptedGh(
        string repository, int number, string login,
        IReadOnlyList<(string Id, string Author, string Body, DateTimeOffset CreatedAt)> comments,
        bool isPrivate = true, (string Login, long AccountId, string Association)? pullRequestAuthor = null,
        long? commentAuthorAccountId = null) =>
        (fileName, arguments, _, _) =>
        {
            if (IsVisibilityRead(arguments))
            {
                return Task.FromResult(new ProcessResult(0, $$"""{"isPrivate":{{(isPrivate ? "true" : "false")}}}""", string.Empty));
            }

            if (IsRepositoryHostRead(arguments))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "no repository this test knows"));
            }

            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, login + "\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                bool isMentionsSearch = arguments.Any(argument => argument.StartsWith("mentions:", StringComparison.Ordinal));
                string listJson = isMentionsSearch && AsksAbout(arguments, repository)
                    ? $$"""
                        [{"number":{{number}},"url":"https://github.com/{{repository}}/pull/{{number}}",
                          "title":"Add rate limiting","body":"no links here"}]
                        """
                    : "[]";
                return Task.FromResult(new ProcessResult(0, listJson, string.Empty));
            }

            if (arguments.Contains("view"))
            {
                int repoIndex = arguments.ToList().IndexOf("--repo");
                string requestRepository = repoIndex >= 0 && repoIndex + 1 < arguments.Count
                    ? arguments[repoIndex + 1]
                    : repository;
                string prJson = $$"""
                    {"number":{{number}},"title":"Add rate limiting","body":"no links here","state":"OPEN",
                     "url":"https://github.com/{{requestRepository}}/pull/{{number}}","baseRefName":"main"}
                    """;
                return Task.FromResult(new ProcessResult(0, prJson, string.Empty));
            }

            if (arguments.Contains("graphql") && arguments.Any(argument => argument.Contains("reviewThreads", StringComparison.Ordinal)))
            {
                string commentAuthorAccountIdField = commentAuthorAccountId is { } accountId
                    ? $",\"databaseId\":{accountId}"
                    : string.Empty;
                string nodes = string.Join(",", comments.Select(comment => $$"""
                    {"id":"{{comment.Id}}","author":{"login":"{{comment.Author}}"{{commentAuthorAccountIdField}}},
                     "body":"{{comment.Body.Replace("\"", "\\\"", StringComparison.Ordinal)}}",
                     "url":"https://github.com/{{repository}}/pull/{{number}}#issuecomment-{{comment.Id}}",
                     "createdAt":"{{comment.CreatedAt:yyyy-MM-ddTHH:mm:ss}}Z"}
                    """));
                string authorField = pullRequestAuthor is { } author
                    ? "\"author\":{\"login\":\"" + author.Login + "\",\"databaseId\":" + author.AccountId + "},"
                      + "\"authorAssociation\":\"" + author.Association + "\","
                    : string.Empty;
                string json =
                    "{\"data\":{\"repository\":{\"pullRequest\":{" + authorField
                    + "\"comments\":{\"nodes\":[" + nodes
                    + "]},\"reviewThreads\":{\"nodes\":[]},\"reviews\":{\"nodes\":[]}}}}}";
                return Task.FromResult(new ProcessResult(0, json, string.Empty));
            }

            // The review-requested timeline query (IsGenuineReRequestAsync's own read, or the
            // withdrawal sweep's) — never exercised by these mention-only tests, so an honestly
            // empty timeline rather than a crash.
            return Task.FromResult(new ProcessResult(
                0, """{"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[]}}}}}""", string.Empty));
        };

    private static async Task SeedProjectAsync(
        DocumentStore store, NodeContext node, Guid projectId, string name, string repository,
        DateTimeOffset registeredAt, DateTimeOffset adoptedAt, Optional<AutoPrReviewSpeed> speed,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new AutoPrReviewDefaultAdoption { Id = node.NodeId, AdoptedAt = adoptedAt });
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), name, $"/tmp/{name}-repo",
            new Uri($"https://github.com/{repository}"), "main", registeredAt);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
        if (speed.HasValue)
        {
            ProjectAggregate project = new();
            project.Apply(registered);
            session.Events.Append(projectId, ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
                Optional<IReadOnlyList<ContextLink>>.None, registeredAt, node.OwnerId, autoPrReview: speed));
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds a completed, safe pull-request review pre-flight (idea 6be68ee2, finding 1, phase
    /// one) for <paramref name="taskId"/> so a follow-up dispatch in this file's own tests clears
    /// the gate exactly as a real pre-flight already having run would — every scripted <c>gh pr
    /// view</c> fixture in <see cref="MentionScriptedGh"/> carries no <c>headRefOid</c> field, so
    /// <see cref="Connectors.WorkItems.GitHubPullRequestProvider"/> reads it as empty here too.
    /// </summary>
    private static async Task SeedSafePrReviewPreflightAsync(
        DocumentStore store, Guid taskId, Guid nodeId, CancellationToken cancellationToken)
    {
        // DateTimeOffset.UtcNow, not this file's own fixed Now: RunLauncher's own production
        // dispatch records a pre-flight at the real current time, and RunLauncher.LaunchAsync
        // always reads the LATEST pre-flight by DispatchedAt — a seed timestamped at this file's
        // own fixed (and much older) Now would sort behind an already-dispatched pre-flight from
        // an earlier poll in the same test, rather than superseding it.
        DateTimeOffset seededAt = DateTimeOffset.UtcNow;
        Guid preflightRunId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        // DispatchingRunId is read only by RunSupervisor's own claim-identity guard, never by the
        // read-side gate this seed exists to satisfy (EnsurePrReviewPreflightSafeAsync) — an
        // arbitrary id stands in for it here, since none of this file's callers claim through the
        // supervisor at seed time.
        session.Events.StartStream(preflightRunId, new PrReviewPreflightDispatched(
            preflightRunId, taskId, DomainId.New(), nodeId, "claude-opus-5-5", string.Empty, [], seededAt));
        session.Events.Append(preflightRunId, new PrReviewPreflightCompleted(
            preflightRunId, Safe: true, "safe", "seeded for test", seededAt));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A live pr-review task already waiting on its pull request (AwaitingAuthor) — the shape a mention attaches to instead of minting a second task.</summary>
    private static async Task<Guid> SeedWaitingReviewAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-2), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddHours(-2));
        task.Apply(claimed);
        PullRequestReviewFollowThroughOpened opened = TaskDecider.OpenPrReviewFollowThrough(
            task, runId, $"https://github.com/{repository}/pull/{number}", headSha: null, Now.AddHours(-1));
        task.Apply(opened);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed, opened]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private sealed class NoOpLock : IAsyncDisposable
    {
        public static readonly NoOpLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Prepares a workspace without touching git; the launcher only needs a path and a branch.</summary>
    private sealed class StubWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Worktree(
                Path.Combine(Path.GetTempPath(), $"hall9k-wt-{request.RunId:N}"),
                $"pr/{request.PullRequestNumber}", $"pr/{request.PullRequestNumber}"));

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "nothing here is a real repository"));

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
    }

    /// <summary>Records the spawn request instead of starting anything.</summary>
    private sealed class CapturingExecutor : IExecutor
    {
        public AgentSpawnRequest? Request { get; private set; }

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new SpawnedAgent(4242, Now));
        }
    }

    private sealed class RefusingExecutor(string why) : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(why);
    }

    private sealed class RefusingWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests: no mention here should ever dispatch a follow-up.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
    }

    private sealed class RefusingInspector : IPullRequestInspector
    {
        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached by these tests.");

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached by these tests.");

        public Task<bool> DeletesHeadBranchOnMergeAsync(
            string repositoryPath, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached by these tests.");
    }

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node)
    {
        FakeProcessManager processes = new();
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes);
        LaunchHoldEngine launchHold = new(store, NullLogger<LaunchHoldEngine>.Instance);
        // Every run this factory's ReviewEngine drives is a fresh, non-follow-up run, so its own
        // already-merged guard (RunAggregate.IsFollowUp) always returns before ever reaching
        // either dependency below — both exist solely to satisfy the constructor, the same
        // RefusingInspector NewLauncher's own RunLauncher already uses for the identical reason.
        RefusingInspector reviewInspector = new();
        CloseoutEngine unusedCloseout = new(
            store, node, new DaemonConnection("unused"), reviewInspector, new RefusingWorktreeManager(),
            new StackedParentWatch(new RefusingWorktreeManager(), new NoOpRemoteParentReader(), NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.NeverInvoked(), FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            RecordingProcessRunner.NeverInvoked(),
            new StackedParentWatch(
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), new NoOpRemoteParentReader(),
                NullLogger<StackedParentWatch>.Instance),
            launchHold, reviewInspector, unusedCloseout);
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), launchHold,
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        SpikeEngine spike = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), node,
            Options.Create(new DaemonOptions()), NullLogger<SpikeEngine>.Instance);
        PrimarySessionResumer primarySessionResumer = new(
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())),
            Options.Create(new DaemonOptions()));
        return new RunSupervisor(store, node, processes, verification, review, prReview, spike,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            primarySessionResumer, launchHold, Options.Create(new DaemonOptions()), NullLogger<RunSupervisor>.Instance);
    }

    /// <summary>
    /// A real <see cref="RunLauncher"/> with the worktree manager and executor swapped for the
    /// fakes a test needs — refusing ones for a test that must never actually dispatch, capturing
    /// ones for a test asserting a follow-up really was dispatched.
    /// </summary>
    private RunLauncher NewLauncher(
        DocumentStore store, NodeContext node, IWorktreeManager worktrees, IExecutor executor, ProcessRunner gh)
    {
        RefusingInspector inspector = new();
        CloseoutEngine closeout = new(
            store, node, new DaemonConnection(postgres.ConnectionString), inspector, new RefusingWorktreeManager(),
            new StackedParentWatch(new RefusingWorktreeManager(), new NoOpRemoteParentReader(), NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner, FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);
        BlockerContextAssembler blockerContext = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, new FakeProcessManager(), Options.Create(new DaemonOptions())),
            new FakeProcessManager(), Options.Create(new DaemonOptions()), NullLogger<BlockerContextAssembler>.Instance);
        return new RunLauncher(
            store, worktrees, executor, NewSupervisor(store, node), blockerContext, inspector, closeout,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance), gh,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);
    }

    /// <summary>
    /// A fleet snapshot naming one declared member account plus a second member with no
    /// declaration at all (a fleet on a version before v0.10.54, or one not restarted since), for
    /// the membership gate (security review idea 6be68ee2, finding 1) — the identical
    /// <see cref="EnrolledNodeSnapshots"/> the message sweep would have written, built directly
    /// rather than through a real chain read, the same shortcut <c>PrReviewTaskEngineTests</c>'s
    /// own <c>FleetOfThisNodeAnd</c> takes for the mint-hold tests.
    /// </summary>
    private static EnrolledNodeSnapshots MemberSnapshot(Guid projectId, NodeContext node, long memberAccountId)
    {
        EnrolledNodeSnapshots snapshots = new();
        ProjectMember member = new("owner-root", MembershipRole.Owner, Now);
        ProjectMember undeclaredMember = new("other-root", MembershipRole.Member, Now);
        TrustedOwner owner = new("owner-root", "ssh-ed25519 AAAAFAKE root", [], RootNodeId: node.NodeId.ToString());
        TrustedOwner undeclaredOwner = new(
            "other-root", "ssh-ed25519 AAAAFAKE other", [], RootNodeId: Guid.NewGuid().ToString());
        Dictionary<string, NodeGitHubDeclaration> declarations = new()
        {
            [node.NodeId.ToString()] = new NodeGitHubDeclaration(
                node.NodeId.ToString(), "owner-root", new DeclaredGitHubAccount(memberAccountId, "brian"), Now),
        };
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["owner-root"] = owner, ["other-root"] = undeclaredOwner },
            [member, undeclaredMember], NodeDeclarations: declarations);
        snapshots.Record(projectId, chain, "owner-root");
        return snapshots;
    }

    /// <summary>A node id that sorts below every id this platform will ever mint — <c>PrReviewTaskEngineTests</c>'s own <c>LowerRankedPeer</c>, this file's own copy for the identical reason every other fixture here is its own.</summary>
    private static readonly Guid LowerRankedPeer = Guid.Parse("00000000-0000-7000-8000-000000000001");

    /// <summary>
    /// A fleet naming this node and one lower-ranked peer — the identical shortcut
    /// <c>PrReviewTaskEngineTests</c>'s own <c>FleetOfThisNodeAnd</c> takes for its own mint-hold
    /// tests, this file's own copy since every fixture here is deliberately its own.
    /// </summary>
    private static EnrolledNodeSnapshots FleetOfThisNodeAnd(Guid projectId, NodeContext node, Guid peer)
    {
        EnrolledNodeSnapshots snapshots = new();
        snapshots.Record(
            projectId,
            new TrustChain(
                new Dictionary<string, TrustedOwner>
                {
                    ["owner-root"] = new TrustedOwner(
                        "owner-root", "ssh-ed25519 AAAAFAKE root",
                        [
                            new TrustedNode(peer.ToString(), "ssh-ed25519 AAAAFAKEpeer test", "peer-fingerprint", Now),
                            new TrustedNode(node.NodeId.ToString(), "ssh-ed25519 AAAAFAKEself test", "self-fingerprint", Now),
                        ]),
                },
                []),
            "owner-root");
        return snapshots;
    }

    /// <summary>An auto-created pr-review task (WasAutoPrReviewCreated) for the hourly mint cap's own query — no Publish or Assign needed, since the cap only ever reads AddedAt and the external reference.</summary>
    private static async Task SeedAutoCreatedTaskAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number, DateTimeOffset addedAt,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), addedAt, node.OwnerId);
        PullRequestReviewAssignmentObserved observed = new(
            taskId, $"https://github.com/{repository}/pull/{number}", "brian", "someone", addedAt, addedAt);

        session.Events.StartStream<TaskAggregate>(taskId, [added, observed]);
        await session.SaveChangesAsync(cancellationToken);
    }

    private const string TeammateRoot = "2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>This install's owner root, claimed on first use: <see cref="NodeBootstrapSeed"/> shares one owner across the class, so a root already claimed by an earlier test is read back rather than claimed again.</summary>
    private static async Task<string> EnsureOwnRootAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(node.OwnerId, token: cancellationToken))!;
        if (!string.IsNullOrEmpty(owner.RootFingerprint))
        {
            return owner.RootFingerprint;
        }

        const string ownRoot = "1111111111111111111111111111111111111111111111111111111111111111";
        session.Events.Append(node.OwnerId, OwnerDecider.ClaimRoot(owner, ownRoot, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);
        return ownRoot;
    }

    /// <summary>
    /// A live pr-review task of another owner's, held by that owner's node and waiting on its pull
    /// request: the shape <see cref="SeedWaitingReviewAsync"/> seeds for this install, replicated in
    /// from a teammate, and the shape AgelessRx/arx-platform#2166 had when a teammate's install
    /// claimed it.
    /// </summary>
    private static async Task<Guid> SeedTeammatesWaitingReviewAsync(
        DocumentStore store, Guid projectId, string repository, int number, CancellationToken cancellationToken)
    {
        Guid teammateOwnerId = DomainId.New();
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-6),
            teammateOwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-6), teammateOwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(
            task, teammateOwnerId, [], Now.AddHours(-6), teammateOwnerId, assignedOwnerRootFingerprint: TeammateRoot);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(
            task, DomainId.New(), teammateOwnerId, runId, Now.AddHours(-6), ownerRootFingerprint: TeammateRoot);
        task.Apply(claimed);
        PullRequestReviewFollowThroughOpened opened = TaskDecider.OpenPrReviewFollowThrough(
            task, runId, $"https://github.com/{repository}/pull/{number}", headSha: null, Now.AddHours(-5));
        task.Apply(opened);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed, opened]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    // -------------------------------------------------------------------------------------
    // The tests themselves.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mention_on_a_pull_request_with_no_live_task_mints_a_fresh_pr_review_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-mint-test";
        const int number = 4201;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-mint", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what do you think of this approach?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
        minted.Type.Should().Be(TaskType.PrReview);
        minted.LatestMentionCommentId.Should().Be("IC_1");
        minted.LatestMentionAuthorLogin.Should().Be("ryan");
        // Task 7ae690f5: a fresh mention mint carries MintedTask true, so this task now counts as
        // auto-pr-review's own for the duplicate-convergence sweep and the hourly mint cap query,
        // exactly as a review-requested mint always has.
        minted.WasAutoPrReviewCreated.Should().BeTrue();

        TaskDetails details = (await query.LoadAsync<TaskDetails>(minted.Id, cts.Token))!;
        details.LatestMentionBody.Should().Be("@brian what do you think of this approach?");
        details.LatestMentionCreatedAt.Should().Be(Now.AddMinutes(5));

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.TaskCreated);
        observed.TaskId.Should().Be(minted.Id);
    }

    /// <summary>
    /// The membership gate's own park (security review idea 6be68ee2, finding 1): a mention on a
    /// public repository, with the COMMENT's own author not among the project's declared members,
    /// still mints the pr-review task through the identical Add and Publish <see cref="TaskDecider"/>
    /// sequence a member's own mention uses — but never Assign, so the task sits Published with no
    /// claim and no run ever launches. <see cref="RefusingWorktreeManager"/> and
    /// <see cref="RefusingExecutor"/> both prove that structurally: either one firing would fail
    /// this test outright (no git, no dispatch). The pull request's own author is a declared member
    /// here, proof that a member's own pull request still parks when a stranger's own comment tags
    /// the install on it (independent pre-PR review, cycle 3, conformance lens: a stranger's comment
    /// on a member's pull request is exactly as unattended-unsafe as a stranger's own pull request
    /// is, and the gate now checks both authors, parking on either one failing). The park card names
    /// the pull request's own author — GitHub's own reading of it, identical to a review-requested
    /// park's own card — even though this particular park was actually caused by the tagging
    /// comment's own author failing the gate, not the pull request's.
    /// </summary>
    [Fact]
    public async Task A_mention_from_a_non_member_on_a_public_repository_mints_published_and_unassigned()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-park-test";
        const int number = 4301;
        const long memberAccountId = 111;
        const long strangerAccountId = 999;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-park", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what do you think of this approach?", Now.AddMinutes(5))],
            isPrivate: false, pullRequestAuthor: ("brian", memberAccountId, "OWNER"),
            commentAuthorAccountId: strangerAccountId);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("a parked task is never launched"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            enrolledNodes: MemberSnapshot(projectId, node, memberAccountId), clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
        minted.Type.Should().Be(TaskType.PrReview);
        minted.State.Should().Be(TaskState.Published, "Add and Publish land, but the membership gate refuses Assign");
        minted.PrReviewGateParked.Should().BeTrue();
        minted.PrReviewGateParkedAuthorAccountId.Should().Be(
            memberAccountId,
            "the park card names the pull request's own author, GitHub's own reading of it, even though this "
            + "park was actually caused by the tagging comment's own author (ryan) failing the gate");
        minted.PrReviewGateParkedAuthorLogin.Should().Be("brian");
        minted.PrReviewGateParkedTitle.Should().Be(
            "Add rate limiting", "the card names the pull request's own title, never the platform-authored objective");
        minted.PrReviewGateParkedIsPrivate.Should().BeFalse();
        minted.PrReviewGateParkedMemberAccountIds.Should().BeEquivalentTo([memberAccountId],
            "the card names the project's own declared member ids beside the author's, so a "
            + "deleted-and-recreated account is diagnosable");
        minted.PrReviewGateParkedMembersWithoutDeclaredAccount.Should().ContainSingle(
            "a fleet not yet on v0.10.54 (or not restarted since) is named on the card, not silently dropped");

        (await query.LoadAsync<TaskLease>(minted.Id, cts.Token)).Should().BeNull("an unassigned task was never claimed");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.TaskCreatedParked);
        observed.TaskId.Should().Be(minted.Id);
    }

    /// <summary>
    /// The mirror image of the park above, and the scenario the membership gate used to miss
    /// entirely (independent pre-PR review, cycle 3, conformance lens): a stranger opens the pull
    /// request, and a declared hall9k team member tags the install in a comment on it. Before the
    /// gate checked the pull request's own author too, this minted Published AND Assigned — an
    /// unattended run dispatched straight into a checkout the stranger controls, on the strength of
    /// who commented rather than what is actually under review. <see cref="RefusingWorktreeManager"/>
    /// and <see cref="RefusingExecutor"/> prove structurally that no dispatch happens now.
    /// </summary>
    [Fact]
    public async Task A_mention_from_a_member_on_a_non_members_public_pull_request_mints_published_and_unassigned()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-stranger-pr-test";
        const int number = 4302;
        const long memberAccountId = 111;
        const long strangerAccountId = 888;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-stranger-pr", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_2", "carol", "@brian take a look", Now.AddMinutes(5))],
            isPrivate: false, pullRequestAuthor: ("mallory", strangerAccountId, "NONE"),
            commentAuthorAccountId: memberAccountId);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("a parked task is never launched"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            enrolledNodes: MemberSnapshot(projectId, node, memberAccountId), clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
        minted.State.Should().Be(
            TaskState.Published,
            "the pull request's own author is not a declared member, so the gate parks even though a "
            + "member tagged the install");
        minted.PrReviewGateParked.Should().BeTrue();
        minted.PrReviewGateParkedAuthorAccountId.Should().Be(strangerAccountId, "the pull request's own author");
        minted.PrReviewGateParkedAuthorLogin.Should().Be("mallory");

        (await query.LoadAsync<TaskLease>(minted.Id, cts.Token)).Should().BeNull("an unassigned task was never claimed");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_2"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.TaskCreatedParked);
        observed.TaskId.Should().Be(minted.Id);
    }

    /// <summary>
    /// A fresh mint's own primary session must be told about the tagged comment too, not only the
    /// task's own stream (idea 2f079bcd, decision 3): the findings report it produces is the ONE
    /// place a mint-triggered task's "You were asked" section can come from, since no separate
    /// follow-up lap ever runs for a task that had no live coverage to attach to.
    /// <para>
    /// The pull-request review pre-flight (idea 6be68ee2, finding 1, phase one) sits ahead of that
    /// primary session now: "Now speed" still dispatches immediately, but what it dispatches first
    /// is the pre-flight, never the primary session, on the very first poll. This test reaches the
    /// primary session by simulating what a safe verdict's own release does — re-invoking the same
    /// claim once a pre-flight is on record — since it wires no <c>DispatchEngine</c> of its own to
    /// pick the released task back up the way the daemon's real dispatch loop would.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_fresh_mint_from_a_mention_asks_its_primary_session_to_answer_the_comment_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-mint-prompt-test";
        const int number = 4208;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-mint-prompt", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Now), cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does this handle the empty-list case?", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        RunLauncher launcher = NewLauncher(store, node, new StubWorktreeManager(), executor, gh);
        AutoPrReviewEngine engine = new(
            store, node, launcher, gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance),
            NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        executor.Request.Should().NotBeNull("Now speed still launches immediately, but its first dispatch is the pre-flight gate");
        executor.Request!.Prompt.Should().Contain(
            PrReviewPreflightVerdictParser.Marker,
            "the pre-flight gate runs before any checkout, so the first dispatch is never the primary session");

        await using IQuerySession query = store.QuerySession();
        TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
        TaskDetails claimed = (await query.LoadAsync<TaskDetails>(minted.Id, cts.Token))!;

        // A safe verdict releases the task for the daemon's ordinary dispatch loop to reclaim and
        // relaunch — simulated here by re-invoking the same still-current claim directly. "Now
        // speed" claims through the sentinel path, which never writes a TaskLease document, so the
        // still-current generation is read off the task's own stream instead.
        await SeedSafePrReviewPreflightAsync(store, minted.Id, node.NodeId, cts.Token);
        await launcher.LaunchAsync(
            minted.Id, claimed.CurrentRunId!.Value, node.NodeId, node.OwnerId, claimed.LeaseGeneration, cts.Token);

        executor.Request!.Prompt.Should().Contain("@brian does this handle the empty-list case?");
        executor.Request.Prompt.Should().Contain("ryan");
        // The session's own working directory is the pull request checkout, a different directory
        // entirely from where its findings files land — the prompt must therefore name the exact
        // absolute path to write mention-answer.md to, never just the bare filename, or
        // PrReviewEngine.ComposeReportAndParkAsync's own read of Path.Combine(runDirectory,
        // "mention-answer.md") finds nothing (independent pre-PR review, cycle 1, conformance
        // lens, high).
        executor.Request.Prompt.Should().Contain(
            Path.Combine(executor.Request.RunDirectory, "mention-answer.md"),
            "the prompt must name the exact path PrReviewEngine reads back, not just the bare filename");
    }

    [Fact]
    public async Task A_comment_written_by_the_installs_own_login_is_never_counted_as_a_mention()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-own-comment-test";
        const int number = 4202;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-own-comment", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        // The connector's own filter already excludes an authorLogin matching the mentioned
        // login — this comment is here only so the fake gh has SOMETHING to answer, and the
        // assertion is that nothing at all was minted from it.
        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "brian", "@brian noting this for my own records", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("never dispatches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("the install's own comment is never a trigger");
    }

    /// <summary>
    /// AgelessRx/arx-platform#2166: a mention of this install's login on a pull request whose only
    /// live pr-review task is another owner's used to find that task by external reference alone and
    /// claim it. Now nothing is claimed, launched, appended or minted: the mention is recorded
    /// against no task, for the board to surface. <see cref="RefusingExecutor"/> and
    /// <see cref="RefusingWorktreeManager"/> prove nothing launched, and the stream's own version
    /// proves nothing was appended to it.
    /// </summary>
    [Fact]
    public async Task A_mention_on_a_pull_request_only_a_teammates_task_covers_is_recorded_and_touches_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string ownRoot = await EnsureOwnRootAsync(store, node, cts.Token);
        ownRoot.Should().NotBe(TeammateRoot);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-teammate-covered-test";
        const int number = 4210;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-teammate-covered", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid teammatesTask = await SeedTeammatesWaitingReviewAsync(store, projectId, repository, number, cts.Token);
        await SeedSafePrReviewPreflightAsync(store, teammatesTask, node.NodeId, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian any thoughts on the rate limiter?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("a teammate's task is never launched on"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> tasks = await query.Query<TaskListItem>()
            .Where(task => task.ProjectId == projectId).ToListAsync(cts.Token);
        tasks.Should().ContainSingle("nothing is minted over a teammate's task").Which.Id.Should().Be(teammatesTask);
        tasks[0].LatestMentionCommentId.Should().BeNull("the mention is never appended to the teammate's task");
        tasks[0].State.Should().Be(TaskState.AwaitingAuthor, "nothing claimed it");
        (await query.Events.FetchStreamStateAsync(teammatesTask, token: cts.Token))!.Version.Should().Be(5);

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.CoveredByTeammate);
        observed.TaskId.Should().BeNull();
    }

    [Fact]
    public async Task A_mention_predating_the_cutoff_never_mints()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-cutoff-test";
        const int number = 4203;

        // Registered now, with the mention two weeks old — the no-backfill guard applies to a
        // mention's own comment timestamp exactly as it does to a review request's.
        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-cutoff", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian could you take a look?", Now.AddDays(-14))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("held mentions never dispatch"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("the comment predates this project's own cutoff — no backfill");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.HeldBeforeCutoff);
        observed.TaskId.Should().BeNull();
    }

    [Fact]
    public async Task Auto_pr_review_off_silences_mentions_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-off-test";
        const int number = 4204;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-off", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Off), cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian could you take a look?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("off means off"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("--auto-pr-review off silences mentions exactly as it silences review requests");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.HeldSettingOff);
    }

    /// <summary>
    /// Off silences a follow-up dispatch onto an existing, already-covered task exactly as it
    /// silences a mint (independent pre-PR review, cycle 1, both lenses: the attach path used to
    /// launch before ever checking the setting). The mention still attaches — it costs nothing and
    /// keeps the record honest — but no session is spawned.
    /// </summary>
    [Fact]
    public async Task Auto_pr_review_off_silences_a_follow_up_onto_an_already_covered_task_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-attach-off-test";
        const int number = 4208;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-attach-off", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Off), cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question about this", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("off silences the follow-up too"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1", "the mention is still recorded on the task, off or not");
        watched.State.Should().Be(TaskState.AwaitingAuthor, "no follow-up claim was made, so the task never moved off its waiting state");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.AttachedNoFollowUp);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    /// <summary>
    /// The no-backfill guard applies to a follow-up dispatch onto an existing task exactly as it
    /// applies to a mint (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public async Task A_stale_mention_never_dispatches_a_follow_up_onto_an_already_covered_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-attach-cutoff-test";
        const int number = 4209;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-attach-cutoff", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian could you take a look?", Now.AddDays(-14))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("stale mentions never dispatch"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1");
        watched.State.Should().Be(TaskState.AwaitingAuthor, "no follow-up claim was made — the comment predates this project's own cutoff");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.AttachedNoFollowUp);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    [Fact]
    public async Task A_comment_id_already_handled_never_fires_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-dedup-test";
        const int number = 4205;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-dedup", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what do you think?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);
        Guid mintedTaskId;
        await using (IQuerySession query = store.QuerySession())
        {
            mintedTaskId = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single().Id;
        }

        // Abandon the minted task so a second mint would be visible if the dedup ever failed —
        // the comment id is what must stop it, not "a live task still covers it".
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(mintedTaskId, token: cts.Token))!;
            session.Events.Append(mintedTaskId, TaskDecider.Abandon(task, "test cleanup", Now, node.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession recheck = store.QuerySession();
        (await recheck.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().HaveCount(1, "the same comment id must never mint a second time, even once its own task is gone");
    }

    [Fact]
    public async Task A_mention_on_a_pull_request_whose_only_task_is_done_mints_a_fresh_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-done-test";
        const int number = 4206;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-done", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        // A Done pr-review task already covers this pull request — a prior review that reached
        // Done (merged/closed) does not block a fresh mint the way a live one does.
        Guid doneTaskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                doneTaskId, projectId, $"Review pull request {repository}#{number}", ["done"], TaskType.PrReview,
                null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"),
                Now.AddDays(-2), node.OwnerId);
            TaskAggregate task = new();
            task.Apply(added);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddDays(-2), node.OwnerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddDays(-2), node.OwnerId);
            task.Apply(assigned);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now.AddDays(-2));
            task.Apply(claimed);
            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, null, Now.AddDays(-2).AddHours(1));
            task.Apply(completed);
            session.Events.StartStream<TaskAggregate>(doneTaskId, [added, published, assigned, claimed, completed]);
            await session.SaveChangesAsync(cts.Token);
        }

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question now that this merged", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> tasks = await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token);
        tasks.Should().HaveCount(2, "the Done task stays, and a fresh one mints beside it");
        tasks.Should().Contain(task => task.Id == doneTaskId && task.State == TaskState.Done);
        tasks.Should().Contain(task => task.Id != doneTaskId && task.LatestMentionCommentId == "IC_1");
    }

    /// <summary>
    /// The mint-or-attach decision, decision 2's own core: a mention on a pull request a live task
    /// already covers attaches to it — recording <see cref="PullRequestReviewMentionObserved"/> on
    /// that task's own stream — and, because the task is waiting on its pull request
    /// (AwaitingAuthor, eligible for a follow-up), the daemon claims it and dispatches the bounded
    /// follow-up lap through <see cref="RunLauncher.LaunchPrReviewMentionFollowUpAsync"/>.
    /// </summary>
    [Fact]
    public async Task A_mention_on_a_pull_request_a_live_task_already_covers_attaches_and_dispatches_a_follow_up()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-attach-test";
        const int number = 4207;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-attach", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question about this", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new StubWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await SeedSafePrReviewPreflightAsync(store, watchedTaskId, node.NodeId, cts.Token);
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().HaveCount(1, "the mention attaches to the live task rather than minting a second one");

        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1");
        watched.State.Should().Be(TaskState.Claimed, "claimed for the bounded follow-up lap");

        executor.Request.Should().NotBeNull("a bounded follow-up session was dispatched");
        executor.Request!.Prompt.Should().Contain("@brian one more question about this");
        executor.Request.Prompt.Should().Contain("ryan");
        // Security review idea 6be68ee2, process-injection finding 1, Brian's ruling 2026-09-27:
        // a mention follow-up is one of the three real spawn sites that never skips permissions.
        executor.Request.SkipPermissions.Should().BeFalse();
        executor.Request.UsesReviewPermissions.Should().BeTrue();
        // Task 7ae690f5: a follow-up lap is spawned with a hard turn limit, the same shape
        // CourierEngine passes CourierMaxTurns with, rather than the unbounded budget an ordinary
        // build session gets.
        executor.Request.MaxTurns.Should().Be(new DaemonOptions().PrReviewMentionFollowUpMaxTurns);

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached);
        observed.TaskId.Should().Be(watchedTaskId);

        // Task 7ae690f5: this task was seeded through SeedWaitingReviewAsync, the same shape a
        // human's own h9k task add --from-pr adoption takes (no PullRequestReviewAssignmentObserved
        // ever landed on its stream) — attaching a mention and dispatching a follow-up for it must
        // never mark it as auto-pr-review's own twin, or PullRequestReviewDuplicateRule.IsRival
        // would start treating a person's own adopted task as one.
        TaskListItem watchedListItem = (await query.LoadAsync<TaskListItem>(watchedTaskId, cts.Token))!;
        watchedListItem.WasAutoPrReviewCreated.Should().BeFalse(
            "PullRequestReviewMentionObserved carries MintedTask only from a fresh mint, never an attach");
    }

    /// <summary>
    /// A task whose review already parked but is not yet resolved sits Claimed, never
    /// AwaitingAuthor/NeedsHuman — the park lives entirely on the run stream
    /// (<c>RunState.ReviewParked</c>), never the task's own. Both independent review lenses (cycle
    /// 1) found <see cref="TaskDecider.AwaitsPrReviewFollowThrough"/> alone blind to this, so a
    /// mention arriving here used to just sit recorded with no follow-up ever dispatched — exactly
    /// the criterion's own "when that task's report is already parked" case.
    /// </summary>
    private static async Task<Guid> SeedParkedReviewAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-2), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddHours(-2));
        task.Apply(claimed);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, LeaseGeneration: 1, SessionId: DomainId.New(),
            WorktreePath: "/tmp/does-not-exist", Branch: $"pr/{number}", ExecutorMode.Subscription, Now.AddHours(-2)));
        session.Events.Append(runId, new ReviewParked(
            runId, "Pull request review complete. Findings: /tmp/does-not-exist/review-1-findings.md.", Now.AddHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    [Fact]
    public async Task A_mention_on_a_task_whose_report_is_parked_and_unresolved_attaches_and_dispatches_a_follow_up()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-parked-report-test";
        const int number = 5301;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-parked", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedParkedReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does the retry logic look right to you?", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new StubWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await SeedSafePrReviewPreflightAsync(store, watchedTaskId, node.NodeId, cts.Token);
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1");
        watched.State.Should().Be(
            TaskState.Claimed, "reclaimed for the bounded follow-up lap, exactly as the AwaitingAuthor case is");

        executor.Request.Should().NotBeNull(
            "a report parked and not yet resolved is one of the two states the criterion names for a dispatched follow-up");
        executor.Request!.Prompt.Should().Contain("@brian does the retry logic look right to you?");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    /// <summary>
    /// The follow-up gate's own Unknown case (independent pre-PR review, cycle 1, conformance
    /// lens): this node has not yet computed the project's declared member accounts at all — the
    /// first sweep after a restart, modelled here by never recording an <see cref="EnrolledNodeSnapshots"/>
    /// entry for the project at all — must skip without recording anything, exactly as a fresh
    /// mint's own Unknown already does, so the identical comment id is still fresh next sweep once
    /// membership is known. Before this fix, this path recorded <see cref="ReviewMentionOutcome.AttachedNoFollowUp"/>
    /// permanently — a comment id already handled never fires again — closing the door on a
    /// genuine member's follow-up for good.
    /// </summary>
    [Fact]
    public async Task A_mention_follow_up_with_membership_unknown_skips_without_recording_and_retries_next_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-membership-unknown-test";
        const int number = 5303;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-membership-unknown", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedParkedReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does the retry logic look right to you?", Now.AddMinutes(5))],
            isPrivate: false);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new StubWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().BeNull("nothing was recorded on the task's own stream either");
        watched.State.Should().Be(TaskState.Claimed, "the original human review lap, untouched");

        executor.Request.Should().BeNull("a guess must never dispatch a follow-up");

        (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))
            .Should().BeNull("the comment id must still read as fresh next sweep, once membership is known");
    }

    /// <summary>
    /// A human reviewer's own <c>h9k pr review</c> lap re-enters the identical Claimed/ReviewParked
    /// shape <see cref="SeedParkedReviewAsync"/> leaves a task in, without moving either off it
    /// (<c>PullRequestReviewCommand</c>'s own re-entry path). A mention landing mid-lap must not
    /// claim the task out from under that live human review — the worktree cleanup a mention
    /// follow-up's own launch runs would delete the checkout the reviewer is sitting in
    /// (independent pre-PR review, cycle 1, adversarial lens, high).
    /// </summary>
    [Fact]
    public async Task A_mention_on_a_task_with_an_open_human_review_lap_attaches_without_claiming_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-open-lap-test";
        const int number = 5302;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-open-lap", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        (Guid watchedTaskId, Guid lapRunId) = await SeedParkedReviewWithOpenLapAsync(
            store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does the retry logic look right to you?", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1", "the mention is still recorded on the task's own stream");
        watched.State.Should().Be(TaskState.Claimed, "the lap's own claim, never reclaimed for a follow-up");
        watched.CurrentRunId.Should().Be(
            lapRunId, "the reviewer's own lap run is still current — a follow-up would have repointed it");
        watched.ReviewLapOpen.Should().BeTrue("the human reviewer's lap is still open");

        executor.Request.Should().BeNull(
            "a mention must never dispatch a follow-up onto a task a live human review lap already owns");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(
            ReviewMentionOutcome.AttachedNoFollowUp, "the mention is still recorded, just without a claim");
        observed.TaskId.Should().Be(watchedTaskId);
    }

    private static async Task<(Guid TaskId, Guid RunId)> SeedParkedReviewWithOpenLapAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-2), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddHours(-2));
        task.Apply(claimed);
        PullRequestReviewLapOpened lapOpened = new(
            taskId, runId, "/tmp/does-not-exist", $"https://github.com/{repository}/pull/{number}",
            Now.AddMinutes(-30), node.OwnerId);
        task.Apply(lapOpened);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed, lapOpened]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, LeaseGeneration: 1, SessionId: DomainId.New(),
            WorktreePath: "/tmp/does-not-exist", Branch: $"pr/{number}", ExecutorMode.Subscription, Now.AddHours(-2)));
        session.Events.Append(runId, new ReviewParked(
            runId, "Pull request review complete. Findings: /tmp/does-not-exist/review-1-findings.md.", Now.AddHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }

    /// <summary>
    /// The second search itself: <c>gh</c> is asked for <c>mentions:&lt;login&gt;</c>, a distinct
    /// call from the review-requested search the sweep already made — proof the sweep runs both,
    /// not that a single search happens to answer both trigger names.
    /// </summary>
    [Fact]
    public async Task The_sweep_runs_a_second_search_for_mentions_beside_review_requested()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-second-search-test";

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-second-search", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        List<IReadOnlyList<string>> searches = [];
        ProcessRunner gh = (fileName, arguments, workingDirectory, cancellationToken) =>
        {
            if (arguments.Contains("--search"))
            {
                searches.Add(arguments);
            }

            return MentionScriptedGh(repository, 1, "brian", [])(fileName, arguments, workingDirectory, cancellationToken);
        };
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("no mention here dispatches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        searches.Should().Contain(arguments => arguments.Contains("review-requested:brian"));
        searches.Should().Contain(arguments => arguments.Contains("mentions:brian"), "the second, independent search idea 2f079bcd adds");
    }

    // -------------------------------------------------------------------------------------
    // The bounded follow-up's own caps (task 7ae690f5): a LIFETIME per-task cap, a cooldown
    // between laps, only the fleet's own leader ever dispatches one, a per-repository hourly mint
    // cap, and the period spend budget — all built over facts this feature already records, no
    // new event or counter.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The LIFETIME cap (task 7ae690f5, Opus verdict 2026-09-27): a task that has already
    /// dispatched as many follow-up laps as <see cref="DaemonOptions.AutoPrReviewMentionFollowUpCap"/>
    /// allows attaches a further mention to the record but never claims or dispatches another one
    /// — <see cref="RefusingExecutor"/> and <see cref="RefusingWorktreeManager"/> both prove that
    /// structurally. Decided purely over the two prior <see cref="ObservedReviewMention"/> rows
    /// this test seeds directly with <c>Outcome</c> <c>Attached</c>, exactly the rows a real
    /// dispatch would itself have recorded — no new event or counter is needed.
    /// </summary>
    [Fact]
    public async Task A_task_at_its_lifetime_follow_up_cap_attaches_without_dispatching_another_lap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-cap-test";
        const int number = 4401;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-cap", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 0; i < 2; i++)
            {
                session.Store(new ObservedReviewMention
                {
                    Id = ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", $"IC_prior_{i}"),
                    ObservingNodeId = node.NodeId,
                    ProjectId = projectId,
                    Repository = repository,
                    Number = number,
                    PullRequestUrl = $"https://github.com/{repository}/pull/{number}",
                    MentionedLogin = "brian",
                    CommentId = $"IC_prior_{i}",
                    CommentAuthorLogin = "ryan",
                    CommentBody = "an earlier question",
                    CommentUrl = $"https://github.com/{repository}/pull/{number}#issuecomment-prior{i}",
                    CommentCreatedAt = Now.AddHours(-3),
                    ObservedAt = Now.AddHours(-3),
                    Outcome = ReviewMentionOutcome.Attached,
                    TaskId = watchedTaskId,
                });
            }

            await session.SaveChangesAsync(cts.Token);
        }

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_new", "ryan", "@brian one more question", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("the lifetime cap holds this dispatch"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            options: Options.Create(new DaemonOptions { AutoPrReviewMentionFollowUpCap = 2 }), clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_new"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.AttachedNoFollowUp);
        observed.OutcomeDetail.Should().Contain("cap");
        observed.OutcomeDetail.Should().Contain("h9k pr review --since-my-review");

        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.State.Should().Be(TaskState.AwaitingAuthor, "the cap held the claim, so the task's own state never moved");
    }

    /// <summary>
    /// The cooldown between laps (task 7ae690f5): a task whose last follow-up dispatched five
    /// minutes ago, well inside the default thirty-minute cooldown, attaches a further mention to
    /// the record but never claims or dispatches another one.
    /// </summary>
    [Fact]
    public async Task A_task_still_cooling_down_from_its_last_follow_up_attaches_without_dispatching_another_lap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-cooldown-test";
        const int number = 4402;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-cooldown", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new ObservedReviewMention
            {
                Id = ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_prior"),
                ObservingNodeId = node.NodeId,
                ProjectId = projectId,
                Repository = repository,
                Number = number,
                PullRequestUrl = $"https://github.com/{repository}/pull/{number}",
                MentionedLogin = "brian",
                CommentId = "IC_prior",
                CommentAuthorLogin = "ryan",
                CommentBody = "an earlier question",
                CommentUrl = $"https://github.com/{repository}/pull/{number}#issuecomment-prior",
                CommentCreatedAt = Now.AddMinutes(-5),
                ObservedAt = Now.AddMinutes(-5),
                Outcome = ReviewMentionOutcome.Attached,
                TaskId = watchedTaskId,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_new", "ryan", "@brian and one more thing", Now.AddMinutes(1))]);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("the cooldown holds this dispatch"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            options: Options.Create(new DaemonOptions { AutoPrReviewMentionFollowUpCooldown = TimeSpan.FromMinutes(30) }),
            clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_new"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.AttachedNoFollowUp);
        observed.OutcomeDetail.Should().Contain("cools down");
        observed.OutcomeDetail.Should().Contain("h9k pr review --since-my-review");
    }

    /// <summary>
    /// Only the fleet's own leader dispatches a follow-up (task 7ae690f5): a lower-ranked peer
    /// exists in the fleet snapshot, so this node attaches the mention to the task's own stream —
    /// the record is never lost — but never claims it or launches a run.
    /// <see cref="RefusingExecutor"/> and <see cref="RefusingWorktreeManager"/> both prove
    /// structurally that no dispatch was attempted.
    /// </summary>
    [Fact]
    public async Task A_non_leader_node_attaches_a_mention_without_claiming_or_dispatching()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-non-leader-test";
        const int number = 4403;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-non-leader", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question", Now.AddMinutes(5))]);
        EnrolledNodeSnapshots fleet = FleetOfThisNodeAnd(projectId, node, LowerRankedPeer);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("a non-leader node never dispatches a follow-up"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            enrolledNodes: fleet, clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1", "the mention still attaches to the task's own stream");
        watched.State.Should().Be(TaskState.AwaitingAuthor, "a non-leader node never claims the task for a follow-up");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.AttachedNoFollowUp);
        observed.OutcomeDetail.Should().Contain("fleet peer ranks first");
    }

    /// <summary>
    /// The pull request's own author answering this owner's review, on a task already following the
    /// pull request through, is recorded on the task and shown as a row, and nothing is claimed or
    /// launched for it: <see cref="RefusingExecutor"/> and <see cref="RefusingWorktreeManager"/>
    /// prove structurally that no session was attempted, and the reply is not an
    /// <see cref="ReviewMentionOutcome.Attached"/> outcome, so it never counts toward the
    /// follow-up lifetime cap. The author is matched by login, case-insensitively.
    /// </summary>
    [Fact]
    public async Task The_pull_request_authors_reply_on_a_task_following_it_through_is_recorded_and_launches_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-author-reply-test";
        const int number = 4411;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-author-reply", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "Taylor-Dennison", "@brian fixed in abc123, please look again", Now.AddMinutes(5))],
            pullRequestAuthor: ("taylor-dennison", 4411L, "MEMBER"));
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("an author's reply never launches a session"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1", "the reply is recorded on the task's own stream");
        watched.LatestMentionUrl.Should().Contain("issuecomment-IC_1", "the comment's link is recorded for h9k task show");
        watched.State.Should().Be(TaskState.AwaitingAuthor, "nothing claimed the task");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.AuthorReplied);
        observed.TaskId.Should().Be(watchedTaskId);
        observed.CommentBody.Should().Contain("please look again");
        (await query.Query<ObservedReviewMention>().Where(mention => mention.TaskId == watchedTaskId).ToListAsync(cts.Token))
            .Should().NotContain(
                mention => mention.Outcome == ReviewMentionOutcome.Attached,
                "an author's reply is not a dispatched follow-up lap, so the lifetime cap never counts it");
    }

    /// <summary>
    /// The fleet-coordination hold applies to a fresh mint from a mention too (task 7ae690f5): a
    /// lower-ranked peer exists, and the comment is still inside the default hold on the first
    /// sweep, so this node holds — nothing minted, and the comment left unrecorded exactly as
    /// <c>ObservedReviewMention</c>'s own permanent one-shot dedupe requires, since a row recorded
    /// while held could never be re-decided once the hold ends. Once the hold is over with
    /// nothing covering the mention, the same node mints at once, the identical two-sweep shape
    /// <c>PrReviewTaskEngineTests.A_follower_holds_a_fresh_request_for_the_leader_with_one_log_line_and_mints_when_its_hold_ends</c>
    /// already proves for the request side.
    /// </summary>
    [Fact]
    public async Task A_peer_held_mention_mints_once_its_hold_has_already_elapsed_and_nothing_covers_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-peer-hold-test";
        const int number = 4404;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-peer-hold", repository, Now.AddDays(-2), Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        DateTimeOffset commentCreatedAt = Now.AddSeconds(-60);
        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what do you think?", commentCreatedAt)]);
        EnrolledNodeSnapshots fleet = FleetOfThisNodeAnd(projectId, node, LowerRankedPeer);

        AutoPrReviewEngine holding = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("a standing peer hold never mints"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            enrolledNodes: fleet, clock: new FixedClock(Now));

        await holding.PollOnceAsync(cts.Token);

        await using (IQuerySession heldQuery = store.QuerySession())
        {
            (await heldQuery.Query<TaskListItem>().Where(task => task.ProjectId == projectId).CountAsync(cts.Token))
                .Should().Be(0, "the fleet's own leader is still inside its hold, so this node mints nothing yet");

            (await heldQuery.LoadAsync<ObservedReviewMention>(
                ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))
                .Should().BeNull(
                    "a held mint is left unrecorded so the next sweep can still decide it once the hold ends");
        }

        // The hold is over: the same node, now told never to defer, sees the same standing
        // mention with nothing covering it and mints.
        AutoPrReviewEngine minting = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            enrolledNodes: fleet, options: Options.Create(new DaemonOptions { AutoPrReviewMintHoldSeconds = 0 }),
            clock: new FixedClock(Now));

        await minting.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
        minted.Type.Should().Be(TaskType.PrReview);

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.TaskCreated);
        observed.TaskId.Should().Be(minted.Id);
    }

    /// <summary>
    /// The per-repository hourly mint cap (task 7ae690f5): with the cap set to one and this
    /// repository already carrying one auto-created task minted ten minutes ago, a mention on a
    /// second pull request in the same repository holds rather than mints — and is left
    /// unrecorded, the identical permanent-dedupe reason a fleet-peer hold is, so the next sweep
    /// retries once the window rolls rather than losing the comment for good.
    /// </summary>
    [Fact]
    public async Task A_fresh_mention_mint_past_the_hourly_cap_holds_and_is_left_unrecorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-hourly-cap-test";
        const int alreadyMintedNumber = 4501;
        const int number = 4502;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-hourly-cap", repository, Now.AddDays(-2), Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        await SeedAutoCreatedTaskAsync(store, node, projectId, repository, alreadyMintedNumber, Now.AddMinutes(-10), cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what about this one?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node,
            NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("the hourly cap holds this mint"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance,
            options: Options.Create(new DaemonOptions { AutoPrReviewHourlyMintCapPerRepository = 1 }), clock: new FixedClock(Now));

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).CountAsync(cts.Token))
            .Should().Be(1, "the hourly cap holds a fresh mint for the second pull request");

        (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))
            .Should().BeNull(
                "left unrecorded so the next sweep retries once the window rolls, the identical reason a "
                + "fleet-peer hold is");
    }
}
