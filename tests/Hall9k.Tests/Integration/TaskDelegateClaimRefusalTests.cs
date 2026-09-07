using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k task delegate's own entry-state refusals and success paths (task 15f889e3-h9k, design
/// ruling R6, idea fcaded0b's design rulings, Take the Wheel epic 9272e514's slice 10), pinned
/// against a real store and a real git repository because <see cref="TaskDelegateCommand.PrepareAsync"/>
/// reads the interactive claim's own run and worktree straight off both — exactly the shape
/// <c>TaskStartClaimRefusalTests</c> already is for h9k task start's own sibling. This never
/// drives as far as the actual detached process spawn (<see cref="HeadlessLaunch"/>) — that needs
/// a real <c>claude</c> binary and is out of reach here, the same reasoning that class gives for
/// stopping at <c>ClaimAndCutAsync</c> rather than <c>RunDeliberateStartAsync</c>.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TaskDelegateClaimRefusalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Note = "Attempted: the retry loop. Deliberate: left the timeout at 30s. Latitude: none.";
    private readonly List<string> _repositoryRoots = [];
    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-delegate-claim-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task A_draft_task_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Still being written", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*")
            .Where(exception => exception.Message.Contains("active interactive claim"));
    }

    /// <summary>
    /// An ordinary headless claim (h9k task assign, or the dispatcher's own claim) reads Claimed
    /// but carries a real node id rather than the interactive sentinel — the same discriminator
    /// <see cref="TaskAggregate.IsInteractiveClaim"/> is for.
    /// </summary>
    [Fact]
    public async Task A_headless_claim_is_refused_as_not_interactive()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Dispatched headlessly", ["it is done"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("active interactive claim"));
    }

    [Fact]
    public async Task A_claim_belonging_to_a_different_owner_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid theirOwnerId = DomainId.New();
        Guid myOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Someone else's interactive claim", ["it is done"],
                    TaskType.Chore, null, null, null, Now, theirOwnerId),
                theirOwnerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, theirOwnerId, DomainId.New(), Now, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, myOwnerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage($"*claimed by {theirOwnerId}*")
            .Where(exception => exception.Message.Contains("your own interactive claim"));
    }

    /// <summary>
    /// A pr-review task's own Claimed+sentinel state (AutoPrReviewEngine.CreateOneAsync's Now
    /// speed) reads identically to a real interactive claim on IsInteractiveClaim's own
    /// Guid.Empty discriminator — mirrors TaskHandbackCommand's identical guard. Seeded with
    /// interactiveMode: false, exactly as CreateOneAsync's own claim leaves it (it never turns
    /// interactive mode on), so this test actually exercises the pr-review guard's ordering ahead
    /// of the interactive-mode-off check rather than the interactive-mode-off check itself
    /// (adversarial review, cycle 1: interactiveMode: true here made this test pass regardless of
    /// which guard the command reached first).
    /// </summary>
    [Fact]
    public async Task A_pr_review_sentinel_claim_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Reviewing a pull request", ["it is done"],
                    TaskType.PrReview, null, null, null, Now, ownerId),
                ownerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, DomainId.New(), Now, dependencyOverrideAcknowledged: false, interactiveMode: false);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*pr-review task*")
            .Where(exception => exception.Message.Contains("not an interactive claim to delegate"));
    }

    [Fact]
    public async Task A_claim_whose_run_has_no_record_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "The process died mid-claim", ["it is done"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            // No RunDispatched stream ever started for this run id — mirrors a process that died
            // between the TaskClaimed append and the worktree cut.
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, DomainId.New(), Now, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*has no record*")
            .Where(exception => exception.Message.Contains("h9k task release"));
    }

    /// <summary>
    /// Orchestrator ruling on cycle 2's own medium finding: the refusal must not send the operator
    /// to a command that cannot actually fix the problem it names. h9k task work re-enters this
    /// exact claim (<see cref="Hall9k.Cli.Commands.TaskWorkCommand.ReenterAsync"/>) without
    /// appending any event, so it can never turn <see cref="TaskAggregate.InteractiveModeEnabled"/>
    /// back on — only a fresh <c>TaskClaimed</c> does that (<c>TaskRevised.ClearInteractiveMode</c>'s
    /// own doc). The refusal instead has to name the release-then-reclaim path (only possible on an
    /// untouched claim) and h9k task handback as the honest alternative once the branch holds work.
    /// </summary>
    [Fact]
    public async Task A_claim_with_interactive_mode_cleared_is_refused_without_naming_task_work_as_the_fix()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await ClaimInteractivelyAsync(store, taskId, projectId, ownerId, repositoryPath, cts.Token);

        await using (IDocumentSession clear = store.LightweightSession())
        {
            StreamState fence = (await clear.Events.FetchStreamStateAsync(taskId, cts.Token))!;
            TaskAggregate task = (await clear.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cts.Token))!;
            TaskRevised revised = TaskDecider.Revise(
                task, default, default, default, default, default, default, Now, ownerId,
                clearInteractiveMode: true);
            clear.Events.Append(taskId, expectedVersion: fence.Version + 1, revised);
            await clear.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*claim turned interactive mode off*")
            .Where(exception =>
                !exception.Message.Contains("re-enters it interactively, which turns the flag back on")
                && exception.Message.Contains("h9k task release")
                && exception.Message.Contains("h9k task handback"));
    }

    [Fact]
    public async Task A_claim_already_handed_to_the_standard_pipeline_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();
        Guid runId;

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Already delivered", ["it is done"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            runId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, runId, Now, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, ownerId, LeaseGeneration: 1, SessionId: DomainId.New(),
                WorktreePath: Path.Combine(repositoryPath, "wt"), Branch: "task/x",
                ExecutorMode.Subscription, Now));
            // Mirrors h9k task deliver's own append: the run's own state moves to Verifying, past
            // the Dispatched/Running window this command may still act in.
            seed.Events.Append(runId, new AgentSessionCompleted(runId, Now, DomainId.New()));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*already*")
            .Where(exception => exception.Message.Contains("h9k task deliver"));
    }

    /// <summary>
    /// The double-booking guard <see cref="InteractiveSessionLiveness.EnsureNotAttachedElsewhere"/>
    /// already carries its own generic tests; this is the one integration point proving
    /// <see cref="TaskDelegateCommand.PrepareAsync"/> actually calls it before dispatching a
    /// contractor into a worktree an operator is still attached to.
    /// </summary>
    [Fact]
    public async Task A_claim_with_a_still_attached_interactive_session_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();
        Guid runId;
        using Process current = Process.GetCurrentProcess();
        DateTimeOffset startedAt = new(current.StartTime.ToUniversalTime(), TimeSpan.Zero);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Still attached in another terminal", ["it is done"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            runId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, runId, Now, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, ownerId, LeaseGeneration: 1, SessionId: DomainId.New(),
                WorktreePath: Path.Combine(repositoryPath, "wt"), Branch: "task/x",
                ExecutorMode.Subscription, Now));
            seed.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), startedAt, current.Id, Environment.MachineName, "abc12345-interactive-claim"));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*still attached*");
    }

    [Fact]
    public async Task A_fresh_interactive_claim_on_a_virgin_branch_does_not_resume_previous_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        Guid runId = await ClaimInteractivelyAsync(store, taskId, projectId, ownerId, repositoryPath, cts.Token);

        TaskDelegateCommand.DelegationPlan plan = await PrepareAsync(store, taskId, ownerId, cts.Token);

        plan.RunId.Should().Be(runId, "delegation reuses the interactive claim's own run rather than minting a new one");
        plan.ResumesPreviousWork.Should().BeFalse("nothing has been committed on this branch yet");
        plan.SessionName.Should().EndWith("-build");
        plan.Prompt.Should().Contain("A human delegated this phase to you");
        plan.Prompt.Should().Contain("Nothing has been committed on this branch yet");
        plan.Prompt.Should().Contain(Note);
        plan.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task A_claim_whose_branch_already_carries_commits_resumes_previous_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await ClaimInteractivelyAsync(store, taskId, projectId, ownerId, repositoryPath, cts.Token);

        await using (IQuerySession verify = store.QuerySession())
        {
            TaskAggregate task = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            RunDetails run = (await verify.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!;
            File.WriteAllText(Path.Combine(run.WorktreePath, "committed.txt"), "already here\n");
            Git(run.WorktreePath, "add -A");
            Git(run.WorktreePath, "-c user.name=Test -c user.email=test@test commit -m \"prior work\"");
        }

        TaskDelegateCommand.DelegationPlan plan = await PrepareAsync(store, taskId, ownerId, cts.Token);

        plan.ResumesPreviousWork.Should().BeTrue("the branch already carries a commit ahead of base");
        plan.Prompt.Should().Contain("This worktree already holds work");
    }

    /// <summary>
    /// <see cref="TaskDelegateCommand.PrepareAsync"/> resolves the calling owner itself, through
    /// <see cref="NodeBootstrap.EnsureAsync"/>, which is idempotent per store but not per test: a
    /// <see cref="PostgresFixture"/> database is shared across every <c>[Fact]</c> in this class,
    /// so whichever test runs first mints the one owner every later test's own bootstrap call
    /// reads back. Every test below whose claim must actually pass PrepareAsync's own "is this
    /// your claim" check resolves that real owner here first, rather than a random id of its own
    /// that would only coincidentally match.
    /// </summary>
    private static async Task<Guid> ResolveOwnerAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return context.OwnerId;
    }

    private async Task<Guid> ClaimInteractivelyAsync(
        DocumentStore store, Guid taskId, Guid projectId, Guid ownerId, string repositoryPath, CancellationToken cancellationToken)
    {
        await using IDocumentSession seed = store.LightweightSession();
        seed.Store(new ProjectDetails
        {
            Id = projectId,
            RepositoryPath = repositoryPath,
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(taskId, projectId, "Interactively claimed for delegation", ["it is done"],
                TaskType.Chore, null, null, null, Now, ownerId),
            ownerId, Now));
        await seed.SaveChangesAsync(cancellationToken);

        StreamState fence = (await seed.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await seed.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

        // ClaimAndCutAsync's success path ends in Doorbell.RingAsync, which resolves its
        // connection off HALL9K_CONNECTION_STRING rather than this fixture, so it has to be
        // pointed at the fixture for the one call that reaches it (mirrors TaskStartClaimRefusalTests).
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            (Guid runId, _, _, _, _, _) = await TaskStartCommand.ClaimAndCutAsync(
                store, seed, task, fence, context, DomainId.New(),
                SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
                acknowledgeUnmetDependencies: false, interactiveMode: true, trackerClaimGate: null,
                cancellationToken);
            return runId;
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }
    }

    private static async Task<TaskDelegateCommand.DelegationPlan> PrepareAsync(
        DocumentStore store, Guid taskId, Guid ownerId, CancellationToken cancellationToken)
    {
        // ownerId is unused here — TaskDelegateCommand.PrepareAsync resolves the calling owner
        // itself, through NodeBootstrap.EnsureAsync — but every call site names the owner the
        // fixture claimed as, so the test reads honestly even though this helper does not pass it on.
        await using IDocumentSession session = store.LightweightSession();
        return await TaskDelegateCommand.PrepareAsync(session, taskId, Note, force: false, cancellationToken);
    }

    private string CreateRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-delegate-repo-{Guid.NewGuid():N}");
        _repositoryRoots.Add(root);
        Directory.CreateDirectory(root);

        string originPath = Path.Combine(root, "origin.git");
        string seedPath = Path.Combine(root, "seed");
        Git(root, $"init --bare -b main \"{originPath}\"");
        Git(root, $"clone \"{originPath}\" \"{seedPath}\"");
        File.WriteAllText(Path.Combine(seedPath, "README.md"), "# seed\n");
        Git(seedPath, "add -A");
        Git(seedPath, "-c user.name=Test -c user.email=test@test commit -m init");
        Git(seedPath, "push origin main");

        string repositoryPath = Path.Combine(root, "repo");
        Git(root, $"clone \"{originPath}\" \"{repositoryPath}\"");
        return repositoryPath;
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        try
        {
            if (Directory.Exists(_home))
            {
                Directory.Delete(_home, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        foreach (string root in _repositoryRoots)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
