using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A reviewer's own review lap end to end (Decisions Log #149), against a real store and a
/// scripted <c>gh</c>: <c>PullRequestReviewCommand.RunAsync</c> and
/// <c>PullRequestReviewVerdict.DeliverAsync</c> are called directly rather than through
/// <c>CliStore.Open</c>'s ambient connection, which is the only test seam this codebase's CLI
/// commands have (the same shape <see cref="ReviewResolveCommandTests"/> already takes).
/// <para>
/// Both the lap and the verdict ring the doorbell (<c>Hall9k.Cli.Infrastructure.Doorbell</c>),
/// which resolves its connection off <c>HALL9K_CONNECTION_STRING</c> rather than this fixture,
/// and both write artifacts under <c>HALL9K_HOME</c>. Both are process-wide, so this joins the
/// same collection every other test that redirects them does.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class PullRequestReviewLapTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;
    private readonly string home;
    private readonly string? previousHome;
    private readonly string? previousConnectionString;
    private readonly List<string> scratchDirectories = [];

    /// <summary>
    /// A repository of this test's own. The one-live-task-per-item rule the lap's attach path
    /// enforces is keyed on the canonical reference and is deliberately project-blind — the same
    /// rule <c>TaskAddCommand.RefuseSecondAdoptionAsync</c> and <c>AutoPrReviewEngine</c> share —
    /// so two tests naming the same <c>owner/repo#42</c> against this class's shared Postgres
    /// fixture would have the second one attach to the first one's task instead of adopting.
    /// That is the rule working; making each test's pull request genuinely a different pull
    /// request is what lets each one exercise the path it is about.
    /// </summary>
    private readonly string repository = $"acme/web-{Guid.NewGuid():N}";

    public PullRequestReviewLapTests(PostgresFixture postgres)
    {
        this.postgres = postgres;
        home = Path.Combine(Path.GetTempPath(), $"hall9k-lap-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
        previousConnectionString = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
    }

    /// <summary>
    /// The pull request as gh reports it, in one document that serves both reads the lap makes:
    /// <c>GitHubPullRequestSurface</c>'s own wide field list and, on the adoption path,
    /// <c>GitHubPullRequestProvider</c>'s narrower one — which reads a strict subset of these
    /// properties, so a single response cannot let the two disagree about the pull request.
    /// <para>
    /// The body deliberately carries no closing keyword and no Jira key: those would send
    /// <c>LinkedWorkItemImport</c> off to import a linked item, which is real behaviour but not
    /// what any test here is about.
    /// </para>
    /// </summary>
    private string PullRequestJson =>
        $$"""
        {
          "number": 42,
          "title": "Teach the closeout monitor to read a rebase conflict",
          "body": "Adds the conflict read and the dispute park behind it.",
          "state": "OPEN",
          "url": "https://github.com/{{repository}}/pull/42",
          "baseRefName": "main",
          "headRefName": "task/9f2-conflict-read",
          "headRefOid": "0f1e2d3c4b5a69788796a5b4c3d2e1f001234567",
          "author": { "login": "someone-else" },
          "additions": 120,
          "deletions": 18,
          "files": [
            { "path": "src/Hall9k.Daemon/Closeout/CloseoutEngine.cs", "additions": 80, "deletions": 10 },
            { "path": "tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs", "additions": 40, "deletions": 8 }
          ],
          "statusCheckRollup": [
            { "name": "build", "workflowName": "ci", "status": "COMPLETED", "conclusion": "SUCCESS" },
            { "name": "test", "workflowName": "ci", "status": "IN_PROGRESS" }
          ]
        }
        """;

    [Fact]
    public async Task The_lap_attaches_to_the_pr_review_task_this_node_already_holds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        string projectName = seeded.ProjectName;
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), worktrees, cts.Token);
            result.Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> prReviewTasks = await query.Query<TaskListItem>()
            .Where(task => task.ExternalReference == $"github-pr:{repository}#42")
            .ToListAsync(cts.Token);
        prReviewTasks.Should().ContainSingle(
            "the lap attaches to the task this node already holds — it never mints a second one for the same pull request");

        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        task.ReviewLapOpen.Should().BeTrue();
        task.ReviewLapRunId.Should().Be(
            seeded.RunId, "the lap rides on the automated review's own parked run, not a run of its own");
        task.ReviewLapWorktreePath.Should().Be(seeded.WorktreePath);
        worktrees.PrReviewCheckouts.Should().BeEmpty(
            "the run's existing read-only worktree is reused, not re-fetched");
    }

    [Fact]
    public async Task The_lap_adopts_the_pull_request_when_no_live_task_holds_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        SeededProject project = await SeedProjectAsync(store, node, cts.Token);
        string projectName = project.Name;
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), worktrees, cts.Token);
            result.Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskListItem adopted = (await query.Query<TaskListItem>()
            .Where(item => item.ExternalReference == $"github-pr:{repository}#42")
            .ToListAsync(cts.Token)).Should().ContainSingle().Subject;
        adopted.Type.Should().Be(TaskType.PrReview, "--from-pr's own rule: adopting a pull request is always a pr-review task");
        adopted.State.Should().Be(TaskState.Claimed, "the lap claims what it adopts, so the dispatcher never takes it mid-read");

        TaskDetails details = (await query.LoadAsync<TaskDetails>(adopted.Id, cts.Token))!;
        details.ProjectId.Should().Be(project.Id);
        details.AcceptanceCriteria.Should().ContainSingle()
            .Which.Should().Contain("GitHub review", "a lap's deliverable is the submitted review");
        details.ReviewLapOpen.Should().BeTrue();
        details.CurrentRunId.Should().NotBeNull();

        RunDetails run = (await query.LoadAsync<RunDetails>(details.CurrentRunId!.Value, cts.Token))!;
        run.Branch.Should().Be("pr/42");
        run.NodeId.Should().Be(Guid.Empty, "a reviewer's lap is a human-held, ceiling-exempt claim");
        run.DispatchingNodeId.Should().Be(
            node.NodeId, "the sentinel carries no node identity, so the daemon's own sweep can only find this run by the dispatching node");
        run.PrReviewBaseRefName.Should().Be("main");
        worktrees.PrReviewCheckouts.Should().ContainSingle().Which.Should().Be(42);
    }

    [Fact]
    public async Task No_worktree_skips_the_checkout_and_records_the_absence()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string projectName = (await SeedProjectAsync(store, node, cts.Token)).Name;
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession session = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName, NoWorktree = true },
                ScriptedGh(), worktrees, cts.Token);
        }

        worktrees.PrReviewCheckouts.Should().BeEmpty("--no-worktree means no checkout is ever fetched");

        await using IQuerySession query = store.QuerySession();
        TaskListItem adopted = (await query.Query<TaskListItem>()
            .Where(item => item.ExternalReference == $"github-pr:{repository}#42")
            .ToListAsync(cts.Token)).Should().ContainSingle().Subject;
        TaskDetails details = (await query.LoadAsync<TaskDetails>(adopted.Id, cts.Token))!;
        details.ReviewLapWorktreePath.Should().BeNull(
            "an absent checkout is recorded as absent, never as an empty path something might later hand to git");
        details.ReviewLapOpen.Should().BeTrue("the lap runs the same way otherwise");

        RunDetails run = (await query.LoadAsync<RunDetails>(details.CurrentRunId!.Value, cts.Token))!;
        run.WorktreePath.Should().BeEmpty();
        run.Branch.Should().Be("pr/42", "the branch name is the only record of which pull request this run was for");
        ReadPrompt(run).Should().Contain(
            "--no-worktree", "the briefing tells the session there is no checkout rather than letting it hunt for one");
    }

    [Fact]
    public async Task The_briefing_quotes_the_findings_report_when_the_automated_review_has_parked_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(
            store, node,
            findingsReport: "# Pull request review findings\n\nThe lease fence is checked after the read, not before.",
            cts.Token);
        string projectName = seeded.ProjectName;

        await using (IDocumentSession session = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), NewWorktrees(), cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(seeded.RunId, cts.Token))!;
        string prompt = ReadPrompt(run);
        prompt.Should().Contain("The lease fence is checked after the read, not before.");
        prompt.Should().Contain("verbatim", "the report is quoted rather than summarised for the reviewer");
        prompt.Should().NotContain(
            "has not produced a findings report yet",
            "the absence wording must not appear alongside a report that is right there");
    }

    [Fact]
    public async Task The_briefing_says_so_when_no_findings_report_exists_yet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        string projectName = seeded.ProjectName;

        await using (IDocumentSession session = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), NewWorktrees(), cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(seeded.RunId, cts.Token))!;
        string prompt = ReadPrompt(run);
        prompt.Should().Contain(
            "has not produced a findings report yet",
            "an absent report is said out loud — a silent empty section reads as 'the machines found nothing'");
        prompt.Should().Contain("normal way to run a lap", "getting there first is not a missing prerequisite");
    }

    /// <summary>
    /// The briefing's own ruling, checked as a property rather than a phrase: it states what is
    /// there and stops. A briefing that opened with scenarios or a review order would hand the
    /// reviewer the platform's judgment in place of their own, which is the thing #149's design
    /// ruling is specifically about.
    /// </summary>
    [Fact]
    public async Task The_briefing_states_the_facts_and_volunteers_no_review_direction()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        string projectName = seeded.ProjectName;

        await using (IDocumentSession session = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), NewWorktrees(), cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(seeded.RunId, cts.Token))!;
        string prompt = ReadPrompt(run);

        prompt.Should().Contain("src/Hall9k.Daemon", "the surfaces touched are named");
        prompt.Should().Contain("+120/-18", "the blast radius is the arithmetic of the diff");
        prompt.Should().Contain("no conclusion yet", "a check still running has concluded nothing and says so");
        prompt.Should().Contain("Do not open with test scenarios");
        prompt.Should().Contain("Never commit to or push this pull request's branch");
        prompt.Should().Contain("h9k pr approve");
        prompt.Should().Contain("h9k pr request-changes");
        prompt.Should().Contain("never on its own");
    }

    [Fact]
    public async Task The_push_guard_lands_in_the_worktree_and_in_the_settings_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        string projectName = seeded.ProjectName;

        await using (IDocumentSession session = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), NewWorktrees(), cts.Token);
        }

        string guardPath = Path.Combine(seeded.WorktreePath, ".claude", "settings.local.json");
        File.Exists(guardPath).Should().BeTrue(
            "a session started in the worktree with no --settings flag must still be denied the push");
        string guard = await File.ReadAllTextAsync(guardPath, cts.Token);
        foreach (string denied in ClaudeSettingsFile.ReviewLapDeniedTools)
        {
            guard.Should().Contain(denied);
        }

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(seeded.RunId, cts.Token))!;
        string settings = await File.ReadAllTextAsync(
            RunPaths.SettingsFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory)), cts.Token);
        settings.Should().Contain("Bash(git push:*)", "the same denials travel with the settings file for a session started elsewhere");
        settings.Should().Contain("\"includeCoAuthoredBy\": false", "the platform's standing conventions still apply");
    }

    [Fact]
    public async Task Approve_posts_the_review_and_hands_the_task_to_the_daemon_to_finalize()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        RecordingGh gh = new(PullRequestJson, ReviewUrl);

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewVerdict.DeliverAsync(
                session, seeded.TaskId, ReviewerVerdict.Approved, "Reads clean; the fence is the right shape.",
                findings: [], new GitHubPullRequestSurface(gh.Runner), cts.Token);
            result.Should().Be(0);
        }

        JsonDocument payload = JsonDocument.Parse(gh.ReviewPayload!);
        payload.RootElement.GetProperty("event").GetString().Should().Be("APPROVE");
        payload.RootElement.GetProperty("commit_id").GetString().Should().Be(
            "0f1e2d3c4b5a69788796a5b4c3d2e1f001234567", "a review is an opinion about the head the reviewer read");
        payload.RootElement.GetProperty("body").GetString().Should().Be("Reads clean; the fence is the right shape.");
        payload.RootElement.TryGetProperty("comments", out _).Should().BeFalse("an approval carries no line comments");
        gh.ReviewEndpoint.Should().Be($"repos/{repository}/pulls/42/reviews");

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        task.ReviewerVerdict.Should().Be(ReviewerVerdict.Approved);
        task.ReviewLapOpen.Should().BeFalse("the verdict ends the lap");

        TaskDetails details = (await query.LoadAsync<TaskDetails>(seeded.TaskId, cts.Token))!;
        details.ReviewerVerdictReviewUrl.Should().Be(ReviewUrl);

        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(seeded.RunId, token: cts.Token))!;
        run.PrReviewDelivered.Should().BeTrue();
        run.State.Should().Be(
            RunState.UnderReview,
            "exactly where h9k review resolve --merge-ready leaves a pr-review run — PrReviewEngine finalizes it from here");
    }

    [Fact]
    public async Task Request_changes_posts_every_finding_as_a_line_comment_and_finalizes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        RecordingGh gh = new(PullRequestJson, ReviewUrl);

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewVerdict.DeliverAsync(
                session, seeded.TaskId, ReviewerVerdict.ChangesRequested, "Two real defects.",
                [
                    PullRequestReviewLineComment.Parse(
                        "src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:88: the fence is read after the load"),
                    PullRequestReviewLineComment.Parse(
                        "tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs:12: this asserts the fake, not the rule"),
                ],
                new GitHubPullRequestSurface(gh.Runner), cts.Token);
            result.Should().Be(0);
        }

        JsonDocument payload = JsonDocument.Parse(gh.ReviewPayload!);
        payload.RootElement.GetProperty("event").GetString().Should().Be("REQUEST_CHANGES");
        JsonElement comments = payload.RootElement.GetProperty("comments");
        comments.GetArrayLength().Should().Be(2);
        comments[0].GetProperty("path").GetString().Should().Be("src/Hall9k.Daemon/Closeout/CloseoutEngine.cs");
        comments[0].GetProperty("line").GetInt32().Should().Be(88);
        comments[0].GetProperty("side").GetString().Should().Be("RIGHT", "a lap reads the head, so a finding is about the line as it now stands");
        comments[0].GetProperty("body").GetString().Should().Be("the fence is read after the load");
        comments[1].GetProperty("line").GetInt32().Should().Be(12);

        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(seeded.TaskId, cts.Token))!;
        details.ReviewerVerdict.Should().Be(ReviewerVerdict.ChangesRequested);
        details.ReviewerVerdictFindings.Should().HaveCount(2)
            .And.Contain("tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs:12: this asserts the fake, not the rule");

        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(seeded.RunId, token: cts.Token))!;
        run.PrReviewDelivered.Should().BeTrue();
        run.State.Should().Be(RunState.UnderReview);
    }

    /// <summary>
    /// The post-before-record ordering, from the other side: a second verdict on a task that
    /// already delivered one must be refused before anything reaches GitHub, or a reviewer who
    /// re-runs the command out of habit posts a duplicate review under their own login.
    /// </summary>
    [Fact]
    public async Task A_second_verdict_is_refused_before_anything_is_posted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);

        await using (IDocumentSession first = store.LightweightSession())
        {
            await PullRequestReviewVerdict.DeliverAsync(
                first, seeded.TaskId, ReviewerVerdict.Approved, "Looks right.", findings: [],
                new GitHubPullRequestSurface(new RecordingGh(PullRequestJson, ReviewUrl).Runner), cts.Token);
        }

        RecordingGh second = new(PullRequestJson, ReviewUrl);
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewVerdict.DeliverAsync(
            session, seeded.TaskId, ReviewerVerdict.ChangesRequested, "Changed my mind.", findings: [],
            new GitHubPullRequestSurface(second.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>()).WithMessage("*already delivered*");
        second.ReviewPayload.Should().BeNull("nothing reaches GitHub once the lap has ended");
    }

    /// <summary>
    /// Closing the terminal is an ordinary way to leave a lap, exactly as it is for an
    /// interactive claim — so re-running <c>h9k pr review</c> has to re-enter. A lap-owned run
    /// sits at Dispatched for the lap's whole life because nothing ever moves it, so a run-state
    /// check applied to the reviewer's own lap refuses every re-entry and refuses it with a
    /// message about an automated session that is not running (self-review, round one: the check
    /// as first written did exactly that).
    /// </summary>
    [Fact]
    public async Task Re_running_the_lap_re_enters_the_reviewers_own_open_lap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string projectName = (await SeedProjectAsync(store, node, cts.Token)).Name;
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession first = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, first, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), worktrees, cts.Token);
        }

        await using (IDocumentSession again = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, again, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
                ScriptedGh(), worktrees, cts.Token);
            result.Should().Be(0, "re-entering a lap you already hold is the ordinary case, not a conflict");
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> tasks = await query.Query<TaskListItem>()
            .Where(item => item.ExternalReference == $"github-pr:{repository}#42")
            .ToListAsync(cts.Token);
        tasks.Should().ContainSingle("the re-entry attaches rather than adopting a second task");
        TaskDetails details = (await query.LoadAsync<TaskDetails>(tasks[0].Id, cts.Token))!;
        details.ReviewLapOpen.Should().BeTrue();
        worktrees.PrReviewCheckouts.Should().ContainSingle(
            "the second entry reuses the checkout the first one cut rather than fetching it again");
    }

    /// <summary>
    /// A lap opened without a checkout stays that way. Cutting one on re-entry would leave it
    /// named nowhere on the run stream, which is where every consumer that actually removes a
    /// checkout reads one from — so it would be a worktree nothing ever releases (self-review,
    /// round one, blast-radius sweep). Refused with the way out, rather than each reader growing
    /// a fallback.
    /// </summary>
    [Fact]
    public async Task Re_entering_a_no_worktree_lap_with_a_checkout_is_refused_rather_than_diverging()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string projectName = (await SeedProjectAsync(store, node, cts.Token)).Name;
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession opening = store.LightweightSession())
        {
            await PullRequestReviewCommand.RunAsync(
                store, opening,
                new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName, NoWorktree = true },
                ScriptedGh(), worktrees, cts.Token);
        }

        await using IDocumentSession again = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, again, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
            ScriptedGh(), worktrees, cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>()).WithMessage("*checkout is fixed for its life*");
        worktrees.PrReviewCheckouts.Should().BeEmpty("nothing was cut, so nothing is left for nobody to release");

        // And the same re-entry WITH the flag continues the lap, so the refusal above is a
        // divergence guard rather than a dead end.
        await using IDocumentSession continuing = store.LightweightSession();
        int result = await PullRequestReviewCommand.RunAsync(
            store, continuing,
            new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName, NoWorktree = true },
            ScriptedGh(), worktrees, cts.Token);
        result.Should().Be(0);
    }

    /// <summary>
    /// A pr-review run reaches UnderReview twice for opposite reasons — the conformance pass
    /// being dispatched, and a verdict landing — and only the run stream tells them apart. The
    /// refusal has to read the stream rather than the state, or a reviewer arriving while the
    /// machines are still reading is told their verdict was already recorded (self-review,
    /// round one).
    /// </summary>
    [Fact]
    public async Task A_lap_over_a_still_running_conformance_pass_is_refused_without_claiming_a_verdict_exists()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        string projectName = seeded.ProjectName;

        // The conformance lens dispatching is what moves a pr-review run to UnderReview
        // (RunDetails.Apply(PrReviewConformanceDispatched)) — the same state a delivered verdict
        // leaves it in.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(seeded.RunId, new PrReviewConformanceDispatched(
                seeded.RunId, DomainId.New(), 4242, Now, Now, AgentModel.Sonnet, "review-conformance-1"));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession attaching = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, attaching,
            new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
            ScriptedGh(), NewWorktrees(), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*conformance pass is still running*")
            .And.Message.Should().NotContain(
                "verdict has already been recorded", "no verdict exists — the machines are still reading");
    }

    /// <summary>
    /// The mirror of the refusal above, on the verdict side. A reviewer who read the pull request
    /// on GitHub can run <c>h9k pr approve</c> with no lap at all — and if the automated review
    /// is still mid-lens, the verdict lands on a run whose <c>DriveAsync</c> has already passed
    /// its own delivered check and will call <c>ComposeReportAndParkAsync</c> unconditionally
    /// when its lens returns. The delivered run then sits ReviewParked, which no sweep ever
    /// finalizes (<c>StrandedRunStates</c> is [UnderReview, Verifying]; adoption's ReviewParked
    /// arm only refreshes the lease), so the task strands Claimed with a posted verdict
    /// (independent pre-PR review, cycle 1, both lenses). Refused before the post, which is the
    /// whole reason the check lives on this side of the irreversible half.
    /// </summary>
    [Fact]
    public async Task A_verdict_on_a_still_running_conformance_pass_is_refused_before_anything_is_posted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(seeded.RunId, new PrReviewConformanceDispatched(
                seeded.RunId, DomainId.New(), 4242, Now, Now, AgentModel.Sonnet, "review-conformance-1"));
            await session.SaveChangesAsync(cts.Token);
        }

        RecordingGh gh = new(PullRequestJson, ReviewUrl);
        await using IDocumentSession delivering = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewVerdict.DeliverAsync(
            delivering, seeded.TaskId, ReviewerVerdict.Approved, "Read it on GitHub; looks right.",
            findings: [], new GitHubPullRequestSurface(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*conformance pass is still running*");
        gh.ReviewPayload.Should().BeNull("the refusal has to come before the irreversible half");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(seeded.RunId, token: cts.Token))!;
        run.PrReviewDelivered.Should().BeFalse("nothing was recorded either");
    }

    /// <summary>
    /// The lap's run and the lap's own event are two commits, so a Ctrl-C between them leaves a
    /// claimed task naming a Dispatched sentinel run with <c>ReviewLapOpen</c> still false.
    /// Re-running the command has to re-enter that run: the refusal that stood here credited the
    /// automated review's own adversarial pass with reading the pull request in that worktree,
    /// about a run with no process at all, and pointed away from every way out
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_lap_whose_opening_was_interrupted_re_enters_rather_than_being_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        SeededProject project = await SeedProjectAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();

        // Exactly what the interrupt leaves behind: the claim and the run committed, carrying the
        // lap's own session role, and no PullRequestReviewLapOpened after them.
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-lap-interrupted-wt-{runId:N}");
        Directory.CreateDirectory(worktreePath);
        scratchDirectories.Add(worktreePath);
        await using (IDocumentSession seeding = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, project.Id, "Review pull request acme/web#42", ["the verdict is submitted"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#42"), Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, Now);
            seeding.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seeding.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                worktreePath, "pr/42", ExecutorMode.Subscription, Now,
                RunDirectory: RunPaths.GlobalDirectory(runId),
                SessionName: SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.ReviewLap),
                DispatchingNodeId: node.NodeId));
            await seeding.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession again = store.LightweightSession();
        int result = await PullRequestReviewCommand.RunAsync(
            store, again, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = project.Name },
            ScriptedGh(), worktrees, cts.Token);

        result.Should().Be(0, "re-opening the lap is the recovery, not a conflict");
        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.ReviewLapOpen.Should().BeTrue("the second entry recorded the lap the first one never got to");
        details.ReviewLapRunId.Should().Be(runId, "it rode on the run the interrupted opening already cut");
        worktrees.PrReviewCheckouts.Should().BeEmpty(
            "the run's checkout is still on disk, so it is reused rather than cut a second time");
    }

    /// <summary>
    /// The same interrupted opening, reached from the verdict side by a reviewer who read the
    /// pull request anyway. The refusal stands — nothing on the task says a lap is open, and the
    /// daemon still reads that run as a dispatch that never started — but it has to name the lap
    /// it is actually looking at rather than credit the automated review's adversarial pass with
    /// occupying the worktree, which is a session nobody launched
    /// (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public async Task A_verdict_on_a_lap_whose_opening_was_interrupted_names_the_lap_and_not_an_automated_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        SeededProject project = await SeedProjectAsync(store, node, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seeding = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, project.Id, "Review pull request acme/web#42", ["the verdict is submitted"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#42"), Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, Now);
            seeding.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seeding.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                string.Empty, "pr/42", ExecutorMode.Subscription, Now,
                RunDirectory: RunPaths.GlobalDirectory(runId),
                SessionName: SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.ReviewLap),
                DispatchingNodeId: node.NodeId));
            await seeding.SaveChangesAsync(cts.Token);
        }

        RecordingGh gh = new(PullRequestJson, ReviewUrl);
        await using IDocumentSession delivering = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewVerdict.DeliverAsync(
            delivering, taskId, ReviewerVerdict.Approved, "Read it on GitHub; looks right.",
            findings: [], new GitHubPullRequestSurface(gh.Runner), cts.Token);

        string message = (await act.Should().ThrowAsync<DomainConflictException>()).Which.Message;
        message.Should().Contain("review lap of this node's own", "that is the run the reviewer is looking at");
        message.Should().Contain("h9k pr review", "re-entering is what records the lap the interrupt lost");
        message.Should().NotContain(
            "adversarial pass", "no automated session was ever launched under this run — asserting one guesses");
        gh.ReviewPayload.Should().BeNull("the refusal comes before the irreversible half");
    }

    /// <summary>
    /// The lap adopts and claims a task in whichever project it resolves, so guessing between
    /// several would put a review task on the wrong repository's board — and, worse, run gh from
    /// the wrong repository, which is what decides the bare number's meaning.
    /// </summary>
    [Fact]
    public async Task A_multi_project_install_refuses_to_guess_which_repository_the_pull_request_is_in()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        await SeedProjectAsync(store, node, cts.Token);
        await SeedProjectAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, session, new PullRequestReviewCommand.Settings { PullRequest = "42" },
            RecordingProcessRunner.NeverInvoked(), NewWorktrees(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*Pass --project*");
    }

    /// <summary>
    /// A dead automated review leaves <c>CurrentRunId</c> standing (<c>TaskFailed</c> moves only
    /// the state), so the lap's refusal reaches its catch-all arm — and the shared "open the lap
    /// once the automated review parks its findings report" suffix named a route that run will
    /// never take (independent pre-PR review, cycle 1, adversarial lens). Every reason now
    /// carries its own way out, and a terminal run's is <c>h9k task retry</c>.
    /// </summary>
    [Fact]
    public async Task A_lap_over_a_terminal_run_is_pointed_at_task_retry_rather_than_a_park_that_never_comes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(seeded.RunId, new RunFailed(
                seeded.RunId, "Agent process died without a result.", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession attaching = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, attaching,
            new PullRequestReviewCommand.Settings { PullRequest = "42", Project = seeded.ProjectName },
            ScriptedGh(), NewWorktrees(), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*will never park a findings report*")
            .And.Message.Should().Contain($"h9k task retry {seeded.TaskId}");
    }

    /// <summary>
    /// The verdict side's own copy of that shape, refused the same way — a reviewer whose
    /// automated run died must not be told to wait for a park either.
    /// </summary>
    [Fact]
    public async Task A_verdict_on_a_terminal_run_is_pointed_at_task_retry_and_posts_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        RecordingGh gh = new(PullRequestJson, ReviewUrl);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(seeded.RunId, new RunFailed(
                seeded.RunId, "Agent process died without a result.", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession delivering = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewVerdict.DeliverAsync(
            delivering, seeded.TaskId, ReviewerVerdict.Approved, "Reads clean.", findings: [],
            new GitHubPullRequestSurface(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*will never park a findings report*")
            .And.Message.Should().Contain($"h9k task retry {seeded.TaskId}");
        gh.ReviewPayload.Should().BeNull("nothing was posted");
    }

    /// <summary>
    /// GitHub answers a review on one's own pull request with a 422, not the 403 it reads like —
    /// and on a single-login install, which is the ordinary one, the daemon opened the pull
    /// request under the very login the reviewer posts with. The generic 422 explanation blamed
    /// a line comment or a moved head for it (independent pre-PR review, cycle 1, adversarial
    /// lens).
    /// </summary>
    [Fact]
    public async Task A_review_refused_as_the_authors_own_says_that_rather_than_blaming_a_line()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        ProcessRunner refusingGh = (_, arguments, _, _) => Task.FromResult(
            arguments.Contains("--input")
                ? new ProcessResult(
                    1, string.Empty,
                    "gh: Unprocessable Entity (HTTP 422) Can not approve your own pull request")
                : new ProcessResult(0, PullRequestJson, string.Empty));

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewVerdict.DeliverAsync(
            session, seeded.TaskId, ReviewerVerdict.Approved, "Reads clean.", findings: [],
            new GitHubPullRequestSurface(refusingGh), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*that pull request's own author*")
            .And.Message.Should().NotContain(
                "the head moved", "a line comment and a moved head are the other 422, not this one");

        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(seeded.TaskId, cts.Token))!;
        details.ReviewerVerdict.Should().Be(
            ReviewerVerdict.Unknown, "a post that failed records nothing — the reviewer re-runs");
    }

    /// <summary>
    /// The race the lap's two stream fences exist for (independent pre-PR review, cycle 1,
    /// adversarial lens): the admission check reads the run as parked, the checkout then takes a
    /// git fetch, and in that window the daemon's token-budget retry sweep reads
    /// <c>ReviewLapOpen == false</c> — the lap's own event has not committed yet — and resumes
    /// the automated session into the very checkout the reviewer is about to be handed. The cut
    /// is the only place a test can stand inside that window, so the daemon's append is made from
    /// there. Nothing of the lap may be recorded on the way out, which is what makes re-running
    /// the command the whole remedy.
    /// </summary>
    [Fact]
    public async Task A_run_that_moved_while_the_lap_was_being_prepared_refuses_and_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);

        // A pruned checkout is what sends the lap through the re-cut — the slow half, and the one
        // whose window this is about.
        Directory.Delete(seeded.WorktreePath, recursive: true);
        FakeReviewWorktrees worktrees = NewWorktrees();
        worktrees.WhileCuttingTheCheckout = async () =>
        {
            await using IDocumentSession daemon = store.LightweightSession();
            daemon.Events.Append(seeded.RunId, new RunResumed(seeded.RunId, 4242, Now, Now, "resumed-review"));
            await daemon.SaveChangesAsync(cts.Token);
        };

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = seeded.ProjectName },
            ScriptedGh(), worktrees, cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*moved while this lap was being prepared*");

        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(seeded.TaskId, cts.Token))!;
        details.ReviewLapOpen.Should().BeFalse(
            "a lap recorded over a run a live agent session was just resumed in would shield that run from "
            + "adoption and hand the reviewer a checkout being overwritten under them");
        details.ReviewLapRunId.Should().BeNull("nothing of the lap was recorded at all");
    }

    /// <summary>
    /// The other fence: the task stream's own version, carried as the lap record's expected
    /// version, so two laps opening on the same pull request at once cannot both believe they
    /// opened one. The first one's own <c>PullRequestReviewLapOpened</c> is what the second one
    /// loses to.
    /// </summary>
    [Fact]
    public async Task A_task_that_changed_while_the_lap_was_being_prepared_refuses_and_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);

        Directory.Delete(seeded.WorktreePath, recursive: true);
        FakeReviewWorktrees worktrees = NewWorktrees();
        worktrees.WhileCuttingTheCheckout = async () =>
        {
            await using IDocumentSession other = store.LightweightSession();
            other.Events.Append(seeded.TaskId, new PullRequestReviewLapOpened(
                seeded.TaskId, seeded.RunId, "somewhere-else", string.Empty, Now, node.OwnerId));
            await other.SaveChangesAsync(cts.Token);
        };

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = seeded.ProjectName },
            ScriptedGh(), worktrees, cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*changed while this lap was being prepared*");

        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(seeded.TaskId, cts.Token))!;
        details.ReviewLapWorktreePath.Should().Be(
            "somewhere-else", "the lap that got there first is the one on the task, undisturbed");
    }

    /// <summary>
    /// A cut that succeeds and a run-stream commit that then fails is the one window in which a
    /// checkout exists at a path no run records — and every consumer that ever removes a
    /// pr-review checkout enumerates recorded run paths, so nothing else in the platform could
    /// ever have collected it (independent pre-PR review, cycle 1, adversarial lens). The
    /// collision is a run stream already standing at the id this lap is about to start, which is
    /// what a real connection drop between the fetch and the commit looks like from here.
    /// </summary>
    [Fact]
    public async Task A_checkout_cut_for_a_run_that_never_committed_is_released_rather_than_leaked()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string projectName = (await SeedProjectAsync(store, node, cts.Token)).Name;
        FakeReviewWorktrees worktrees = NewWorktrees();
        worktrees.WhileCuttingTheCheckoutForRequest = async request =>
        {
            await using IDocumentSession collision = store.LightweightSession();
            collision.Events.StartStream<RunAggregate>(
                request.RunId, new RunResumed(request.RunId, 4242, Now, Now, "already-there"));
            await collision.SaveChangesAsync(cts.Token);
        };

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => PullRequestReviewCommand.RunAsync(
            store, session, new PullRequestReviewCommand.Settings { PullRequest = "42", Project = projectName },
            ScriptedGh(), worktrees, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>();

        worktrees.PrReviewCheckouts.Should().ContainSingle().Which.Should().Be(42, "the cut itself succeeded");
        worktrees.Removed.Should().ContainSingle(
            "the checkout is on no run, so this command is the last thing that will ever know where it is");
        worktrees.TrackingRefsDeleted.Should().ContainSingle().Which.Should().Be(
            42, "the ref the cut fetched goes with it, exactly as PrReviewEngine.FinalizeAsync releases both");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private FakeReviewWorktrees NewWorktrees()
    {
        FakeReviewWorktrees worktrees = new(scratchDirectories);
        return worktrees;
    }

    private static string ReadPrompt(RunDetails run) =>
        File.ReadAllText(RunPaths.PromptFile(RunPaths.ResolveCurrentDirectory(run.RunDirectory)));

    /// <summary>
    /// gh, scripted: every <c>pr view</c> answers with <see cref="PullRequestJson"/>, whichever
    /// of the two field lists asked for it.
    /// </summary>
    private ProcessRunner ScriptedGh() => (_, _, _, _) =>
        Task.FromResult(new ProcessResult(0, PullRequestJson, string.Empty));

    /// <summary>
    /// gh, scripted and recording the review it was asked to post. The payload is read out of the
    /// <c>--input</c> file WHILE the call is in flight, which is the only moment it exists: the
    /// poster deletes it in a finally, and that is the behaviour under test as much as the
    /// payload's shape is.
    /// </summary>
    private sealed class RecordingGh(string pullRequestJson, string reviewUrl)
    {
        public string? ReviewPayload { get; private set; }

        public string? ReviewEndpoint { get; private set; }

        public ProcessRunner Runner => (_, arguments, _, _) =>
        {
            int inputIndex = arguments.ToList().IndexOf("--input");
            if (inputIndex < 0)
            {
                return Task.FromResult(new ProcessResult(0, pullRequestJson, string.Empty));
            }

            ReviewEndpoint = arguments[1];
            ReviewPayload = File.ReadAllText(arguments[inputIndex + 1]);
            return Task.FromResult(new ProcessResult(
                0, $$"""{"html_url": "{{reviewUrl}}"}""", string.Empty));
        };
    }

    /// <summary>The URL gh answers the review post with, and what the verdict must record verbatim.</summary>
    private string ReviewUrl => $"https://github.com/{repository}/pull/42#pullrequestreview-1";

    /// <summary>
    /// git, faked down to the two verbs a lap ever uses. The checkout is a real directory,
    /// because the push guard writes into it and the reuse check reads whether it is there —
    /// faking those away would leave the two facts this test most needs unasserted.
    /// </summary>
    private sealed class FakeReviewWorktrees(List<string> scratch) : IWorktreeManager
    {
        public List<int> PrReviewCheckouts { get; } = [];

        /// <summary>
        /// Run while a checkout is being cut — the window the daemon's own sweeps can act in, and
        /// the only place a test can stand inside it (the cut is where a real lap spends a git
        /// fetch).
        /// </summary>
        public Func<Task>? WhileCuttingTheCheckout { get; set; }

        /// <summary>
        /// The same window with the request in hand: colliding with the run-stream commit that
        /// FOLLOWS a cut needs the run id the cut was for, and this is where a test can read it.
        /// </summary>
        public Func<PrReviewWorktreeRequest, Task>? WhileCuttingTheCheckoutForRequest { get; set; }

        /// <summary>Every checkout this fake was asked to remove, so a leak can be asserted against rather than inferred.</summary>
        public List<string> Removed { get; } = [];

        /// <summary>Every pull request whose tracking ref this fake was asked to delete.</summary>
        public List<int> TrackingRefsDeleted { get; } = [];

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a review lap never cuts a branch of its own");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a review lap never checks out an existing branch");

        public async Task<Worktree> CreatePrReviewCheckoutAsync(
            PrReviewWorktreeRequest request, CancellationToken cancellationToken)
        {
            PrReviewCheckouts.Add(request.PullRequestNumber);
            if (WhileCuttingTheCheckout is { } interference)
            {
                await interference();
            }

            if (WhileCuttingTheCheckoutForRequest is { } collision)
            {
                await collision(request);
            }

            string path = Path.Combine(Path.GetTempPath(), $"hall9k-lap-wt-{request.RunId:N}");
            Directory.CreateDirectory(path);
            scratch.Add(path);
            return new Worktree(path, $"pr/{request.PullRequestNumber}", "refs/remotes/origin/pr-review/42");
        }

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken)
        {
            Removed.Add(worktreePath);
            return Task.CompletedTask;
        }

        public Task DeletePrReviewTrackingRefAsync(
            string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken)
        {
            TrackingRefsDeleted.Add(pullRequestNumber);
            return Task.CompletedTask;
        }

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "not a real repository"));

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoLock.Instance);
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Seeded(Guid ProjectId, string ProjectName, Guid TaskId, Guid RunId, string WorktreePath);

    private sealed record SeededProject(Guid Id, string Name);

    /// <summary>
    /// A project of this test's own, named uniquely. Every test names it explicitly through
    /// <c>--project</c> rather than relying on the lap's own single-project inference: the
    /// Postgres fixture is shared across this class, so by the second test there are several
    /// registered and the inference correctly refuses to guess between them
    /// (<see cref="A_multi_project_install_refuses_to_guess_which_repository_the_pull_request_is_in"/>
    /// is what covers that refusal).
    /// </summary>
    private async Task<SeededProject> SeedProjectAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        string name = $"lap-{projectId:N}";
        await using IDocumentSession session = store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), name,
            Path.Combine(Path.GetTempPath(), $"hall9k-lap-repo-{projectId:N}"),
            new Uri($"https://github.com/{repository}"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
        await session.SaveChangesAsync(cancellationToken);
        return new SeededProject(projectId, name);
    }

    /// <summary>
    /// The state the lap is built around: a pr-review task this node already holds, whose
    /// automated run has read the pull request and parked. <paramref name="findingsReport"/>
    /// writes the merged report into the run's own directory when the case under test has one —
    /// the file, not the park, is what the briefing reads.
    /// </summary>
    private async Task<Seeded> SeedParkedPrReviewTaskAsync(
        DocumentStore store, NodeContext node, string? findingsReport, CancellationToken cancellationToken)
    {
        SeededProject project = await SeedProjectAsync(store, node, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-lap-seeded-wt-{runId:N}");
        Directory.CreateDirectory(worktreePath);
        scratchDirectories.Add(worktreePath);
        string runDirectory = RunPaths.GlobalDirectory(runId);
        Directory.CreateDirectory(runDirectory);

        if (findingsReport is not null)
        {
            await File.WriteAllTextAsync(RunPaths.ReviewFindingsFile(runDirectory, 1), findingsReport, cancellationToken);
        }

        await using IDocumentSession session = store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, project.Id, "Teach the closeout monitor to read a rebase conflict",
                ["every finding names a file and a line"], TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#42"), Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(), worktreePath, "pr/42",
                ExecutorMode.Subscription, Now, RunDirectory: runDirectory, PrReviewBaseRefName: "main"),
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId, "Pull request review complete. Findings ready.", Now));
        await session.SaveChangesAsync(cancellationToken);

        return new Seeded(project.Id, project.Name, taskId, runId, worktreePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        foreach (string directory in scratchDirectories.Append(home))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }
    }
}
