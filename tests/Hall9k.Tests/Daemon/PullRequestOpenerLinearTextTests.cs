using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// How the opener hands the Linear guard its facts, without opening a pull request: the composed
/// argument vector and body file for a Linear-tracked project, and the proof that the team keys are read
/// at most once and only when the text asks for it. The read is a delegate the test counts, so no test
/// here reaches Linear, <c>gh</c>, or GitHub.
/// </summary>
public sealed class PullRequestOpenerLinearTextTests : IDisposable
{
    private static readonly IReadOnlySet<string> Teams = new HashSet<string> { "ENG", "DES" };

    private readonly string _runDirectory = Directory.CreateTempSubdirectory("hall9k-pr-linear-").FullName;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public void Dispose()
    {
        _cancellation.Dispose();
        Directory.Delete(_runDirectory, recursive: true);
    }

    private sealed class CountingRead(IReadOnlySet<string>? keys)
    {
        public int Calls { get; private set; }

        public LinearTextScopeSource Source(string? own = "ENG-123") => new(own, _ =>
        {
            Calls++;
            return Task.FromResult(keys);
        });
    }

    private async Task<(IReadOnlyList<string> Arguments, string Body)> ComposeAsync(
        string? title, string body, string? externalReference, LinearTextScopeSource? linear,
        string objective = "Add rate limiting", string? agentSummary = null)
    {
        await File.WriteAllTextAsync(
            RunPaths.PrSummaryFile(_runDirectory), title is null ? body : $"Title: {title}\n\n{body}", Token);
        RunDetails run = new() { Id = DomainId.New(), RunDirectory = _runDirectory, Branch = "ENG-123-add-rate-limiting" };
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = objective,
            AcceptanceCriteria = ["Requests over the limit are refused"],
            ExternalReference = externalReference,
        };

        IReadOnlyList<string> arguments = await PullRequestOpener.CreateArgumentsAsync(
            NullLogger.Instance, run, task, agentSummary, sourceUrl: null, "main", WritingConventions.Default, linear,
            Token);
        return (arguments, await File.ReadAllTextAsync(Path.Combine(_runDirectory, "pr-body.md"), Token));
    }

    private static string TitleOf(IReadOnlyList<string> arguments) => arguments[arguments.ToList().IndexOf("--title") + 1];

    [Fact]
    public async Task A_pull_request_that_names_only_its_own_issue_makes_no_Linear_call()
    {
        CountingRead read = new(Teams);

        (IReadOnlyList<string> arguments, string body) = await ComposeAsync(
            "Add rate limiting", "Rate limiting for ENG-123.\n\nFixes ENG-123.", "linear:ENG-123", read.Source());

        read.Calls.Should().Be(0);
        TitleOf(arguments).Should().Be("ENG-123: Add rate limiting");
        body.Should().Contain("Fixes ENG-123.").And.NotContain("ticket");
    }

    [Fact]
    public async Task A_token_that_only_looks_like_a_key_reads_the_teams_once_and_then_stays_as_written()
    {
        CountingRead read = new(Teams);

        (IReadOnlyList<string> arguments, string body) = await ComposeAsync(
            "Fix SHA-256 hashing", "Fix SHA-256 hashing everywhere, and UTF-8 too.", "linear:ENG-123", read.Source());

        read.Calls.Should().Be(1);
        TitleOf(arguments).Should().Be("ENG-123: Fix SHA-256 hashing");
        body.Should().Contain("Fix SHA-256 hashing everywhere, and UTF-8 too.").And.NotContain("ticket");
    }

    [Fact]
    public async Task Another_issue_in_the_title_and_the_body_reads_the_teams_once_and_is_guarded_in_both()
    {
        CountingRead read = new(Teams);

        (IReadOnlyList<string> arguments, string body) = await ComposeAsync(
            "DES-5: Fix login", "Closes DES-5 and creates ENG-NEW.", "linear:ENG-123", read.Source());

        read.Calls.Should().Be(1);
        TitleOf(arguments).Should().Be("ENG-123: Fix login");
        body.Should().Contain("Closes ticket DES-5 and creates ENG NEW.");
    }

    [Fact]
    public async Task A_failed_team_read_costs_precision_and_never_the_pull_request()
    {
        CountingRead read = new(keys: null);

        (IReadOnlyList<string> arguments, string body) = await ComposeAsync(
            "DES-5: Fix login", "Closes DES-5 and Fix SHA-256 hashing.", "linear:ENG-123", read.Source());

        read.Calls.Should().Be(1);
        TitleOf(arguments).Should().Be("ENG-123: DES-5: Fix login", "the title rule needs the team keys and is skipped");
        body.Should().Contain("Closes ticket DES-5 and Fix ticket SHA-256 hashing.", "the body is matched by shape");
    }

    [Fact]
    public async Task The_skeleton_body_and_the_objective_title_are_guarded_too()
    {
        CountingRead read = new(Teams);
        string summaryFile = RunPaths.PrSummaryFile(_runDirectory);
        if (File.Exists(summaryFile))
        {
            File.Delete(summaryFile);
        }

        RunDetails run = new() { Id = DomainId.New(), RunDirectory = _runDirectory, Branch = "b" };
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Port login. Fixes DES-5.",
            AcceptanceCriteria = ["Closes DES-6"],
            ExternalReference = "linear:ENG-123",
        };

        IReadOnlyList<string> arguments = await PullRequestOpener.CreateArgumentsAsync(
            NullLogger.Instance, run, task, "Resolves DES-7", sourceUrl: null, "main", WritingConventions.Default,
            read.Source(), Token);

        TitleOf(arguments).Should().Be("ENG-123: Port login. Fixes.", "the objective title drops the other issue; the body only guards it");
        (await File.ReadAllTextAsync(Path.Combine(_runDirectory, "pr-body.md"), Token))
            .Should().Contain("Fixes ticket DES-5").And.Contain("Closes ticket DES-6").And.Contain("Resolves ticket DES-7");
        read.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("jira:PROJ-1")]
    [InlineData("github:Hallmanac/hall9k#42")]
    [InlineData(null)]
    public async Task A_pull_request_with_no_Linear_scope_is_byte_for_byte_what_it_was_and_asks_Linear_nothing(
        string? reference)
    {
        const string body = "Fix SHA-256 hashing. Fixes PROJ-1 and Closes DES-5.";

        (IReadOnlyList<string> arguments, string written) = await ComposeAsync(
            "Fixes PROJ-1 and DES-5", body, reference, linear: null);

        TitleOf(arguments).Should().Be(
            reference == "jira:PROJ-1" ? "PROJ-1: Fixes PROJ-1 and DES-5" : "Fixes PROJ-1 and DES-5");
        written.Should().Contain(body).And.NotContain("ticket");
    }

    [Fact]
    public void A_project_is_Linear_tracked_by_its_team_or_by_the_tasks_own_Linear_reference_and_by_nothing_else()
    {
        Func<CancellationToken, Task<IReadOnlySet<string>?>> read = _ => Task.FromResult<IReadOnlySet<string>?>(Teams);
        ProjectDetails plain = new() { Id = DomainId.New() };
        ProjectDetails bound = new() { Id = DomainId.New(), LinearTeam = LinearTeamKey.Parse("ENG") };

        PullRequestOpener.LinearTextFor(TaskFor("jira:PROJ-1"), plain, read).Should().BeNull();
        PullRequestOpener.LinearTextFor(TaskFor("github:Hallmanac/hall9k#42"), plain, read).Should().BeNull();
        PullRequestOpener.LinearTextFor(TaskFor(null), plain, read).Should().BeNull();

        OwnOf(PullRequestOpener.LinearTextFor(TaskFor(null), bound, read)).Should().BeNull();
        OwnOf(PullRequestOpener.LinearTextFor(TaskFor("github:Hallmanac/hall9k#42"), bound, read)).Should().BeNull();
        OwnOf(PullRequestOpener.LinearTextFor(TaskFor("linear:ENG-123"), bound, read)).Should().Be("ENG-123");
        OwnOf(PullRequestOpener.LinearTextFor(TaskFor("linear:DES-9"), plain, read))
            .Should().Be("DES-9", "a project moved off Linear still carries tasks whose own issue is one");
    }

    private static string? OwnOf(LinearTextScopeSource? source) =>
        (source ?? throw new InvalidOperationException("a Linear scope was expected")).OwnIdentifier;

    private static TaskDetails TaskFor(string? reference) => new()
    {
        Id = DomainId.New(),
        Objective = "Add rate limiting",
        AcceptanceCriteria = [],
        ExternalReference = reference,
    };
}
