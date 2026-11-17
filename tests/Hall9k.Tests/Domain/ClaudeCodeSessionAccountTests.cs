using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The account an interactive session records: a claude-code connection, the default, or unrecognized, never
/// a path or an email, derived from the session's own CLAUDE_CONFIG_DIR, and kept beside the registered
/// conversation so <c>h9k task drive</c> can tell whether a resume would find it.
/// </summary>
public sealed class ClaudeCodeSessionAccountTests
{
    private static readonly string Home = OperatingSystem.IsWindows()
        ? "C:\\Users\\me"
        : "/Users/me";

    private static readonly string Alt = Path.Join(Home, ".claude-alt");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_blank_variable_is_the_default(string? variable)
    {
        ClaudeCodeSessionAccount.Derive(variable, [Connection(Alt)], Home)
            .Should().Be(ClaudeCodeSessionAccount.Default);
    }

    [Fact]
    public void A_directory_equal_under_the_path_rule_to_a_connections_is_that_connection()
    {
        ConnectionDetails alt = Connection(Alt);
        ConnectionDetails[] connections = [Connection(Path.Join(Home, ".claude-other")), alt];

        foreach (string variable in new[] { Alt, Alt + Path.DirectorySeparatorChar, "~/.claude-alt" })
        {
            ClaudeCodeSessionAccount.Derive(variable, connections, Home)
                .Should().Be(ClaudeCodeSessionAccount.Connection(alt.Id), $"'{variable}' normalizes to the recorded directory");
        }
    }

    [Fact]
    public void Any_other_value_is_unrecognized()
    {
        foreach (string variable in new[] { Path.Join(Home, ".claude-unknown"), "relative/dir", Path.GetPathRoot(Home) ?? "/" })
        {
            ClaudeCodeSessionAccount.Derive(variable, [Connection(Alt)], Home)
                .Should().Be(ClaudeCodeSessionAccount.Unrecognized, $"'{variable}' names no claude-code connection");
        }
    }

    [Fact]
    public void A_directory_another_provider_records_is_unrecognized()
    {
        ConnectionDetails github = Connection(Alt);
        github.Provider = WorkItemProvider.GitHub;

        ClaudeCodeSessionAccount.Derive(Alt, [github], Home)
            .Should().Be(ClaudeCodeSessionAccount.Unrecognized);
    }

    [Fact]
    public void A_direct_launch_records_the_account_it_spawned_under()
    {
        Guid id = DomainId.New();

        ClaudeCodeSessionAccount.For(new ClaudeCodeAccount(id, Alt, "a@example.com", "org"))
            .Should().Be(ClaudeCodeSessionAccount.Connection(id));
        ClaudeCodeSessionAccount.For(ClaudeCodeAccount.Default).Should().Be(ClaudeCodeSessionAccount.Default);
    }

    [Fact]
    public void The_stored_value_never_holds_a_path_and_reads_back_as_written()
    {
        Guid id = DomainId.New();
        InteractiveSessionStarted started = new(
            DomainId.New(), DomainId.New(), Now, 9001, "mac", "me", ClaudeCodeSessionAccount.Connection(id));

        string json = JsonSerializer.Serialize(started);

        json.Should().Contain($"\"{id}\"").And.NotContain(".claude");
        JsonSerializer.Deserialize<InteractiveSessionStarted>(json)?.ClaudeCodeAccount
            .Should().Be(ClaudeCodeSessionAccount.Connection(id));
        JsonSerializer.Deserialize<ClaudeCodeSessionAccount>("\"unrecognized\"").Should().Be(ClaudeCodeSessionAccount.Unrecognized);
        JsonSerializer.Deserialize<ClaudeCodeSessionAccount>("\"default\"").Should().Be(ClaudeCodeSessionAccount.Default);
        JsonSerializer.Deserialize<ClaudeCodeSessionAccount>("\"something-new\"").Should().Be(ClaudeCodeSessionAccount.Unrecognized);
    }

    [Fact]
    public void The_registered_session_keeps_its_account_and_an_old_registration_counts_as_the_default()
    {
        RunDetailsProjection projection = new();
        Guid runId = DomainId.New();
        Guid connection = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(runId)));

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(runId, DomainId.New(), Now, 9001, "mac", "mine", ClaudeCodeSessionAccount.Connection(connection))), view);
        view.RegisteredInteractiveClaudeAccount.Should().Be(ClaudeCodeSessionAccount.Connection(connection));

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(runId, DomainId.New(), Now, 9002, "mac", "mine")), view);
        view.RegisteredInteractiveClaudeAccount.Should().Be(ClaudeCodeSessionAccount.Default);
    }

    [Fact]
    public void A_build_session_leaves_the_registered_account_alone()
    {
        RunDetailsProjection projection = new();
        Guid runId = DomainId.New();
        Guid connection = DomainId.New();
        RunDetails view = projection.Create(new FakeEvent<RunDispatched>(Dispatched(runId)));
        projection.Apply(new FakeEvent<InteractiveSessionStarted>(
            new InteractiveSessionStarted(runId, DomainId.New(), Now, 9001, "mac", "mine", ClaudeCodeSessionAccount.Connection(connection))), view);

        projection.Apply(new FakeEvent<InteractiveSessionStarted>(new InteractiveSessionStarted(
            runId, DomainId.New(), Now, 9002, "mac", SessionRoleName.For(DomainId.Short(view.TaskId), SessionRoleName.Build))), view);

        view.RegisteredInteractiveClaudeAccount.Should().Be(ClaudeCodeSessionAccount.Connection(connection));
    }

    [Fact]
    public void A_run_document_written_before_the_field_existed_reads_the_default()
    {
        RunDetails view = JsonSerializer.Deserialize<RunDetails>("{}") ?? throw new InvalidOperationException();

        view.RegisteredInteractiveClaudeAccount.Should().Be(ClaudeCodeSessionAccount.Default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Register_session_reads_the_default_registry_when_the_variable_is_unset_or_blank(string? variable)
    {
        TaskRegisterSessionCommand.SessionsDirectory(variable, Home).Should().Be(Path.Combine(Home, ".claude", "sessions"));
    }

    [Fact]
    public void Register_session_reads_the_registry_under_its_own_config_directory()
    {
        TaskRegisterSessionCommand.SessionsDirectory(Alt, Home).Should().Be(Path.Combine(Alt, "sessions"));
    }

    private static ConnectionDetails Connection(string directory) => new()
    {
        Id = DomainId.New(),
        Provider = WorkItemProvider.ClaudeCode,
        ExternalAccountId = directory,
    };

    private static RunDispatched Dispatched(Guid id) => new(
        id, DomainId.New(), Guid.Empty, DomainId.New(), LeaseGeneration: 1, SessionId: id,
        WorktreePath: "/wt/x", Branch: "task/x", ExecutorMode.Subscription, Now);
}
