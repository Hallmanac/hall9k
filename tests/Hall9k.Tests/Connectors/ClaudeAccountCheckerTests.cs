using FluentAssertions;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Connection;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The pre-spawn and add-time checks for a Claude Code config directory, driven through a fake
/// <c>claude auth status</c> so no real claude runs: each failure names the directory and its remedy, a pass
/// is reused for at most five minutes, and the default login is never probed.
/// </summary>
public sealed class ClaudeAccountCheckerTests
{
    private const string Directory = "/Users/someone/.claude-alt";
    private const string LoggedIn =
        """{"loggedIn":true,"authMethod":"claude.ai","email":"dev@example.com","orgId":"org-1","orgName":"Example Org","subscriptionType":"max"}""";
    private const string LoggedOut = """{"loggedIn":false,"authMethod":"none"}""";
    private const string ApiKey = """{"loggedIn":true,"authMethod":"api_key","apiKeySource":"ANTHROPIC_API_KEY"}""";
    private const string OauthToken = """{"loggedIn":true,"authMethod":"oauth_token"}""";

    private static readonly ClaudeCodeAccount Bound = new(Guid.NewGuid(), Directory, "dev@example.com", "org-1");

    private sealed class FakeProbe(params string[] answers) : IClaudeAuthStatusProbe
    {
        private int _index;

        public int Calls { get; private set; }

        public Task<ClaudeAuthStatusRead> ReadAsync(string configDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            string answer = answers[Math.Min(_index++, answers.Length - 1)];
            return Task.FromResult(ClaudeAuthStatus.Parse(answer) is { } status
                ? new ClaudeAuthStatusRead(status, null)
                : ClaudeAuthStatusRead.Failed("no JSON"));
        }
    }

    private static ClaudeAccountChecker Checker(
        FakeProbe probe, TimeProvider? time = null, bool exists = true, bool windows = false) =>
        new(probe, time, _ => exists, windows);

    [Fact]
    public async Task A_missing_directory_is_refused_before_claude_runs_since_claude_would_create_it()
    {
        FakeProbe probe = new(LoggedIn);

        ClaudeAccountCheckResult result = await Checker(probe, exists: false).CheckAsync(Bound, reusePass: true, CancellationToken.None);

        result.Outcome.Should().Be(ClaudeAccountCheckOutcome.DirectoryMissing);
        result.Message.Should().Contain(Directory).And.Contain("CLAUDE_CONFIG_DIR='/Users/someone/.claude-alt' claude");
        probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_logged_out_directory_is_refused_with_its_login_line()
    {
        ClaudeAccountCheckResult result = await Checker(new FakeProbe(LoggedOut)).CheckAsync(Bound, reusePass: true, CancellationToken.None);

        result.Outcome.Should().Be(ClaudeAccountCheckOutcome.LoggedOut);
        result.Message.Should().Contain("CLAUDE_CONFIG_DIR='/Users/someone/.claude-alt' claude").And.Contain("/login");
    }

    [Fact]
    public async Task The_login_line_is_powershell_on_windows_and_quotes_any_path()
    {
        ClaudeCodeAccount quoted = Bound with { ConfigDirectory = @"C:\Users\some one\it's" };

        ClaudeAccountCheckResult result = await Checker(new FakeProbe(LoggedOut), windows: true)
            .CheckAsync(quoted, reusePass: true, CancellationToken.None);

        result.Message.Should().Contain(@"$env:CLAUDE_CONFIG_DIR='C:\Users\some one\it''s'; claude");
        ClaudeAccountCheck.LoginLine("/tmp/it's", windows: false).Should().Be(@"CLAUDE_CONFIG_DIR='/tmp/it'\''s' claude");
    }

    [Theory]
    [InlineData(ApiKey, "ANTHROPIC_API_KEY")]
    [InlineData(OauthToken, "CLAUDE_CODE_OAUTH_TOKEN")]
    public async Task An_override_credential_is_refused_naming_the_variable(string answer, string variable)
    {
        ClaudeAccountCheckResult result = await Checker(new FakeProbe(answer)).CheckAsync(Bound, reusePass: true, CancellationToken.None);

        result.Outcome.Should().Be(ClaudeAccountCheckOutcome.OverrideCredential);
        result.Message.Should().Contain(variable).And.Contain(Directory);
    }

    [Fact]
    public async Task A_different_account_in_the_directory_is_refused_naming_both_remedies()
    {
        const string other = """{"loggedIn":true,"email":"other@example.com","orgId":"org-2","orgName":"Other"}""";

        ClaudeAccountCheckResult result = await Checker(new FakeProbe(other)).CheckAsync(Bound, reusePass: true, CancellationToken.None);

        result.Outcome.Should().Be(ClaudeAccountCheckOutcome.WrongAccount);
        result.Message.Should().Contain("other@example.com").And.Contain("dev@example.com")
            .And.Contain("h9k connection add claude-code --config-dir '/Users/someone/.claude-alt'");
    }

    [Fact]
    public async Task The_same_email_in_a_different_org_is_a_different_account()
    {
        const string otherOrg = """{"loggedIn":true,"email":"dev@example.com","orgId":"org-2"}""";

        ClaudeAccountCheckResult result = await Checker(new FakeProbe(otherOrg)).CheckAsync(Bound, reusePass: true, CancellationToken.None);

        result.Outcome.Should().Be(ClaudeAccountCheckOutcome.WrongAccount);
    }

    [Fact]
    public async Task A_pass_is_reused_for_five_minutes_and_then_checked_again()
    {
        FakeProbe probe = new(LoggedIn, LoggedOut);
        SettableClock clock = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        ClaudeAccountChecker checker = Checker(probe, clock);

        (await checker.CheckAsync(Bound, reusePass: true, CancellationToken.None)).Passed.Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(4));
        (await checker.CheckAsync(Bound, reusePass: true, CancellationToken.None)).Passed.Should().BeTrue();
        probe.Calls.Should().Be(1, "a pass inside the window is reused");

        clock.Advance(TimeSpan.FromMinutes(2));
        ClaudeAccountCheckResult after = await checker.CheckAsync(Bound, reusePass: true, CancellationToken.None);

        after.Outcome.Should().Be(ClaudeAccountCheckOutcome.LoggedOut);
        probe.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_re_check_without_reuse_probes_even_inside_the_window_and_a_failure_is_never_reused()
    {
        FakeProbe probe = new(LoggedIn, LoggedOut, LoggedIn);
        ClaudeAccountChecker checker = Checker(probe);

        await checker.CheckAsync(Bound, reusePass: true, CancellationToken.None);
        (await checker.CheckAsync(Bound, reusePass: false, CancellationToken.None)).Passed.Should().BeFalse();
        (await checker.CheckAsync(Bound, reusePass: true, CancellationToken.None)).Passed.Should().BeTrue(
            "the failed re-check dropped the earlier pass, so this one probed again");
        probe.Calls.Should().Be(3);
    }

    [Fact]
    public async Task The_default_login_is_never_probed()
    {
        FakeProbe probe = new(LoggedOut);

        ClaudeAccountCheckResult result = await Checker(probe).CheckAsync(ClaudeCodeAccount.Default, reusePass: false, CancellationToken.None);

        result.Passed.Should().BeTrue();
        probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Registering_a_missing_directory_is_refused_before_claude_runs()
    {
        FakeProbe probe = new(LoggedIn);

        ClaudeAccountCheckResult result = await Checker(probe, exists: false).CheckNewDirectoryAsync(Directory, CancellationToken.None);

        result.Outcome.Should().Be(ClaudeAccountCheckOutcome.DirectoryMissing);
        result.Message.Should().Contain(Directory);
        probe.Calls.Should().Be(0, "claude would create the directory it was pointed at");
    }

    [Fact]
    public async Task Registering_a_directory_accepts_whichever_account_names_itself()
    {
        ClaudeAccountCheckResult result = await Checker(new FakeProbe(LoggedIn)).CheckNewDirectoryAsync(Directory, CancellationToken.None);

        result.Passed.Should().BeTrue();
        result.Status!.Email.Should().Be("dev@example.com");
        result.Status.OrganizationName.Should().Be("Example Org");
        result.Status.SubscriptionType.Should().Be("max");
    }

    [Fact]
    public async Task Registering_a_logged_out_or_overridden_directory_is_refused()
    {
        (await Checker(new FakeProbe(LoggedOut)).CheckNewDirectoryAsync(Directory, CancellationToken.None))
            .Outcome.Should().Be(ClaudeAccountCheckOutcome.LoggedOut);
        (await Checker(new FakeProbe(ApiKey)).CheckNewDirectoryAsync(Directory, CancellationToken.None))
            .Outcome.Should().Be(ClaudeAccountCheckOutcome.OverrideCredential);
    }

    [Fact]
    public async Task The_real_probe_sets_the_directory_and_reads_the_json_whatever_the_exit_code()
    {
        RecordingEnvironmentProcessRunner runner = new(() => new ProcessResult(1, LoggedOut, string.Empty));

        ClaudeAuthStatusRead read = await new ClaudeAuthStatusProbe(runner.Runner).ReadAsync(Directory, CancellationToken.None);

        read.Status!.LoggedIn.Should().BeFalse();
        runner.Calls.Should().ContainSingle();
        runner.Calls[0].Arguments.Should().Equal("auth", "status");
        runner.Calls[0].Environment.Should().Contain(ClaudeConfigDirectory.Variable, Directory);
    }

    [Fact]
    public async Task The_real_probe_reports_a_failure_rather_than_throwing()
    {
        RecordingEnvironmentProcessRunner runner = new(() => throw new InvalidOperationException("no such binary"));

        ClaudeAuthStatusRead read = await new ClaudeAuthStatusProbe(runner.Runner).ReadAsync(Directory, CancellationToken.None);

        read.Status.Should().BeNull();
        read.Failure.Should().Contain("no such binary");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"email":"dev@example.com"}""")]
    [InlineData("")]
    public void Text_that_is_not_the_status_json_parses_to_nothing(string text) =>
        ClaudeAuthStatus.Parse(text).Should().BeNull();

    [Fact]
    public void A_bound_account_sets_the_exact_directory_and_the_default_removes_an_inherited_one()
    {
        Dictionary<string, string?> bound = new() { [ClaudeConfigDirectory.Variable] = "/elsewhere" };
        Dictionary<string, string?> unbound = new() { [ClaudeConfigDirectory.Variable] = "/elsewhere", ["PATH"] = "/bin" };

        ClaudeCodeEnvironment.Apply(bound, Bound);
        ClaudeCodeEnvironment.Apply(unbound, ClaudeCodeAccount.Default);

        bound[ClaudeConfigDirectory.Variable].Should().Be(Directory);
        unbound.Should().NotContainKey(ClaudeConfigDirectory.Variable);
        unbound.Should().ContainKey("PATH");
    }
}
