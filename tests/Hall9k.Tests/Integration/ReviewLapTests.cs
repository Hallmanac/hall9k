using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
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
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A reviewer's own review lap end to end (Decisions Log #149) and the resolve command that runs
/// inside it, against a real store and a scripted <c>gh</c>, sharing one container. Both halves
/// call their commands directly — <c>PullRequestReviewCommand.RunAsync</c>,
/// <c>PullRequestReviewVerdict.DeliverAsync</c>, and
/// <see cref="ReviewResolveCommand.ResolvePrReviewAsync"/> — rather than through
/// <c>CliStore.Open</c>'s ambient connection, which is the only test seam this codebase's CLI
/// commands have. The pr-review verdict rules had no coverage at all before the resolve half
/// (cycle-1 conformance finding, <c>PrReviewEngine.cs:50</c>).
/// <para>
/// The two were separate classes for twenty-three tests each. Every assertion here is scoped
/// either to a stream it seeded by id or to <see cref="repository"/> — a repository name of this
/// instance's own, for the reason that field's own doc comment gives — so the halves are as
/// invisible to each other as two tests in one class already were.
/// </para>
/// </summary>
// The lap, the verdict, and the resolve command's merge-ready path all ring the doorbell
// (Hall9k.Cli.Infrastructure.Doorbell), which resolves its connection off
// HALL9K_CONNECTION_STRING rather than this fixture, and the lap and verdict write artifacts
// under HALL9K_HOME. All of it is process-wide, so this joins the Hall9kHome collection every
// other test that redirects them does.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ReviewLapTests : IClassFixture<PostgresFixture>, IDisposable
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

    public ReviewLapTests(PostgresFixture postgres)
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
    /// The note and every line comment go out in one review under the reviewer's own login, so
    /// both halves obey the project's writing conventions (task 412afe6c). The review lap's own
    /// briefing promises exactly that to the session drafting them, and a check that covered only
    /// the note would leave the half a reviewer reads in the diff unguarded.
    /// </summary>
    [Fact]
    public async Task An_em_dash_in_the_note_or_a_finding_never_reaches_the_posted_review()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedParkedPrReviewTaskAsync(store, node, findingsReport: null, cts.Token);
        RecordingGh gh = new(PullRequestJson, ReviewUrl);

        await using (IDocumentSession session = store.LightweightSession())
        {
            await PullRequestReviewVerdict.DeliverAsync(
                session, seeded.TaskId, ReviewerVerdict.ChangesRequested,
                "Two real defects — both in the closeout path.",
                [
                    PullRequestReviewLineComment.Parse(
                        "src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:88: the fence is read after the load — "
                        + "that inverts the guard."),
                ],
                new GitHubPullRequestSurface(gh.Runner), cts.Token);
        }

        JsonDocument payload = JsonDocument.Parse(gh.ReviewPayload!);
        string body = payload.RootElement.GetProperty("body").GetString()!;
        string comment = payload.RootElement.GetProperty("comments")[0].GetProperty("body").GetString()!;
        body.Should().NotContain("—").And.Be("Two real defects, both in the closeout path.");
        comment.Should().NotContain("—")
            .And.Be("the fence is read after the load; that inverts the guard.");

        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(seeded.TaskId, cts.Token))!;
        details.ReviewerVerdictNote.Should().NotContain("—", "the stream says what the reviewer posted");
        details.ReviewerVerdictFindings.Should().ContainSingle().Which.Should().NotContain("—");
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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


    private FakeReviewWorktrees NewWorktrees()
    {
        FakeReviewWorktrees worktrees = new(scratchDirectories);
        return worktrees;
    }

    // ── the scoped second lap: h9k pr review --since-my-review ──

    /// <summary>
    /// gh and git, scripted for a scoped lap (task: a pr-review task stays open while the pull
    /// request's review threads are unresolved). Four calls reach it and each answers a different
    /// question, so it dispatches on what was asked rather than on call order — which is the
    /// honest shape anyway: the composer is free to reorder its reads.
    /// </summary>
    private ProcessRunner ScopedLapGh(string conversationJson) => (fileName, arguments, _, _) =>
    {
        List<string> argv = [.. arguments];
        if (fileName == "git")
        {
            return Task.FromResult(argv.Contains("log")
                ? new ProcessResult(0, "9a1b2c3 answer the review\n", string.Empty)
                : new ProcessResult(
                    0,
                    "diff --git a/src/One.cs b/src/One.cs\n@@ -1 +1 @@\n-old\n+new\n", string.Empty));
        }

        if (argv.Contains("graphql"))
        {
            return Task.FromResult(new ProcessResult(0, conversationJson, string.Empty));
        }

        return Task.FromResult(argv.Contains("user")
            ? new ProcessResult(0, "brian\n", string.Empty)
            : new ProcessResult(0, PullRequestJson, string.Empty));
    };

    /// <summary>
    /// The review conversation as GraphQL reports it once the author has answered: the reviewer's
    /// own thread, its opener theirs and one reply theirs, plus a thread of somebody else's that
    /// this lap must not read as its own.
    /// </summary>
    private static string ConversationWithOneReply() =>
        """
        {"data":{"repository":{"pullRequest":{
          "state":"OPEN","merged":false,"closed":false,"headRefOid":"9a1b2c3d4e5f",
          "commits":{"totalCount":4},
          "reviewThreads":{"nodes":[
            {"id":"T1","isResolved":false,"path":"src/One.cs","line":12,
             "comments":{"totalCount":2,"nodes":[
               {"author":{"login":"brian"},"body":"the fence is checked after the read","createdAt":"2026-09-07T13:20:00Z"},
               {"author":{"login":"someone-else"},"body":"moved it ahead of the read in 9a1b2c3","createdAt":"2026-09-08T12:06:00Z"}]}},
            {"id":"T2","isResolved":false,"path":"src/Two.cs","line":3,
             "comments":{"totalCount":1,"nodes":[
               {"author":{"login":"copilot"},"body":"a bot's own concern","createdAt":"2026-09-07T13:25:00Z"}]}}
          ],"pageInfo":{"hasNextPage":false}},
          "reviewRequests":{"nodes":[]}
        }}}}
        """;

    /// <summary>
    /// What an author leaves behind when they click re-request review and nothing else: the
    /// reviewer's thread exactly as they left it, no reply, no resolution, no push. The one shape
    /// that summons a scoped lap with an empty packet in BOTH halves.
    /// </summary>
    private static string ConversationWithNothingButAReReviewRequest() =>
        """
        {"data":{"repository":{"pullRequest":{
          "state":"OPEN","merged":false,"closed":false,"headRefOid":"0f1e2d3c4b5a",
          "commits":{"totalCount":3},
          "reviewThreads":{"nodes":[
            {"id":"T1","isResolved":false,"path":"src/One.cs","line":12,
             "comments":{"totalCount":1,"nodes":[
               {"author":{"login":"brian"},"body":"the fence is checked after the read","createdAt":"2026-09-07T13:20:00Z"}]}}
          ],"pageInfo":{"hasNextPage":false}},
          "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"User","login":"brian"}}]}
        }}}}
        """;

    /// <summary>
    /// The lap the re-review wake now summons (independent pre-PR review, cycle 1, adversarial
    /// lens): nothing was said, nothing was pushed, and the packet has nothing in either half —
    /// so it must say the re-request is what asked for it, rather than sending the session hunting
    /// a cause in a code half that is empty too.
    /// </summary>
    [Fact]
    public async Task A_scoped_lap_summoned_by_a_re_review_request_alone_says_that_is_what_asked_for_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();
        // Nothing was pushed either, so git has no commits and no diff to report.
        ProcessRunner quietGit = (fileName, arguments, workingDirectory, token) => fileName == "git"
            ? Task.FromResult(new ProcessResult(0, string.Empty, string.Empty))
            : ScopedLapGh(ConversationWithNothingButAReReviewRequest())(
                fileName, arguments, workingDirectory, token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings
                {
                    PullRequest = "42", Project = seeded.ProjectName, SinceMyReview = true,
                },
                quietGit, worktrees, cts.Token);
            result.Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        RunDetails run = (await query.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!;
        string prompt = ReadPrompt(run);

        prompt.Should().Contain(
            "The author has re-requested this review",
            "it is the only thing that asked for this lap, and an empty packet cannot imply it");
        prompt.Should().Contain(
            "may be nothing more than the re-request above",
            "and the empty-thread line must not send the session looking for a cause in the code half");
        prompt.Should().NotContain(
            "Whatever prompted this lap is in the code half below",
            "which would be false: the code half is empty too");
    }

    [Fact]
    public async Task The_scoped_laps_packet_holds_only_the_deltas_since_the_reviewers_own_review()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings
                {
                    PullRequest = "42", Project = seeded.ProjectName, SinceMyReview = true,
                },
                ScopedLapGh(ConversationWithOneReply()), worktrees, cts.Token);
            result.Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        task.State.Should().Be(TaskState.Claimed, "a scoped lap claims the waiting task rather than minting one");
        RunDetails run = (await query.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!;
        string prompt = ReadPrompt(run);

        prompt.Should().Contain("scoped lap");
        prompt.Should().Contain(
            "moved it ahead of the read in 9a1b2c3", "the author's reply is carried verbatim");
        prompt.Should().Contain("src/One.cs:12", "the reply is placed where the thread is");
        prompt.Should().Contain("9a1b2c3 answer the review", "the commits pushed since the review are named");
        prompt.Should().Contain("+new", "and their diff is in the packet");
        prompt.Should().Contain("h9k pr approve", "the lap ends the same two ways an ordinary one does");

        prompt.Should().NotContain(
            "a bot's own concern", "somebody else's thread is not what a scoped lap is scoped to");
        prompt.Should().NotContain(
            "+120/-18", "the blast radius was read in the first lap and is deliberately absent");
        prompt.Should().NotContain(
            "no conclusion yet", "so are the CI results");
        prompt.Should().NotContain(
            "The lease fence is checked after the read, not before.",
            "and so is the earlier findings report");
    }

    /// <summary>
    /// The contradiction the first cut shipped (self-review, round one): the needs-you line says
    /// "1 reply in 1 thread", the reviewer runs the scoped lap it names, and the packet says
    /// nothing has moved — because the poll watermark the packet diffed against had just been
    /// advanced past those very replies, which is what stops the same replies notifying twice. The
    /// review's own anchor is what the packet has to diff against, and it does not move.
    /// </summary>
    [Fact]
    public async Task The_scoped_packet_survives_the_polls_own_re_baselining()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);

        // The poll that notified: it observed the reply AND re-baselined its own watermark onto it,
        // exactly as PrReviewFollowThroughEngine does in one transaction.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                seeded.TaskId, token: cts.Token))!;
            PullRequestReviewFollowThroughObserved observed = TaskDecider.ObservePrReviewFollowThrough(
                task, "brian", [new PrReviewThreadWatermark("T1", 1, IsResolved: false)],
                reReviewRequested: false, headSha: "9a1b2c3d4e5f", commitCount: 4, Now.AddHours(3));
            task.Apply(observed);
            PullRequestReviewAuthorResponded responded = TaskDecider.RecordPrReviewAuthorResponse(
                task, "The author answered your review.", replyCount: 1, threadsWithReplies: 1,
                newCommitCount: 1, headMoved: true, reReviewNewlyRequested: false,
                interactiveSessionAddress: null, Now.AddHours(3));
            session.Events.Append(seeded.TaskId, observed, responded);
            await session.SaveChangesAsync(cts.Token);
        }

        FakeReviewWorktrees worktrees = NewWorktrees();
        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings
                {
                    PullRequest = "42", Project = seeded.ProjectName, SinceMyReview = true,
                },
                ScopedLapGh(ConversationWithOneReply()), worktrees, cts.Token);
            result.Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate claimed = (await query.Events.AggregateStreamAsync<TaskAggregate>(
            seeded.TaskId, token: cts.Token))!;
        string prompt = ReadPrompt((await query.LoadAsync<RunDetails>(claimed.CurrentRunId!.Value, cts.Token))!);

        prompt.Should().Contain(
            "moved it ahead of the read in 9a1b2c3",
            "the reply the needs-you line was about is still what the scoped packet shows");
        prompt.Should().Contain(
            "9a1b2c3 answer the review",
            "and the commits are diffed from the head the review was posted against, not the last poll's");
        prompt.Should().NotContain(
            "None of the reviewer's own threads have moved",
            "which is what the poll watermark would have said");
    }

    /// <summary>
    /// The conversation as GraphQL reports it when both of the provider's own caps bit: the thread
    /// page has a next page, and the reviewer's one readable thread carries 140 comments of which
    /// two came back. The read is short in two different ways and the packet has to say both.
    /// </summary>
    private static string ConversationReadShortByBothCaps() =>
        """
        {"data":{"repository":{"pullRequest":{
          "state":"OPEN","merged":false,"closed":false,"headRefOid":"9a1b2c3d4e5f",
          "commits":{"totalCount":4},
          "reviewThreads":{"nodes":[
            {"id":"T1","isResolved":false,"path":"src/One.cs","line":12,
             "comments":{"totalCount":140,"nodes":[
               {"author":{"login":"brian"},"body":"the fence is checked after the read","createdAt":"2026-09-07T13:20:00Z"},
               {"author":{"login":"someone-else"},"body":"moved it ahead of the read in 9a1b2c3","createdAt":"2026-09-08T12:06:00Z"}]}}
          ],"pageInfo":{"hasNextPage":true}},
          "reviewRequests":{"nodes":[]}
        }}}}
        """;

    /// <summary>
    /// A short read presented as a complete one is the one failure a scoped packet cannot recover
    /// from: the reviewer reads "nothing moved" and stops (independent pre-PR review, cycle 2).
    /// Both of the provider's caps are stated out loud instead — the thread page, which makes every
    /// thread count a floor, and the per-thread comment page, whose unread tail is the NEWEST
    /// comments and so the very ones the lap was opened to read.
    /// </summary>
    [Fact]
    public async Task A_scoped_packet_read_short_by_the_providers_caps_says_so_rather_than_reading_as_complete()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession session = store.LightweightSession())
        {
            (await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings
                {
                    PullRequest = "42", Project = seeded.ProjectName, SinceMyReview = true,
                },
                ScopedLapGh(ConversationReadShortByBothCaps()), worktrees, cts.Token))
                .Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        string prompt = ReadPrompt((await query.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!);

        prompt.Should().Contain(
            "more review threads than the provider's own page cap",
            "a capped thread page makes every count below it a floor, and silence would read as 'these are all your threads'");
        prompt.Should().Contain(
            "138 further comment(s) on this thread are past the provider's own page cap",
            "the tail the read could not carry is named with its size rather than dropped");
        prompt.Should().Contain(
            "the most recent ones", "and named as the newest comments, which is what makes losing them silently worst");
        prompt.Should().Contain(
            "moved it ahead of the read in 9a1b2c3", "what WAS read is still carried verbatim");
    }

    /// <summary>
    /// A thread whose whole reply tail fell past the comment cap must never be counted unchanged:
    /// ReplyCountFor already counts that tail as replies, so the board says the author answered,
    /// and a packet calling the same thread unchanged is the one disagreement the shared
    /// FirstReplyIndexFor rule exists to make impossible.
    /// </summary>
    [Fact]
    public async Task A_thread_whose_replies_all_fell_past_the_comment_cap_is_never_reported_unchanged()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();

        // The reviewer's own comment is the LAST one the read carried, so every reply is in the
        // tail: "since their last word" reads as nothing at all off the comments that came back.
        string conversation =
            """
            {"data":{"repository":{"pullRequest":{
              "state":"OPEN","merged":false,"closed":false,"headRefOid":"9a1b2c3d4e5f",
              "commits":{"totalCount":4},
              "reviewThreads":{"nodes":[
                {"id":"T1","isResolved":false,"path":"src/One.cs","line":12,
                 "comments":{"totalCount":103,"nodes":[
                   {"author":{"login":"brian"},"body":"the fence is checked after the read","createdAt":"2026-09-07T13:20:00Z"},
                   {"author":{"login":"brian"},"body":"and here too","createdAt":"2026-09-07T13:21:00Z"}]}}
              ],"pageInfo":{"hasNextPage":false}},
              "reviewRequests":{"nodes":[]}
            }}}}
            """;

        await using (IDocumentSession session = store.LightweightSession())
        {
            (await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings
                {
                    PullRequest = "42", Project = seeded.ProjectName, SinceMyReview = true,
                },
                ScopedLapGh(conversation), worktrees, cts.Token))
                .Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        string prompt = ReadPrompt((await query.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!);

        prompt.Should().NotContain(
            "None of the reviewer's own threads have moved",
            "the board counts those 101 comments as replies, and the packet must not call the same thread quiet");
        prompt.Should().Contain("src/One.cs:12", "the thread is listed");
        prompt.Should().Contain(
            "101 further comment(s) on this thread are past the provider's own page cap",
            "with the size of what it could not show");
    }

    /// <summary>
    /// Closing the terminal is an ordinary way to leave a scoped lap too (independent pre-PR
    /// review, cycle 1, adversarial lens). The refusal that guarded <c>--since-my-review</c> read
    /// the task's STATE, which the lap's own claim has already moved to Claimed — so re-running
    /// the identical command was refused with a message saying no review of theirs was being
    /// followed through, while it was, and the scoped packet stayed unreachable until a verdict or
    /// an abandon ended the lap.
    /// </summary>
    [Fact]
    public async Task Re_running_a_scoped_lap_re_enters_it_rather_than_refusing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();
        PullRequestReviewCommand.Settings scoped = new()
        {
            PullRequest = "42", Project = seeded.ProjectName, SinceMyReview = true,
        };

        await using (IDocumentSession session = store.LightweightSession())
        {
            (await PullRequestReviewCommand.RunAsync(
                store, session, scoped, ScopedLapGh(ConversationWithOneReply()), worktrees, cts.Token))
                .Should().Be(0);
        }

        await using (IDocumentSession again = store.LightweightSession())
        {
            (await PullRequestReviewCommand.RunAsync(
                store, again, scoped, ScopedLapGh(ConversationWithOneReply()), worktrees, cts.Token))
                .Should().Be(0, "re-entering a scoped lap you already hold is the ordinary case, not a conflict");
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        task.PrReviewFollowThroughOpen.Should().BeTrue("the wait is suspended by the lap, not ended by it");
        string prompt = ReadPrompt((await query.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!);
        prompt.Should().Contain("scoped lap")
            .And.Contain(
                "moved it ahead of the read in 9a1b2c3",
                "the re-entry composes the same packet the lap was opened for");
        worktrees.PrReviewCheckouts.Should().ContainSingle(
            "the second entry reuses the checkout the first one cut");
    }

    [Fact]
    public async Task An_ordinary_lap_on_a_waiting_review_reads_the_pull_request_whole()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Seeded seeded = await SeedWaitingPrReviewTaskAsync(store, node, cts.Token);
        FakeReviewWorktrees worktrees = NewWorktrees();

        await using (IDocumentSession session = store.LightweightSession())
        {
            int result = await PullRequestReviewCommand.RunAsync(
                store, session,
                new PullRequestReviewCommand.Settings { PullRequest = "42", Project = seeded.ProjectName },
                ScopedLapGh(ConversationWithOneReply()), worktrees, cts.Token);
            result.Should().Be(0);
        }

        await using IQuerySession query = store.QuerySession();
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(seeded.TaskId, token: cts.Token))!;
        RunDetails run = (await query.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!;
        string prompt = ReadPrompt(run);

        prompt.Should().NotContain("scoped lap");
        prompt.Should().Contain("+120/-18", "without the flag the whole pull request is read as it always was");
    }

    /// <summary>
    /// A pr-review task whose review is posted and whose follow-through has already been looked at
    /// once: the shape <c>PrReviewEngine.FinalizeAsync</c> plus one poll leaves behind. The
    /// observation is seeded rather than skipped because it is what fixes the review's own anchor
    /// — the resolution state a scoped lap reports a thread as having changed against — and its
    /// thread carries no reply yet, which is the state the review was posted in.
    /// </summary>
    private async Task<Seeded> SeedWaitingPrReviewTaskAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Seeded parked = await SeedParkedPrReviewTaskAsync(
            store, node, "The lease fence is checked after the read, not before.", cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            parked.TaskId, token: cancellationToken))!;
        PullRequestReviewFollowThroughOpened opened = TaskDecider.OpenPrReviewFollowThrough(
            task, parked.RunId, $"https://github.com/{repository}/pull/42", "0f1e2d3c4b5a", Now.AddHours(1));
        task.Apply(opened);
        PullRequestReviewFollowThroughObserved observed = TaskDecider.ObservePrReviewFollowThrough(
            task, "brian", [new PrReviewThreadWatermark("T1", 0, IsResolved: false)],
            reReviewRequested: false, headSha: "0f1e2d3c4b5a", commitCount: 3, Now.AddHours(2));
        session.Events.Append(parked.TaskId, opened, observed);
        session.Delete<TaskLease>(parked.TaskId);
        await session.SaveChangesAsync(cancellationToken);
        return parked;
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

    // ── h9k review resolve, inside the lap ──
    private static readonly DateTimeOffset ResolveThreadsNow = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Needs_fixes_is_refused_outright_on_a_pr_review_tasks_park()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;

        Func<Task> act = () => ReviewResolveCommand.ResolvePrReviewAsync(
            session, runId, taskId, fence,
            new ReviewResolveCommand.Settings { NeedsFixes = "Fix the thing" }, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*nothing here for a fix session to apply*");
    }

    [Fact]
    public async Task A_missing_merge_ready_verdict_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;

        Func<Task> act = () => ReviewResolveCommand.ResolvePrReviewAsync(
            session, runId, taskId, fence, new ReviewResolveCommand.Settings(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*Pass --merge-ready*");
    }

    [Fact]
    public async Task Merge_ready_delivers_the_review_without_ever_opening_a_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        // Resolving merge-ready rings the doorbell, which resolves its connection off
        // HALL9K_CONNECTION_STRING rather than this fixture, so it has to be pointed at the
        // fixture for the duration of the call.
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;
            int result = await ReviewResolveCommand.ResolvePrReviewAsync(
                session, runId, taskId, fence,
                new ReviewResolveCommand.Settings { MergeReady = true, Reason = "Walked and directed by hand." },
                cts.Token);

            result.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.PrReviewDelivered.Should().BeTrue();
        run.State.Should().Be(RunState.UnderReview,
            "the run leaves its park the moment the verdict lands — PrReviewEngine's own resume finalizes it from here");
    }


    /// <summary>A pr-review task whose adversarial and conformance lenses both ran and parked their report.</summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedParkedPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-resolve-wt-{runId:N}");

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"resolve-{taskId:N}", "/tmp/resolve-repo", null, "main", ResolveThreadsNow);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        // Minted from this instance's own repository rather than written as a literal: the
        // one-live-task-per-item rule is keyed on the canonical reference and is project-blind
        // (see the repository field's own doc comment), so three callers sharing one literal
        // would put three simultaneously live pr-review tasks on one item key in this class's
        // shared database.
        string pullRequest = $"{repository}#7";

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, $"Review pull request {pullRequest}", ["every finding names a file and line"],
                TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, pullRequest), ResolveThreadsNow, node.OwnerId),
            node.OwnerId, ResolveThreadsNow);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, ResolveThreadsNow);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = ResolveThreadsNow });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/7",
                ExecutorMode.Subscription, ResolveThreadsNow),
            new AgentSessionCompleted(runId, ResolveThreadsNow),
            new ReviewParked(runId, "Findings ready.", ResolveThreadsNow));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }

    /// <summary>
    /// The park's own contract (task: a changes-requested pull-request review from a human becomes
    /// a fix lap): the drafted reply sits there unsent, and a verdict alone will not move the run
    /// on — the implementer has to say what the reviewer hears.
    /// </summary>
    [Fact]
    public async Task A_disagreement_park_posts_nothing_and_refuses_a_verdict_with_no_reply_choice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            RunAggregate parked = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
            parked.State.Should().Be(RunState.ReviewParked);
            parked.ParkedOnReviewDisagreement.Should().BeTrue();
            ReviewDisagreement drafted = parked.ParkedDisagreements.Should().ContainSingle().Subject;
            drafted.ProposedReply.Should().Be(ProposedReply);
            drafted.ThreadId.Should().Be("PRRT_abc");
        }

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId, new ReviewResolveCommand.Settings { MergeReady = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*that reply is yours to send*");
        gh.Calls.Should().BeEmpty("nothing reaches the reviewer until the implementer chooses it");
    }

    /// <summary>Choice one: the drafted reply, verbatim, inside the reviewer's own thread.</summary>
    [Fact]
    public async Task Post_reply_as_written_replies_in_the_reviewers_own_thread()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            gh.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("gh");
        call.Arguments.Should().Contain("graphql", "a thread reply is the GraphQL mutation, not a top-level comment");
        call.Arguments.Should().Contain("threadId=PRRT_abc");
        call.Arguments.Should().Contain($"body={ProposedReply}");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        ReviewDisagreementReplyDirection directed =
            run.ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.AsWritten);
        directed.PostedBody.Should().Be(ProposedReply);
        directed.PostedTarget.Should().Be("PRRT_abc");
        run.ParkedOnReviewDisagreement.Should().BeFalse("the park is resolved; the draft is no longer pending");
        run.State.Should().Be(RunState.UnderReview);
    }

    /// <summary>Choice two: the implementer's own words instead of the draft.</summary>
    [Fact]
    public async Task Post_reply_sends_the_implementers_own_text_instead_of_the_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReply = "Fair point; let me think on it." },
            gh, cts.Token);

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Contain(
            "body=Fair point; let me think on it.");

        await using IQuerySession query = store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.Edited);
        directed.PostedBody.Should().Be("Fair point; let me think on it.");
    }

    /// <summary>
    /// The whole point of task 412afe6c, end to end: a review-feedback follow-up whose session
    /// drafted its answer with em dashes, resolved as written, and the comment the reviewer
    /// actually reads carries none.
    /// <para>
    /// Origin incident (2026-09-09): follow-up run 01a085d9 of arx-platform task 01a083d8 answered
    /// a Copilot review on AgelessRx/arx-platform#2042 with a top-level comment under Brian's login
    /// carrying em dashes in most of its paragraphs. So the disagreement here is the one with no
    /// thread id, which is exactly that shape: a review BODY is unthreadable, so the answer is a
    /// top-level <c>gh pr comment</c>.
    /// </para>
    /// <para>
    /// The recorded direction is asserted too, not only the gh call. The run's own stream is what
    /// <c>h9k task show</c> reads back, and a record holding the draft while GitHub holds the
    /// rewrite would say the reviewer read words they never saw.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_em_dash_in_the_drafted_reply_never_reaches_the_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "the review's own body", "the reset is per window by contract",
                    "The limiter resets per window — that is the documented contract, and it is deliberate.",
                    Location: null, ThreadId: null, ReviewUrl: DisagreementReviewUrl),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        (string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory) call =
            gh.Calls.Should().ContainSingle().Subject;
        call.Arguments.Should().Contain("comment", "a review body has no thread to reply inside");
        string body = call.Arguments.Single(
            argument => argument.Contains("The limiter resets", StringComparison.Ordinal));
        body.Should().NotContain("—", "no em dash reaches a reviewer under the owner's login")
            .And.Contain("The limiter resets per window; that is the documented contract");

        await using IQuerySession query = store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.PostedBody.Should().NotBeNull();
        directed.PostedBody!.Should().NotContain("—", "the record says what the reviewer actually read");
    }

    /// <summary>Choice three: the reviewer hears nothing, and the record says so plainly.</summary>
    [Fact]
    public async Task Post_nothing_says_nothing_and_records_that_it_said_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId,
            new ReviewResolveCommand.Settings { NeedsFixes = "Do it their way after all.", PostNothing = true },
            gh, cts.Token);

        gh.Calls.Should().BeEmpty();

        await using IQuerySession query = store.QuerySession();
        ReviewDisagreementReplyDirection directed = (await query.LoadAsync<RunDetails>(runId, cts.Token))!
            .ChangesRequestedReplyDirections.Should().ContainSingle().Subject;
        directed.Choice.Should().Be(ReviewDisagreementReplyChoice.Nothing);
        directed.PostedBody.Should().BeNull();
        directed.PostedTarget.Should().BeNull();
    }

    /// <summary>
    /// A failed post leaves the park unresolved rather than recorded as answered: a stream saying
    /// the reviewer was told when they were not is the one thing this whole path exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_refused_post_leaves_the_park_unresolved_and_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.Failing("HTTP 404: Could not resolve to a node.");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Nothing was posted.*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue();
    }

    /// <summary>
    /// The post seam one layer down from the arms above: gh exited 0 — so GitHub accepted the
    /// mutation and the reply is live under the operator's login — but a spawned helper held its
    /// output pipe past <c>ExternalProcess.DrainGrace</c>, so <c>ExternalProcess</c> threw instead
    /// of returning. The refusal used to read "Nothing was posted. … Finish by hand if the reviewer
    /// is owed a reply", which contradicts an observed exit code and steers the operator into
    /// posting the identical reply a second time — the double-post this whole ordering exists to
    /// prevent (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_post_whose_output_stuck_after_gh_exited_zero_says_the_reply_did_reach_the_reviewer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.ExitedButOutputStuck(0);
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*DID reach the reviewer at PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*")
            .Which.Message.Should().NotContain(
                "Nothing was posted",
                "gh's own exit code says otherwise, and this path's whole job is not to guess at "
                + "unobserved facts");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue("the park is still the implementer's to resolve");
    }

    /// <summary>
    /// The same seam with gh's exit code saying the opposite: it exited non-zero, so the write was
    /// refused and nothing reached the reviewer, however stuck its output was afterwards. The
    /// certainty read is the exit code, not the exception type — <c>ProcessOutputStuckException</c>
    /// carries both answers.
    /// </summary>
    [Fact]
    public async Task A_post_whose_output_stuck_after_gh_failed_still_says_nothing_was_posted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.ExitedButOutputStuck(1);
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Nothing was posted.*")
            .WithMessage("*Finish by hand if the reviewer is owed a reply*");
    }

    /// <summary>
    /// The deadline expiring mid-post: gh never answered, so whether GitHub accepted the reply was
    /// never observed. Neither "posted" nor "nothing was posted" is a fact here, and the honest
    /// label is the one the operator can act on — go and read the pull request (AGENTS.md: never
    /// guess at unobserved facts).
    /// </summary>
    [Fact]
    public async Task A_post_that_timed_out_mid_flight_says_the_reply_may_have_reached_the_reviewer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        RecordingProcessRunner gh = RecordingProcessRunner.NeverAnswering();
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*MAY have reached the reviewer at PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*")
            .Which.Message.Should().NotContain("Nothing was posted");
    }

    /// <summary>
    /// Ctrl+C while gh is mid-call, with nothing posted yet: <c>ExternalProcess</c> kills the tree,
    /// but the mutation may already have committed on GitHub's side. This used to propagate as a
    /// bare cancellation — no mention that a post was in flight — so the operator's natural re-run
    /// with the same reply choice could post the identical reply a second time. Distinct from the
    /// cancellation test further down, which cancels AFTER a confirmed post and fails in the
    /// SaveChangesAsync window (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_cancellation_while_the_post_was_in_flight_says_the_reply_may_have_reached_the_reviewer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        // Cancelled and thrown from inside the fake gh, which is how the real seam reports it: the
        // caller's token is what ExternalProcess's own catch checks first, and a cancelled one
        // there rethrows the cancellation rather than naming a deadline.
        RecordingProcessRunner gh = new(() =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>(
            "a bare cancellation tells the operator nothing about the post that was in flight"))
            .WithMessage("*MAY have reached the reviewer at PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(
            runId, token: CancellationToken.None))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue("the park is still the implementer's to resolve");
    }

    /// <summary>
    /// The one window left between the post and the commit: the reply reaches the reviewer and the
    /// run stream moves under the resolve before its fenced append can land. Nothing is recorded —
    /// but the refusal has to SAY the reply was posted, because the operator's next move is the
    /// re-run the message itself invites, and a second identical reply under their own login is
    /// not something they can undo (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public async Task A_posted_reply_the_resolve_could_not_record_is_named_rather_than_left_to_a_second_post()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        // Appended from inside the fake gh, which is the only moment that is genuinely after the
        // post and before the append. h9k task log-interaction is a real concurrent writer to this
        // very stream, so this is the race as it actually happens rather than a contrived one.
        RecordingProcessRunner gh = new(_ =>
        {
            DocumentStore racing = postgres.Store;
            using IDocumentSession other = racing.LightweightSession();
            other.Events.Append(runId, new ExternalInteractionLogged(
                runId, ResolveThreadsNow, "another session", "logged while the resolve was posting", false, null, node.OwnerId));
            other.SaveChangesAsync(cts.Token).GetAwaiter().GetResult();
            return new ProcessResult(0, "{}", string.Empty);
        });

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Already posted, and NOT recorded on the run: PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue("the park is still the implementer's to resolve");
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.ChangesRequestedReplyDirections.Should().BeEmpty(
            "the append was one transaction, so the reply the reviewer read is unrecorded — which is "
            + "exactly what the message has to admit");
    }

    /// <summary>
    /// The same window, entered the other way: Ctrl+C after the reply posted and before the commit.
    /// A cancellation used to be excluded from the arm above, so the operator got a bare
    /// cancellation, no mention of the reply the reviewer had already read, and a still-parked run
    /// that refuses a verdict without a reply choice — which makes the natural retry post the
    /// identical reply a second time under their own login (independent pre-PR review, cycle 1,
    /// adversarial finding). With nothing posted a cancellation still propagates untouched, which
    /// is what the ordinary-park path relies on.
    /// </summary>
    [Fact]
    public async Task A_cancellation_after_the_reply_posted_still_names_it_rather_than_inviting_a_second_post()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(store, node, cts.Token);

        // Cancelled from inside the fake gh, which is the one moment that is genuinely after the
        // reply reached the reviewer and before the append could land — the same seam the racing
        // writer above uses, carrying the other failure this window can produce.
        RecordingProcessRunner gh = new(_ =>
        {
            cts.Cancel();
            return new ProcessResult(0, "{}", string.Empty);
        });

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>(
            "a cancellation the operator cannot see the reply behind is the double-post this arm prevents"))
            .WithMessage("*Already posted, and NOT recorded on the run: PRRT_abc*")
            .WithMessage("*Do NOT re-run with a reply choice*");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(
            runId, token: CancellationToken.None))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue("the park is still the implementer's to resolve");
    }

    /// <summary>
    /// The reply choices are refused on every other park: they name a reviewer's thread, and on an
    /// ordinary park there is no drafted reply and no disputed finding to point at.
    /// </summary>
    [Fact]
    public async Task A_reply_choice_is_refused_on_a_park_that_is_not_a_disagreement()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(store, node, cts.Token, disagreed: false);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostNothing = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*not a changes-requested disagreement*");
    }

    /// <summary>The three choices are three answers to one question, so passing two is refused up front.</summary>
    [Fact]
    public void Two_reply_choices_at_once_are_refused_by_validation() =>
        new ReviewResolveCommand.Settings { MergeReady = true, PostNothing = true, PostReplyAsWritten = true }
            .Validate().Successful.Should().BeFalse();

    /// <summary>
    /// One event per reply, not one per resolve (self-review, this task): two disagreements post
    /// two different drafts to two different places, and a single record carrying one body beside
    /// both targets would state that both reviewers read the same words.
    /// </summary>
    [Fact]
    public async Task Two_parked_disagreements_record_one_direction_each_with_its_own_body_and_target()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement("first point", "first reasoning", "First reply.", "src/A.cs:1", "PRRT_a"),
                // No thread: the review's own body, which answers as a top-level comment instead.
                new ReviewDisagreement(
                    "the body's point", "second reasoning", "Second reply.",
                    Location: null, ThreadId: null, ReviewUrl: DisagreementReviewUrl),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        gh.Calls.Should().HaveCount(2);
        gh.Calls[0].Arguments.Should().Contain("graphql").And.Contain("threadId=PRRT_a");
        gh.Calls[1].Arguments.Should().Contain("comment", "a review body has no thread to reply inside");

        await using IQuerySession query = store.QuerySession();
        List<ReviewDisagreementReplyDirection> directions =
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ChangesRequestedReplyDirections;

        directions.Should().HaveCount(2);
        directions[0].PostedBody.Should().Be("First reply.");
        directions[0].PostedTarget.Should().Be("PRRT_a");
        directions[1].PostedBody.Should().Contain("Second reply.").And.Contain(
            DisagreementReviewUrl, "a top-level comment names the review it answers");
        directions[1].PostedTarget.Should().Be(DisagreementPullRequestUrl);
    }

    /// <summary>
    /// Every refusal is raised before the first provider write (self-review, this task): a park
    /// where only one disagreement carries a draft posts NEITHER, rather than posting the first
    /// and then failing the resolve — a reply a reviewer has read with nothing on the stream
    /// saying so is not something an operator can undo.
    /// </summary>
    [Fact]
    public async Task A_park_where_one_disagreement_has_no_draft_posts_neither()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement("first point", "first reasoning", "First reply.", "src/A.cs:1", "PRRT_a"),
                new ReviewDisagreement("second point", "second reasoning", ProposedReply: "", "src/B.cs:2", "PRRT_b"),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*Nothing has been posted*");
        gh.Calls.Should().BeEmpty();

        await using IQuerySession query = store.QuerySession();
        (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!
            .State.Should().Be(RunState.ReviewParked);
    }

    /// <summary>
    /// A single --post-reply text has no one place to go across two disagreements, so it is
    /// refused rather than sent twice to two different reviewers.
    /// </summary>
    [Fact]
    public async Task An_edited_reply_is_refused_when_the_park_holds_more_than_one_disagreement()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement("first", "reasoning", "First reply.", "src/A.cs:1", "PRRT_a"),
                new ReviewDisagreement("second", "reasoning", "Second reply.", "src/B.cs:2", "PRRT_b"),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReply = "My own words." },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*has no one place to go*");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A park whose marker landed with no parseable block still asks the implementer the question,
    /// but has no draft to send — so as-written is refused with the one lever that still applies.
    /// </summary>
    [Fact]
    public async Task An_unparseable_park_refuses_as_written_and_points_at_post_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token, disagreements: []);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!
                .ParkedOnReviewDisagreement.Should().BeTrue(
                    "the marker said a disagreement exists, so the implementer is still owed the question");
        }

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*no readable disagreement*");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The park's thread id is the fix session's own claim, parsed out of its free-text summary,
    /// and it is what selects where the implementer's reply lands under their own login. A session
    /// that copied the adjacent finding's tag — or invented a syntactically valid node id — would
    /// otherwise have the reply accepted into a thread nobody disputed, possibly on another pull
    /// request, with the disputed thread left unanswered and the run recording it as answered
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_thread_the_reviewer_never_opened_is_refused_rather_than_posted_to()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "reset the limiter on every request", "per-window is the documented contract",
                    ProposedReply, "src/Limiter.cs:42", "PRRT_somebody_elses", DisagreementReviewUrl),
            ],
            // What closeout actually read: one thread, and not the one the park names.
            reviewFindings: [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*PRRT_somebody_elses*")
            .WithMessage("*not one of the threads the reviewer opened*")
            .WithMessage("*PRRT_abc*", "the threads the review did leave are named, so the reply can be sent by hand")
            .WithMessage("*--post-nothing*");
        gh.Calls.Should().BeEmpty("the check is a refusal before the first write, not a report after it");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.ReviewParked, "the verdict never landed");
        run.ParkedOnReviewDisagreement.Should().BeTrue();
    }

    /// <summary>
    /// The same skepticism on the other branch: a disputed review BODY answers as a top-level
    /// comment whose text names the review it answers, so a review url closeout never read would
    /// tell a real reviewer they are being answered about a review that may not be theirs.
    /// </summary>
    [Fact]
    public async Task A_review_this_lap_was_not_dispatched_to_answer_is_refused_on_the_comment_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "the body's point", "my reasoning", "Second reply.",
                    Location: null, ThreadId: null,
                    ReviewUrl: "https://github.com/x/y/pull/9#pullrequestreview-99"),
            ]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => ReviewResolveCommand.ResolveAsync(
            session, taskId,
            new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            new GitHubReviewReplies(gh.Runner), cts.Token);

        (await act.Should().ThrowAsync<DomainConflictException>())
            .WithMessage("*pullrequestreview-99*")
            .WithMessage("*not one of the changes-requested reviews this lap was dispatched to answer*")
            .WithMessage($"*{DisagreementReviewUrl}*", "the review this lap IS answering is named");
        gh.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A review url is only what a top-level comment's own text names, so a thread reply — which
    /// posts by thread id and never reads the url — is not refused over one closeout did not read.
    /// Refusing there would refuse a post over a field the post does not use.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_review_url_does_not_refuse_a_thread_reply()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedParkedDisagreementAsync(
            store, node, cts.Token,
            disagreements:
            [
                new ReviewDisagreement(
                    "reset the limiter on every request", "per-window is the documented contract",
                    ProposedReply, "src/Limiter.cs:42", "PRRT_abc",
                    ReviewUrl: "https://github.com/x/y/pull/9#pullrequestreview-99"),
            ],
            reviewFindings: [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]);

        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding("{}");
        await ResolveWithDoorbellAsync(
            store, taskId, new ReviewResolveCommand.Settings { MergeReady = true, PostReplyAsWritten = true },
            gh, cts.Token);

        gh.Calls.Should().ContainSingle().Which.Arguments.Should().Contain("threadId=PRRT_abc");
    }

    /// <summary>
    /// Deliberately already clean against the platform's default writing conventions (task
    /// 412afe6c): every arm below asserts the posted body verbatim, and a draft carrying an em dash
    /// would have those arms testing the rewrite rather than the thing they were written for. The
    /// rewrite has its own test, <see cref="An_em_dash_in_the_drafted_reply_never_reaches_the_pull_request"/>.
    /// </summary>
    private const string ProposedReply =
        "Good catch on the naming; the reset really is per window, deliberately: PLAN.md 12.3 sets the contract.";

    private const string DisagreementPullRequestUrl = "https://github.com/x/y/pull/7";

    /// <summary>The one disagreement a lap's own prompt asks it to park: an inline finding with a drafted reply.</summary>
    private static readonly IReadOnlyList<ReviewDisagreement> DefaultDisagreements =
    [
        new ReviewDisagreement(
            "reset the limiter on every request", "per-window is the documented contract",
            ProposedReply, "src/Limiter.cs:42", "PRRT_abc", DisagreementReviewUrl),
    ];

    private const string DisagreementReviewUrl = $"{DisagreementPullRequestUrl}#pullrequestreview-42";

    /// <summary>
    /// Runs the resolve with the doorbell pointed at this fixture, the same way the pr-review
    /// merge-ready test does: <c>Hall9k.Cli.Infrastructure.Doorbell</c> resolves its connection off
    /// the environment rather than off the store handed in here.
    /// </summary>
    private async Task ResolveWithDoorbellAsync(
        DocumentStore store, Guid taskId, ReviewResolveCommand.Settings settings,
        RecordingProcessRunner gh, CancellationToken cancellationToken)
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            int result = await ReviewResolveCommand.ResolveAsync(
                session, taskId,
                settings,
                new GitHubReviewReplies(gh.Runner), cancellationToken);
            result.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    /// <summary>
    /// A changes-requested fix lap that disagreed with one finding, posted nothing, and parked —
    /// or, with <paramref name="disagreed"/> false, the same lap parked the ordinary way, which is
    /// what the reply choices must be refused on.
    /// </summary>
    /// <param name="reviewFindings">
    /// What closeout read off the review, which is the truth the resolve checks a parked
    /// disagreement's own thread id against. Defaults to a finding per parked disagreement, so an
    /// ordinary park points at threads the reviewer really opened; a test about the mismatch itself
    /// names its own.
    /// </param>
    private async Task<(Guid TaskId, Guid RunId)> SeedParkedDisagreementAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken, bool disagreed = true,
        IReadOnlyList<ReviewDisagreement>? disagreements = null,
        IReadOnlyList<ChangesRequestedFinding>? reviewFindings = null)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-disagree-wt-{runId:N}");

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"disagree-{taskId:N}", "/tmp/disagree-repo", null, "main", ResolveThreadsNow);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Bound the limiter", ["the limiter resets per window"],
                TaskType.Feature, null, null, null, ResolveThreadsNow, node.OwnerId),
            node.OwnerId, ResolveThreadsNow);

        // The real lifecycle, walked rather than shortcut: claimed, delivered with a pull request,
        // then reopened for the changes-requested lap this run answers — the reopen is what puts
        // the follow-up kind and the review itself on the task.
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), ResolveThreadsNow);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, claimed.RunId, DisagreementPullRequestUrl, ResolveThreadsNow);
        task.Apply(completed);
        IReadOnlyList<ReviewDisagreement> parked = disagreements ?? DefaultDisagreements;
        IReadOnlyList<ChangesRequestedFinding> findings = reviewFindings
            ?? (parked.Count > 0
                ? [.. parked.Select(disagreement => new ChangesRequestedFinding(
                    disagreement.Finding, disagreement.Location, disagreement.ThreadId))]
                : [new ChangesRequestedFinding("This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]);
        TaskReopened reopened = TaskDecider.Reopen(
            task, claimed.RunId, "task/limiter", "@teammate requested changes.",
            FollowUpKind.ReviewRequestedChanges, automatic: true, ResolveThreadsNow, node.OwnerId,
            changesRequestedReviews:
            [
                new ChangesRequestedReview("teammate", DisagreementReviewUrl, ResolveThreadsNow, findings),
            ]);
        task.Apply(reopened);
        TaskClaimed followUpClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, ResolveThreadsNow);
        session.Events.StartStream<TaskAggregate>(
            taskId, [.. lifecycle, claimed, completed, reopened, followUpClaim]);
        session.Store(new TaskLease
        {
            Id = taskId, NodeId = node.NodeId, LeaseGeneration = followUpClaim.LeaseGeneration, HeartbeatAt = ResolveThreadsNow,
        });

        List<object> runEvents =
        [
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, followUpClaim.LeaseGeneration, sessionId, worktreePath,
                "task/limiter", ExecutorMode.Subscription, ResolveThreadsNow, IsFollowUp: true),
            new AgentSessionCompleted(runId, ResolveThreadsNow),
        ];
        if (disagreed)
        {
            runEvents.Add(new ReviewDisagreementParked(runId, parked, ResolveThreadsNow));
        }

        runEvents.Add(new ReviewParked(runId, "A changes-requested fix lap disagreed.", ResolveThreadsNow));
        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }
}
