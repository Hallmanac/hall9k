using FluentAssertions;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The CLI half of binding a project to a Claude Code account: registering a directory, resolving
/// <c>--claude-code-account</c>, the refusal a CLI spawn exits with, and the doctor section. Every check runs
/// through a fake <c>claude auth status</c>, so no real claude runs and nothing touches the network.
/// </summary>
public sealed class ClaudeCodeAccountCliTests
{
    private const string LoggedIn =
        """{"loggedIn":true,"email":"dev@example.com","orgId":"org-1","orgName":"Example Org","subscriptionType":"max"}""";
    private const string LoggedOut = """{"loggedIn":false,"authMethod":"none"}""";
    private const string ApiKey = """{"loggedIn":true,"authMethod":"api_key","apiKeySource":"ANTHROPIC_API_KEY"}""";

    private static readonly string Home = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), "h9k-home"));
    private static readonly string Directory = Path.Join(Home, ".claude-alt");

    private sealed class FakeProbe(string answer) : IClaudeAuthStatusProbe
    {
        public Task<ClaudeAuthStatusRead> ReadAsync(string configDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(new ClaudeAuthStatusRead(ClaudeAuthStatus.Parse(answer), null));
    }

    private sealed class ThrowingProbe : IClaudeAuthStatusProbe
    {
        public Task<ClaudeAuthStatusRead> ReadAsync(string configDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the probe broke");
    }

    private static ClaudeAccountChecker Checker(IClaudeAuthStatusProbe probe) =>
        new(probe, directoryExists: _ => true, windows: false);

    private static ConnectionDetails ClaudeCode(string? directory = null) => new()
    {
        Id = DomainId.New(),
        Provider = WorkItemProvider.ClaudeCode,
        ExternalAccountId = directory ?? Directory,
        ClaudeCodeEmail = "dev@example.com",
        ClaudeCodeOrganizationId = "org-1",
    };

    // ---- connection add claude-code ----

    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly ClaudeAuthStatus Observed =
        new(true, "claude.ai", "dev@example.com", "org-1", "Example Org", "max", null);

    private static ConnectionAggregate Recorded(ClaudeAuthStatus status, bool refused)
    {
        Guid id = DomainId.New();
        ConnectionAggregate aggregate = new();
        foreach (object @event in ConnectionAddClaudeCodeCommand.RegistrationEvents(id, DomainId.New(), Directory, status, ObservedAt))
        {
            switch (@event)
            {
                case ConnectionRegistered registered:
                    aggregate.Apply(registered);
                    break;
                case ConnectionClaudeCodeAccountObserved observed:
                    aggregate.Apply(observed);
                    break;
            }
        }

        if (refused)
        {
            aggregate.Apply(new ConnectionClaudeCodeAccountRefused(id, "not logged in", ObservedAt));
        }

        return aggregate;
    }

    [Fact]
    public void Registering_takes_a_passing_checks_status_and_refuses_a_failed_one_with_the_checks_own_message()
    {
        ClaudeAccountCheckResult passed = new(ClaudeAccountCheckOutcome.Passed, Observed, "passed");
        ClaudeAccountCheckResult refused = new(ClaudeAccountCheckOutcome.LoggedOut, null, "the check's own text");

        ConnectionAddClaudeCodeCommand.AcceptedStatus(passed).Should().BeSameAs(Observed);
        Action act = () => ConnectionAddClaudeCodeCommand.AcceptedStatus(refused);

        act.Should().Throw<DomainValidationException>().WithMessage("Not registering this Claude Code connection. the check's own text");
    }

    [Fact]
    public void The_same_directory_again_re_observes_its_connection_and_a_different_directory_adds_a_second()
    {
        ConnectionDetails recorded = ClaudeCode();

        ConnectionAddClaudeCodeCommand.RecordedFor([recorded], Directory).Should().BeSameAs(recorded);
        ConnectionAddClaudeCodeCommand.RecordedFor([recorded], Path.Join(Home, ".claude-other")).Should().BeNull(
            "a directory not yet recorded starts a connection of its own beside the first");
    }

    [Fact]
    public void A_new_directory_records_the_connection_and_the_account_its_check_read()
    {
        Guid id = DomainId.New();

        IReadOnlyList<object> events = ConnectionAddClaudeCodeCommand.RegistrationEvents(
            id, DomainId.New(), Directory, Observed, ObservedAt);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ConnectionRegistered>().Which.Provider.Should().Be(WorkItemProvider.ClaudeCode);
        events[1].Should().Be(new ConnectionClaudeCodeAccountObserved(id, "dev@example.com", "org-1", "Example Org", "max", ObservedAt));
    }

    [Fact]
    public void Re_observing_records_a_changed_account_and_clears_a_refusal_and_otherwise_records_nothing()
    {
        ClaudeAuthStatus changed = Observed with { Email = "other@example.com" };

        ConnectionAddClaudeCodeCommand.ReobservationEvents(Recorded(Observed, refused: false), Observed, ObservedAt)
            .Should().BeEmpty("the same account, never refused, has nothing new to record");
        ConnectionAddClaudeCodeCommand.ReobservationEvents(Recorded(Observed, refused: false), changed, ObservedAt)
            .Should().ContainSingle().Which.Should().BeOfType<ConnectionClaudeCodeAccountObserved>()
            .Which.Email.Should().Be("other@example.com");
        ConnectionAddClaudeCodeCommand.ReobservationEvents(Recorded(Observed, refused: true), Observed, ObservedAt)
            .Should().ContainSingle().Which.Should().BeOfType<ConnectionClaudeCodeAccountCleared>();
    }

    // ---- project set --claude-code-account ----

    [Fact]
    public void The_option_accepts_the_full_id_the_short_id_or_the_directory()
    {
        ConnectionDetails connection = ClaudeCode();
        IReadOnlyList<ConnectionDetails> connections = [connection, ClaudeCode(Path.Join(Home, ".claude-other"))];

        ClaudeCodeAccountOption.Resolve(connection.Id.ToString(), connections, Home).Should().Be(connection.Id);
        ClaudeCodeAccountOption.Resolve(DomainId.Short(connection.Id), connections, Home).Should().Be(connection.Id);
        ClaudeCodeAccountOption.Resolve("~/.claude-alt/", connections, Home).Should().Be(connection.Id,
            "the directory is normalized by the same rule the connection was recorded under");
    }

    [Fact]
    public void Default_clears_the_binding() =>
        ClaudeCodeAccountOption.Resolve("default", [ClaudeCode()], Home).Should().BeNull();

    [Fact]
    public void Binding_names_the_account_and_what_moves_with_it()
    {
        string line = ClaudeCodeAccountOption.BindingConsequence(ClaudeCodeAccount.For(ClaudeCode()));

        line.Should().Contain("dev@example.com").And.Contain(Directory)
            .And.Contain("Work dispatched from now on runs under that account")
            .And.Contain("runs already dispatched keep the account they were dispatched under")
            .And.Contain("keep their old login until you relaunch them from a freshly printed line")
            .And.Contain("Regenerate the orchestrator recipe");
    }

    [Fact]
    public void Binding_an_account_with_no_recorded_email_calls_it_unobserved()
    {
        ConnectionDetails unobserved = ClaudeCode();
        unobserved.ClaudeCodeEmail = null;

        ClaudeCodeAccountOption.BindingConsequence(ClaudeCodeAccount.For(unobserved))
            .Should().Contain($"an unobserved account ({Directory})");
    }

    [Fact]
    public void Clearing_says_the_project_is_back_on_the_default_login() =>
        ClaudeCodeAccountOption.BindingConsequence(ClaudeCodeAccount.Default)
            .Should().Contain("back on the default Claude Code login")
            .And.Contain("runs already dispatched keep the account they were dispatched under")
            .And.Contain("keep the bound login until you relaunch them from a freshly printed line")
            .And.Contain("Regenerate the orchestrator recipe");

    [Fact]
    public void A_connection_this_node_does_not_record_is_refused()
    {
        Action act = () => ClaudeCodeAccountOption.Resolve("~/.claude-missing", [ClaudeCode()], Home);

        act.Should().Throw<DomainNotFoundException>().WithMessage("*h9k connection add claude-code*");
    }

    [Fact]
    public void A_connection_of_another_provider_is_refused()
    {
        ConnectionDetails github = new() { Id = DomainId.New(), Provider = WorkItemProvider.GitHub, ExternalAccountId = "someone" };

        Action act = () => ClaudeCodeAccountOption.Resolve(github.Id.ToString(), [github], Home);

        act.Should().Throw<DomainValidationException>().WithMessage("*'github' connection, not a claude-code one*");
    }

    [Fact]
    public void Connection_list_counts_the_projects_bound_to_a_claude_code_connection()
    {
        ConnectionDetails connection = ClaudeCode();
        ProjectDetails[] projects =
        [
            new() { Id = DomainId.New(), ClaudeCodeConnectionId = connection.Id },
            new() { Id = DomainId.New(), ClaudeCodeConnectionId = DomainId.New() },
            new() { Id = DomainId.New(), ConnectionId = connection.Id },
        ];

        ConnectionListCommand.BoundProjects(connection, projects).Should().Be(1);
    }

    [Fact]
    public void Project_show_names_the_binding_its_email_and_that_it_is_this_nodes_alone()
    {
        ConnectionDetails connection = ClaudeCode();
        ProjectDetails bound = new() { Id = DomainId.New(), Name = "sample", ClaudeCodeConnectionId = connection.Id };
        ProjectDetails unbound = new() { Id = DomainId.New(), Name = "sample" };

        ProjectShowCommand.ClaudeCodeAccountRow(bound, connection)
            .Should().Contain("dev@example.com").And.Contain(Directory).And.Contain("recorded on this node only");
        ProjectShowCommand.ClaudeCodeAccountRow(unbound, null)
            .Should().Contain("default login").And.Contain("recorded on this node only");
    }

    // ---- a CLI spawn's refusal ----

    [Fact]
    public async Task A_cli_spawn_under_a_refused_account_exits_with_the_check_text_before_anything_starts()
    {
        using IDisposable scope = CliClaudeAccount.OverrideForTests(Checker(new FakeProbe(LoggedOut)));
        ClaudeCodeAccount account = ClaudeCodeAccount.For(ClaudeCode());

        Func<Task> act = () => CliClaudeAccount.EnsureUsableAsync(account, CancellationToken.None);

        await act.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage($"*nothing was claimed or started*CLAUDE_CONFIG_DIR='{Directory}' claude*");
    }

    // ---- doctor ----

    [Fact]
    public async Task The_doctor_probe_never_throws_even_when_the_check_does()
    {
        ClaudeAccountChecker checker = Checker(new ThrowingProbe());
        ClaudeCodeAccount account = ClaudeCodeAccount.For(ClaudeCode());

        ClaudeCodeDoctorProbe probe = await ClaudeCodeDoctor.ProbeAsync(
            () => checker.CheckAsync(account, reusePass: false, CancellationToken.None).AsTask(), CancellationToken.None);

        probe.Passed.Should().BeFalse();
        probe.Failure.Should().Contain("the probe broke");
        ClaudeCodeDoctor.ProbeLine(ClaudeCode(), ["sample"], probe).Should().Contain("could not be checked");
    }

    [Fact]
    public async Task The_doctor_line_shows_the_email_org_and_subscription_or_the_problem_found()
    {
        ConnectionDetails connection = ClaudeCode();
        ClaudeCodeAccount account = ClaudeCodeAccount.For(connection);

        ClaudeCodeDoctorProbe passing = await ClaudeCodeDoctor.ProbeAsync(
            () => Checker(new FakeProbe(LoggedIn)).CheckAsync(account, reusePass: false, CancellationToken.None).AsTask(),
            CancellationToken.None);
        ClaudeCodeDoctorProbe failing = await ClaudeCodeDoctor.ProbeAsync(
            () => Checker(new FakeProbe(LoggedOut)).CheckAsync(account, reusePass: false, CancellationToken.None).AsTask(),
            CancellationToken.None);

        ClaudeCodeDoctor.ProbeLine(connection, ["sample"], passing)
            .Should().Contain("dev@example.com").And.Contain("Example Org").And.Contain("max");
        ClaudeCodeDoctor.ProbeLine(connection, ["sample"], failing)
            .Should().Contain("fails its check").And.Contain("not logged in");
    }

    [Fact]
    public void The_doctor_warns_about_every_override_variable_set_in_its_environment()
    {
        Dictionary<string, string> environment = new() { ["ANTHROPIC_AUTH_TOKEN"] = "x", ["CLAUDE_CODE_OAUTH_TOKEN"] = "y", ["PATH"] = "/bin" };

        ClaudeCodeDoctor.EnvironmentOverrides(name => environment.GetValueOrDefault(name))
            .Should().Equal("ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void The_doctor_warns_about_an_api_key_helper_or_an_override_in_a_bound_directorys_settings()
    {
        ClaudeCodeDoctor.SettingsOverrides("""{"apiKeyHelper":"/bin/key","env":{"ANTHROPIC_API_KEY":"x","OTHER":"y"}}""")
            .Should().Equal("apiKeyHelper", "env.ANTHROPIC_API_KEY");
        ClaudeCodeDoctor.SettingsOverrides("""{"model":"sonnet"}""").Should().BeEmpty();
        ClaudeCodeDoctor.SettingsOverrides("not json").Should().BeEmpty();
    }

    private static ProjectDetails Member(string name, SolutionMembership solution, Guid? binding) =>
        new() { Id = DomainId.New(), Name = name, Solution = solution, ClaudeCodeConnectionId = binding };

    [Fact]
    public void The_doctor_warns_when_a_solutions_members_run_under_different_accounts()
    {
        SolutionMembership split = new("01SPLIT", "split", SolutionState.Open, null, []);
        SolutionMembership agreed = new("01AGREED", "agreed", SolutionState.Open, null, []);
        Guid shared = DomainId.New();

        IReadOnlyList<string> warnings = ClaudeCodeDoctor.SolutionMismatches(
        [
            Member("first", split, shared),
            Member("second", split, null),
            Member("third", agreed, shared),
            Member("fourth", agreed, shared),
        ]);

        warnings.Should().ContainSingle().Which.Should().Contain("split").And.Contain("second on default");
    }

    // ---- CLAUDE_CONFIG_DIR in the daemon's environment and in doctor's own shell ----

    private static readonly DaemonProcessDescriptor RunningDaemon = new(4242, DateTimeOffset.Parse("2026-11-17T10:00:00Z"));

    private static bool Alive(int processId, DateTimeOffset startedAt) =>
        processId == RunningDaemon.ProcessId && startedAt == RunningDaemon.StartedAt;

    private static bool Dead(int processId, DateTimeOffset startedAt) => false;

    private static readonly Func<string, string?> ShellWithVariable =
        name => name == ClaudeConfigDirectory.Variable ? "/Users/dev/.claude-shell" : null;

    private static readonly Func<string, string?> ShellWithout = _ => null;

    private static DaemonPidRecord Record(string? inherited) => new(RunningDaemon, new DaemonInheritedEnvironment(inherited));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_doctor_warns_when_the_running_daemon_inherited_the_variable_whatever_its_own_shell_carries(bool shellCarries)
    {
        IReadOnlyList<string> lines = await DoctorLinesAsync(
            [], Record("/Users/dev/.claude-daemon"), Alive, shellCarries ? ShellWithVariable : ShellWithout);

        lines.Where(line => line.Contains("h9kd")).Should().ContainSingle()
            .Which.Should().Contain("[yellow]").And.Contain("/Users/dev/.claude-daemon")
            .And.Contain("regardless of that value").And.Contain("runs under its own connection's directory")
            .And.Contain("h9k connection add claude-code --config-dir \"/Users/dev/.claude-daemon\"")
            .And.Contain("h9k project set <project> --claude-code-account <connection>")
            .And.Contain("restart the daemon");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_daemon_warning_stays_silent_when_it_inherited_none_exited_or_never_ran_whatever_the_shell_carries(bool shellCarries)
    {
        Func<string, string?> shell = shellCarries ? ShellWithVariable : ShellWithout;

        foreach ((DaemonPidRecord? record, Func<int, DateTimeOffset, bool> isAlive) in new (DaemonPidRecord?, Func<int, DateTimeOffset, bool>)[]
        {
            (Record(null), Alive),
            (Record("/Users/dev/.claude-daemon"), Dead),
            (new DaemonPidRecord(RunningDaemon, null), Dead),
            (null, Alive),
        })
        {
            IReadOnlyList<string> lines = await DoctorLinesAsync([], record, isAlive, shell);
            lines.Should().NotContain(line => line.Contains("h9kd"));
            lines.Should().HaveCount(shellCarries ? 2 : 0, "only the shell's own warning, under its header, may print");
        }
    }

    [Fact]
    public void The_doctor_says_it_cannot_tell_for_a_running_daemon_that_left_no_record()
    {
        ClaudeCodeDoctor.DaemonConfigDirectoryLines(new DaemonPidRecord(RunningDaemon, null), Alive).Should().ContainSingle()
            .Which.Should().Contain("[yellow]").And.Contain("cannot tell").And.Contain("Restart the daemon");
    }

    [Fact]
    public void The_doctor_warns_when_its_own_shell_carries_the_variable_and_not_when_it_is_absent_or_empty()
    {
        ClaudeCodeDoctor.ShellConfigDirectoryLines(ShellWithVariable).Should().ContainSingle()
            .Which.Should().Contain("/Users/dev/.claude-shell").And.Contain("is set in this shell")
            .And.Contain("h9k task start").And.Contain("h9k orchestrator measure")
            .And.Contain("regardless of it").And.Contain("pasted into this shell")
            .And.Contain("a daemon started directly from this shell inherits").And.Contain("Unset it");
        ClaudeCodeDoctor.ShellConfigDirectoryLines(ShellWithout).Should().BeEmpty();
        ClaudeCodeDoctor.ShellConfigDirectoryLines(name => name == ClaudeConfigDirectory.Variable ? "" : null).Should().BeEmpty();
    }

    [Fact]
    public async Task Both_warnings_print_on_a_node_with_no_claude_code_connection_recorded()
    {
        IReadOnlyList<string> lines = await DoctorLinesAsync([], Record("/Users/dev/.claude-daemon"), Alive, ShellWithVariable);

        lines.Should().HaveCount(3);
        lines[0].Should().Contain("Claude Code accounts");
        lines[1].Should().Contain("h9kd").And.Contain("/Users/dev/.claude-daemon");
        lines[2].Should().Contain("/Users/dev/.claude-shell").And.Contain("is set in this shell");
    }

    [Fact]
    public async Task The_section_prints_nothing_on_a_node_with_no_connection_and_nothing_to_warn_about()
    {
        (await DoctorLinesAsync([], Record(null), Alive, ShellWithout)).Should().BeEmpty();
    }

    [Fact]
    public async Task Both_warnings_print_ahead_of_a_recorded_connections_check()
    {
        ConnectionDetails connection = ClaudeCode();
        IReadOnlyList<string> lines = await DoctorLinesAsync([connection], Record("/Users/dev/.claude-daemon"), Alive, ShellWithVariable);

        lines.Should().HaveCount(4);
        lines[1].Should().Contain("h9kd");
        lines[2].Should().Contain("is set in this shell");
        lines[3].Should().Contain("no project is bound to it");
    }

    private static async Task<IReadOnlyList<string>> DoctorLinesAsync(
        IReadOnlyList<ConnectionDetails> connections,
        DaemonPidRecord? record,
        Func<int, DateTimeOffset, bool> isAlive,
        Func<string, string?> shell)
    {
        List<string> lines = [];
        await ClaudeCodeDoctor.WriteAsync(
            new ClaudeCodeDoctorInputs(
                connections,
                [],
                record,
                isAlive,
                shell,
                (account, token) => Checker(new FakeProbe(LoggedIn)).CheckAsync(account, reusePass: false, token).AsTask()),
            lines.Add,
            CancellationToken.None);
        return lines;
    }
}
