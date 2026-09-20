using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.RunSkills;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The whole run-skill seam, end to end against real Marten/Postgres but with a fake repository
/// on disk, a <see cref="FakeLedger"/> stand-in for the ledger, and a scripted executor standing
/// in for the discovery session (idea b9b09779, piece 4; Brian's 2026-09-13 testing rule).
/// <para>
/// What each test is actually pinning, since the composing half is an agent nobody can assert
/// against: that the MECHANICAL halves do their job and the seam between them holds. The survey
/// finds the right files and the prompt carries them; the session's own answer is parsed,
/// validated against the shared shape, and recorded with the commit the daemon read rather than
/// one the session claimed; a repository with nothing to read never spawns a session at all; and
/// the ledger write happens in the daemon, never in the CLI process that records the event.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class RunSkillSweepEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string ProjectName = "hall9k";

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public RunSkillSweepEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_README_launch_section_reaches_the_prompt_and_a_full_text_skill_citing_it_lands_on_the_ledger()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(
            worktree, "README.md",
            "# hall9k\n\n## Running it locally\n\ndotnet run --project src/Hall9k.AppHost\n", cts.Token);
        await WriteRepositoryFileAsync(worktree, "docker-compose.yml", "services:\n  db:\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer(
            "full-text",
            Document("Run `dotnet run --project src/Hall9k.AppHost` from the repository root (README.md).")));
        FakeLedger ledger = new();
        RunSkillSweepEngine engine = Engine(node, ledger, executor);

        RunSkillSweepResult sweep = await engine.SweepOnceAsync(cts.Token);

        sweep.Discovered.Should().Be(1);
        sweep.Pushed.Should().Be(1, "the discovery and the ledger push both happen inside one sweep tick");

        // The mechanical half: the survey found the README's launch heading and the compose file,
        // and the prompt handed both to the session rather than asking it to go looking.
        executor.Spawns.Should().ContainSingle();
        string prompt = executor.Spawns[0].Prompt;
        prompt.Should().Contain("`README.md`");
        prompt.Should().Contain("a root briefing whose own headings mention launching");
        prompt.Should().Contain("`docker-compose.yml`");
        executor.Spawns[0].SessionArtifactName.Should().StartWith(SessionRoleName.RunSkillDiscovery);

        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill.Should().NotBeNull();
        project.RunSkill!.Shape.Should().Be(RunSkillShape.FullText);
        project.RunSkill.Author.Should().Be(RunSkillAuthor.DiscoverySession);
        project.RunSkill.Content.Should().StartWith(RunSkillDocument.FirstLine(RunSkillShape.FullText));
        project.RunSkill.Content.Should().Contain("(README.md)");
        project.RunSkill.ComposedAgainstCommit.Should().Be(
            HeadSha, "the commit is what the daemon read with git rev-parse, never what the session claimed");
        project.RunSkillDiscoveryOutstanding.Should().BeFalse();

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.RunSkill.RefspecSource, LedgerRefRegistry.RunSkillPath, cts.Token);
        stored.Content.Should().Be(project.RunSkill.Content);
    }

    [Fact]
    public async Task A_skills_directory_reaches_the_prompt_and_a_pointer_skill_lands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(
            worktree, Path.Combine(".claude", "skills", "run", "SKILL.md"),
            "---\nname: run\n---\n\nLaunch it with `docker compose up -d` then `dotnet run`.\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer(
            "pointer",
            Document("Follow `.claude/skills/run/SKILL.md`; it does not say which .NET SDK to have.")));
        FakeLedger ledger = new();

        RunSkillSweepResult sweep = await Engine(node, ledger, executor).SweepOnceAsync(cts.Token);

        sweep.Discovered.Should().Be(1);
        executor.Spawns.Should().ContainSingle();
        executor.Spawns[0].Prompt.Should().Contain("`.claude/skills/run/SKILL.md`");
        executor.Spawns[0].Prompt.Should().Contain("a repository skill (run) that may already cover launching");

        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill!.Shape.Should().Be(RunSkillShape.Pointer);
        project.RunSkill.Content.Should().StartWith(RunSkillDocument.FirstLine(RunSkillShape.Pointer));

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.RunSkill.RefspecSource, LedgerRefRegistry.RunSkillPath, cts.Token);
        stored.Content.Should().Be(project.RunSkill.Content);
    }

    [Fact]
    public async Task A_repository_with_nothing_to_read_produces_the_none_discoverable_skill_and_never_spawns_a_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(worktree, Path.Combine("src", "thing.txt"), "nothing useful\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new("this session must never be dispatched");
        FakeLedger ledger = new();

        RunSkillSweepResult sweep = await Engine(node, ledger, executor).SweepOnceAsync(cts.Token);

        sweep.Discovered.Should().Be(1);
        executor.Spawns.Should().BeEmpty("tools before tokens: the survey already answered this one");

        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill!.Shape.Should().Be(RunSkillShape.NoneDiscoverable);
        project.RunSkill.Author.Should().Be(
            RunSkillAuthor.Platform, "no session composed it, so none is credited with it");
        project.RunSkill.Content.Should().Contain("Nothing in this repository says");
        project.RunSkill.Content.Should().Contain("briefings at the repository root");

        ProjectShowCommand.RunSkillRow(project).Should().Contain("none discoverable");

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.RunSkill.RefspecSource, LedgerRefRegistry.RunSkillPath, cts.Token);
        stored.Content.Should().Be(project.RunSkill.Content);
    }

    [Fact]
    public async Task Set_replaces_through_the_same_event_and_the_ledger_write_happens_in_the_daemon_not_the_command()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(worktree, "README.md", "# hall9k\n\n## Launch\n\nmake dev\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer("full-text", Document("Run `make dev` (README.md).")));
        FakeLedger ledger = new();
        RunSkillSweepEngine engine = Engine(node, ledger, executor);
        await engine.SweepOnceAsync(cts.Token);
        ledger.Writes.Should().HaveCount(1);

        await SetByHandAsync("pointer", Document("Follow `README.md`; it is complete."), cts.Token);

        // The command itself never touched the ledger — the write count is exactly where the
        // daemon's own last sweep left it, and the event is already recorded.
        ledger.Writes.Should().HaveCount(1, "h9k project run-skill set records an event and nothing else");
        ProjectDetails afterSet = await LoadProjectAsync(projectId, cts.Token);
        afterSet.RunSkill!.Shape.Should().Be(RunSkillShape.Pointer);
        afterSet.RunSkill.Author.Should().Be(RunSkillAuthor.Hand);
        afterSet.RunSkill.Content.Should().Contain("it is complete");

        RunSkillSweepResult second = await engine.SweepOnceAsync(cts.Token);

        second.Discovered.Should().Be(0, "a hand-set skill settles the request rather than earning a new session");
        second.Pushed.Should().Be(1);
        ledger.Writes.Should().HaveCount(2);
        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.RunSkill.RefspecSource, LedgerRefRegistry.RunSkillPath, cts.Token);
        stored.Content.Should().Be(afterSet.RunSkill.Content);

        (await engine.SweepOnceAsync(cts.Token)).Pushed.Should().Be(0, "nothing new to push");
    }

    [Fact]
    public async Task A_session_whose_document_misses_a_shared_section_is_a_failed_discovery_rather_than_a_recorded_skill()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(worktree, "README.md", "# hall9k\n\n## Running\n\nmake dev\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer("full-text", "## Launch\n\nmake dev (README.md)\n"));
        FakeLedger ledger = new();

        RunSkillSweepResult sweep = await Engine(node, ledger, executor).SweepOnceAsync(cts.Token);

        sweep.Discovered.Should().Be(1);
        sweep.Pushed.Should().Be(0);
        ledger.Writes.Should().BeEmpty("a half-composed document never reaches anybody else's machine");

        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill.Should().BeNull();
        project.RunSkillDiscoveryFailure.Should().Contain("Prerequisites");
        project.RunSkillDiscoveryOutstanding.Should().BeFalse("a failure ends the request rather than looping");
        ProjectShowCommand.RunSkillRow(project).Should().Contain("discovery failed");
    }

    [Fact]
    public async Task A_project_with_no_materialised_worktree_fails_the_discovery_by_name_rather_than_retrying_forever()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        Directory.Delete(worktree, recursive: true);
        // Asked for long enough ago that RunSkillCheckoutGrace has plainly run out: this is the
        // home that is broken, not the home that is still being made.
        await RequestDiscoveryAtAsync(projectId, DateTimeOffset.UtcNow.AddDays(-1), cts.Token);

        ScriptedDiscoveryExecutor executor = new("this session must never be dispatched");
        RunSkillSweepResult sweep = await Engine(node, new FakeLedger(), executor).SweepOnceAsync(cts.Token);

        sweep.Discovered.Should().Be(1);
        executor.Spawns.Should().BeEmpty();
        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkillDiscoveryFailure.Should().Contain("h9k project init");
        project.RunSkillDiscoveryOutstanding.Should().BeFalse();
    }

    [Fact]
    public async Task A_checkout_that_has_not_appeared_yet_leaves_the_request_outstanding_rather_than_consuming_it()
    {
        // h9k project add asks for the discovery and then clones, so a tick can land while the
        // home is still being made. Consuming the request there told the operator to repair a
        // home that finished fine a minute later, and nothing ever asked again (independent
        // pre-PR review, cycle 1, both lenses). A fresh request over an absent checkout waits.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        Directory.Delete(worktree, recursive: true);
        await RequestDiscoveryAtAsync(projectId, DateTimeOffset.UtcNow, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer("full-text", Document("make dev (README.md)")));
        RunSkillSweepEngine engine = Engine(node, new FakeLedger(), executor);

        (await engine.SweepOnceAsync(cts.Token)).Discovered.Should().Be(0, "nothing was decided this tick");
        executor.Spawns.Should().BeEmpty();
        ProjectDetails waiting = await LoadProjectAsync(projectId, cts.Token);
        waiting.RunSkillDiscoveryFailure.Should().BeNull("a clone in flight is not a broken home");
        waiting.RunSkillDiscoveryOutstanding.Should().BeTrue("the request survives to be answered later");

        // And once the clone lands, the same standing request is answered without anybody asking
        // again — which is the whole point of not having consumed it.
        await WriteRepositoryFileAsync(worktree, "README.md", "# a\n\n## Launch\n\nmake dev\n", cts.Token);

        (await engine.SweepOnceAsync(cts.Token)).Discovered.Should().Be(1);
        ProjectDetails answered = await LoadProjectAsync(projectId, cts.Token);
        answered.RunSkill!.Shape.Should().Be(RunSkillShape.FullText);
        answered.RunSkillDiscoveryFailure.Should().BeNull();
    }

    [Fact]
    public async Task A_second_discovery_writes_its_own_stream_file_rather_than_reading_the_first_ones()
    {
        // --discover-run-skill is re-runnable by design. One fixed stream path per project had
        // the second session's waiter read the FIRST session's result line, before the child
        // shell had truncated the file, and then kill a live session and record a failure that
        // never happened (independent pre-PR review, cycle 1, adversarial lens).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(worktree, "README.md", "# a\n\n## Launch\n\nmake dev\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(
            Trailer("full-text", Document("the first answer (README.md)")),
            Trailer("full-text", Document("the second answer (README.md)")));
        RunSkillSweepEngine engine = Engine(node, new FakeLedger(), executor);

        await engine.SweepOnceAsync(cts.Token);
        // Later than the dispatch the first sweep just recorded against the real clock, which is
        // what makes the second ask outstanding rather than already answered.
        await RequestDiscoveryAtAsync(projectId, DateTimeOffset.UtcNow.AddMinutes(1), cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        executor.Spawns.Should().HaveCount(2);
        executor.Spawns[0].SessionArtifactName.Should().NotBe(
            executor.Spawns[1].SessionArtifactName, "no discovery ever tails a path an earlier one wrote");
        executor.Spawns.Select(spawn => RunPaths.SessionStreamFile(spawn.RunDirectory, spawn.SessionArtifactName!))
            .Should().OnlyHaveUniqueItems();

        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill!.Content.Should().Contain("the second answer");
        project.RunSkillDiscoveryFailure.Should().BeNull();
    }

    [Fact]
    public async Task A_project_whose_home_has_no_dev_worktree_is_read_from_its_recorded_clone_instead()
    {
        // h9k project add --repo <path> deliberately leaves the home's own repo/ unmaterialised
        // and records the operator's existing clone as the repository path. That project is
        // perfectly readable, so its discovery resolves through ProjectCheckout.ForReading like
        // every other reading session rather than refusing outright over an absent repo/dev.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);

        Guid projectId = DomainId.New();
        string projectHome = Path.Combine(_scopedHome.Home, "projects", "elsewhere");
        string clone = Path.Combine(_scopedHome.Home, "somebody-elses-clone");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        await WriteRepositoryFileAsync(clone, "README.md", "# elsewhere\n\n## Launch\n\nmake dev\n", cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), "elsewhere", clone, null, null, Now,
                    homeDirectory: ProjectHome.Parse(projectHome)));
            await session.SaveChangesAsync(cts.Token);
        }

        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer("full-text", Document("make dev (README.md)")));
        await Engine(node, new FakeLedger(), executor).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().ContainSingle();
        executor.Spawns[0].WorktreePath.Should().Be(clone, "the clone is the checkout with files in it");
        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill.Should().NotBeNull();
        project.RunSkillDiscoveryFailure.Should().BeNull();
    }

    [Fact]
    public async Task A_homed_project_whose_only_checkout_is_the_bare_clone_is_refused_before_a_session_is_spawned()
    {
        // What ProjectCheckout.ForReading falls back to for a homed project with no repo/dev is
        // the recorded repository path, and for a home-materialised project that path IS the
        // home's own bare clone: refs and objects, not one file to read. Caught here rather than
        // by a session reporting it found nothing (ProjectCheckout.IsBare's own contract).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cts.Token);

        Guid projectId = DomainId.New();
        string projectHome = Path.Combine(_scopedHome.Home, "projects", "bare-only");
        string bare = ProjectHomePaths.BareRepository(projectHome, "bare-only");
        Directory.CreateDirectory(Path.Combine(bare, "objects"));
        Directory.CreateDirectory(Path.Combine(bare, "refs"));

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), "bare-only", bare, null, null, Now,
                    homeDirectory: ProjectHome.Parse(projectHome)));
            await session.SaveChangesAsync(cts.Token);
        }

        await RequestDiscoveryAtAsync(projectId, DateTimeOffset.UtcNow.AddDays(-1), cts.Token);

        ScriptedDiscoveryExecutor executor = new("this session must never be dispatched");
        await Engine(node, new FakeLedger(), executor).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().BeEmpty();
        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill.Should().BeNull();
        project.RunSkillDiscoveryFailure.Should().Contain("no checkout with files in it");
        project.RunSkillDiscoveryOutstanding.Should().BeFalse();
    }

    [Fact]
    public async Task One_tick_answers_at_most_one_discovery_so_a_tick_is_bounded_by_a_single_session()
    {
        // A discovery is a synchronous spawn-and-wait under RunSkillDiscoveryTimeout, and every
        // other project's ledger push sits behind it on the same tick. Several projects asking at
        // once — a fresh install is exactly that — must not multiply the tick's own ceiling.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid firstId, string firstWorktree) = await SeedAsync(cts.Token);
        (Guid secondId, string secondWorktree) = await SeedSecondProjectAsync(node, cts.Token);
        await WriteRepositoryFileAsync(firstWorktree, "README.md", "# a\n\n## Launch\n\nmake dev\n", cts.Token);
        await WriteRepositoryFileAsync(secondWorktree, "README.md", "# b\n\n## Launch\n\nmake dev\n", cts.Token);
        await RequestDiscoveryAsync(firstId, cts.Token);
        await RequestDiscoveryAsync(secondId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(
            Trailer("full-text", Document("make dev (README.md)")),
            Trailer("full-text", Document("make dev (README.md)")));
        RunSkillSweepEngine engine = Engine(node, new FakeLedger(), executor);

        (await engine.SweepOnceAsync(cts.Token)).Discovered.Should().Be(1);
        executor.Spawns.Should().ContainSingle();

        (await engine.SweepOnceAsync(cts.Token)).Discovered.Should().Be(1, "the second project's turn comes next tick");
        executor.Spawns.Should().HaveCount(2);

        (await LoadProjectAsync(firstId, cts.Token)).RunSkill.Should().NotBeNull();
        (await LoadProjectAsync(secondId, cts.Token)).RunSkill.Should().NotBeNull();
    }

    [Fact]
    public async Task A_composed_document_past_the_cap_is_refused_by_its_own_rule_rather_than_by_a_generic_failure()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (NodeContext node, Guid projectId, string worktree) = await SeedAsync(cts.Token);
        await WriteRepositoryFileAsync(worktree, "README.md", "# a\n\n## Launch\n\nmake dev\n", cts.Token);
        await RequestDiscoveryAsync(projectId, cts.Token);

        ScriptedDiscoveryExecutor executor = new(Trailer(
            "full-text", Document(new string('x', ProjectDecider.RunSkillMaximumLength))));
        FakeLedger ledger = new();

        await Engine(node, ledger, executor).SweepOnceAsync(cts.Token);

        ProjectDetails project = await LoadProjectAsync(projectId, cts.Token);
        project.RunSkill.Should().BeNull();
        project.RunSkillDiscoveryFailure.Should().Contain("the composed run skill was refused");
        project.RunSkillDiscoveryFailure.Should().Contain($"{ProjectDecider.RunSkillMaximumLength}-character cap");
        project.RunSkillDiscoveryFailure.Should().NotContain(
            "could not be run", "the session did run; it wrote too much, and the reason must say so");
        ledger.Writes.Should().BeEmpty();
    }

    private RunSkillSweepEngine Engine(NodeContext node, FakeLedger ledger, ScriptedDiscoveryExecutor executor) =>
        new(
            _postgres.Store, node, ledger, new NodeKeyStore(), executor, executor.Processes,
            FakeGit, Options.Create(new DaemonOptions()), NullLogger<RunSkillSweepEngine>.Instance);

    /// <summary>
    /// The git seam, answering only what this engine actually asks it: <c>rev-parse HEAD</c>. A
    /// fixed sha so a test can assert the recorded commit came from the daemon's own read rather
    /// than from anything the scripted session said.
    /// </summary>
    private const string HeadSha = "1111111111111111111111111111111111111111";

    private static Task<ProcessResult> FakeGit(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        fileName.Should().Be("git");
        arguments.Should().Equal("rev-parse", "HEAD");
        return Task.FromResult(new ProcessResult(0, HeadSha + "\n", string.Empty));
    }

    /// <summary>A document carrying all six shared sections, with <paramref name="launch"/> under Launch.</summary>
    private static string Document(string launch) =>
        "## Prerequisites\n\nThe .NET 10 SDK.\n\n"
        + "## One-time setup\n\nNone.\n\n"
        + $"## Launch\n\n{launch}\n\n"
        + "## How to know it is up\n\nThe dashboard URL prints on stdout.\n\n"
        + "## Address or entry point\n\nhttp://localhost:5000\n\n"
        + "## Human steps\n\nNone.\n";

    private static string Trailer(string shape, string document) =>
        $"I read the repository and composed this.\n\n{AgentPromptBuilder.RunSkillShapeMarker} {shape}\n"
        + $"{AgentPromptBuilder.RunSkillMarkdownMarker}\n{document}";

    private static async Task WriteRepositoryFileAsync(
        string worktree, string relativePath, string content, CancellationToken cancellationToken)
    {
        string file = Path.Combine(worktree, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content, cancellationToken);
    }

    private Task RequestDiscoveryAsync(Guid projectId, CancellationToken cancellationToken) =>
        RequestDiscoveryAtAsync(projectId, Now, cancellationToken);

    /// <summary>
    /// A discovery asked for at a stated moment. The engine's checkout grace is measured against
    /// this timestamp in real time, so a test about a checkout that is never coming says so by
    /// asking long ago, and one about a home still being made asks just now.
    /// </summary>
    private async Task RequestDiscoveryAtAsync(
        Guid projectId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
        session.Events.Append(
            projectId, ProjectDecider.RequestRunSkillDiscovery(projectId, project.OwnerId, requestedAt));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task SetByHandAsync(string shape, string body, CancellationToken cancellationToken)
    {
        string file = Path.Combine(Path.GetTempPath(), $"run-skill-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(file, body, cancellationToken);
        try
        {
            await using IDocumentSession session = _postgres.Store.LightweightSession();
            int exitCode = await ProjectRunSkillSetCommand.RunAsync(
                session,
                new ProjectRunSkillSetCommand.Settings { Project = ProjectName, File = file, Shape = shape },
                cancellationToken);
            exitCode.Should().Be(ExitCodes.Ok);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task<ProjectDetails> LoadProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        return (await session.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }

    private async Task<(NodeContext Node, Guid ProjectId, string Worktree)> SeedAsync(CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(_postgres.Store, cancellationToken);

        Guid projectId = DomainId.New();
        string projectHome = Path.Combine(_scopedHome.Home, "projects", ProjectName);
        string worktree = ProjectHomePaths.DevWorktree(projectHome);
        Directory.CreateDirectory(worktree);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), ProjectName, RepositoryPath, null, null, Now,
                homeDirectory: ProjectHome.Parse(projectHome)));
        await session.SaveChangesAsync(cancellationToken);

        return (node, projectId, worktree);
    }

    /// <summary>A second registered project sharing the seeded node, for the per-tick ceiling.</summary>
    private async Task<(Guid ProjectId, string Worktree)> SeedSecondProjectAsync(
        NodeContext node, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        string projectHome = Path.Combine(_scopedHome.Home, "projects", "second");
        string worktree = ProjectHomePaths.DevWorktree(projectHome);
        Directory.CreateDirectory(worktree);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), "second", RepositoryPath + "-second", null, null, Now,
                homeDirectory: ProjectHome.Parse(projectHome)));
        await session.SaveChangesAsync(cancellationToken);

        return (projectId, worktree);
    }

    /// <summary>
    /// Stands in for the discovery session: every spawn writes its scripted summary as a terminal
    /// result, synchronously, so <c>SessionResultWaiter</c> completes off the file alone with no
    /// pid ever marked alive. A test that expects NO spawn still constructs one with a script, so
    /// a regression that dispatches anyway fails on the empty-Spawns assertion rather than on a
    /// missing script.
    /// </summary>
    private sealed class ScriptedDiscoveryExecutor(params string?[] summaries) : IExecutor
    {
        private readonly Queue<string?> _summaries = new(summaries);
        private int _nextProcessId = 7_000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public FakeProcessManager Processes { get; } = new();

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            int processId = _nextProcessId++;
            string? summary = _summaries.Count > 0 ? _summaries.Dequeue() : null;
            if (summary is null)
            {
                return new SpawnedAgent(processId, Now);
            }

            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 1_000, ["output_tokens"] = 200 },
                ["total_cost_usd"] = 0.01,
                ["num_turns"] = 4,
                ["result"] = summary,
            });
            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName!),
                line + "\n", cancellationToken);
            return new SpawnedAgent(processId, Now);
        }
    }
}
