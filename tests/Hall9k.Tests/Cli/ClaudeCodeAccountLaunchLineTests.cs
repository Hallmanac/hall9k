using FluentAssertions;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Every claude line Hall9k prints for a person to paste carries the project's bound Claude Code account
/// on the claude command itself, for a stored launch text and a computed default alike, and nothing for an
/// unbound project. The tests stop at composed strings.
/// </summary>
public sealed class ClaudeCodeAccountLaunchLineTests
{
    private const string Directory = "/Users/me/.claude-alt";
    private const string QuotedDirectory = "/Users/me/it's/.claude-alt";

    private static readonly Guid Session = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void The_default_window_line_carries_the_account_on_the_claude_command_in_posix()
    {
        string line = Window(LaunchTextDefaults.Render("/w", "hello", LaunchShell.Posix), Directory, LaunchShell.Posix);

        line.Should().Be(
            $"cd \"/w\" && CLAUDE_CONFIG_DIR='{Directory}' claude --strict-mcp-config --setting-sources project "
            + "--settings recipes/settings.json --dangerously-skip-permissions "
            + "--append-system-prompt-file recipes/launch-anchor.md \"hello\"");
    }

    [Fact]
    public void The_default_window_line_sets_the_variable_before_claude_in_powershell()
    {
        string line = Window(LaunchTextDefaults.Render("C:\\w", "hello", LaunchShell.PowerShell), "C:\\Users\\me\\.claude-alt", LaunchShell.PowerShell);

        line.Should().StartWith("cd \"C:\\w\"; $env:CLAUDE_CONFIG_DIR='C:\\Users\\me\\.claude-alt'; claude --strict-mcp-config ");
    }

    [Fact]
    public void A_stored_text_gets_the_prefix_before_its_first_claude_command_word()
    {
        Window("claude --model opus", Directory, LaunchShell.Posix).Should().Be($"CLAUDE_CONFIG_DIR='{Directory}' claude --model opus");
        Window("cd /x || exit 1; claude -c", Directory, LaunchShell.Posix)
            .Should().Be($"cd /x || exit 1; CLAUDE_CONFIG_DIR='{Directory}' claude -c");
        Window("echo hi | claude -p", Directory, LaunchShell.Posix)
            .Should().Be($"echo hi | CLAUDE_CONFIG_DIR='{Directory}' claude -p");
        Window("cd /x; claude", Directory, LaunchShell.PowerShell)
            .Should().Be($"cd /x; $env:CLAUDE_CONFIG_DIR='{Directory}'; claude");
    }

    [Fact]
    public void A_claude_inside_a_quoted_argument_is_not_the_command_word()
    {
        string posix = Window("cd \"/repos/claude\" && echo 'a; claude' && claude --x", Directory, LaunchShell.Posix);
        string powerShell = Window("cd \"C:\\claude `\" x\"; claude --x", Directory, LaunchShell.PowerShell);

        posix.Should().Be($"cd \"/repos/claude\" && echo 'a; claude' && CLAUDE_CONFIG_DIR='{Directory}' claude --x");
        powerShell.Should().Be($"cd \"C:\\claude `\" x\"; $env:CLAUDE_CONFIG_DIR='{Directory}'; claude --x");
    }

    [Fact]
    public void A_word_that_only_starts_with_claude_or_follows_another_word_is_not_the_command_word()
    {
        LaunchLineAccount.FindClaudeCommandWord("claudex --y", LaunchShell.Posix).Should().BeNull();
        LaunchLineAccount.FindClaudeCommandWord("npx claude", LaunchShell.Posix).Should().BeNull();
        LaunchLineAccount.FindClaudeCommandWord("cd /x && claude", LaunchShell.Posix).Should().Be(9);
    }

    [Fact]
    public void A_quote_in_the_directory_survives_each_shell()
    {
        LaunchLineAccount.Prefix(QuotedDirectory, LaunchShell.Posix)
            .Should().Be("CLAUDE_CONFIG_DIR='/Users/me/it'\\''s/.claude-alt' ");
        LaunchLineAccount.Prefix(QuotedDirectory, LaunchShell.PowerShell)
            .Should().Be("$env:CLAUDE_CONFIG_DIR='/Users/me/it''s/.claude-alt'; ");
    }

    [Fact]
    public void A_bound_stored_text_with_no_claude_command_word_is_printed_unchanged_with_a_notice()
    {
        PrintedLaunchLine printed = LaunchLineAccount.ForWindow(
            new LaunchText(LaunchText.DefaultCli, "my-wrapper --start"), Directory, LaunchShell.Posix);

        printed.Line.Should().Be("my-wrapper --start");
        printed.Notice.Should().Be(LaunchLineAccount.NoCommandWordNotice(Directory));
        printed.Notice.Should().Contain("does not carry the project's Claude Code account").And.Contain(Directory);
    }

    [Fact]
    public void A_launch_text_for_another_cli_never_gets_the_prefix()
    {
        PrintedLaunchLine printed = LaunchLineAccount.ForWindow(new LaunchText("codex", "claude --x"), Directory, LaunchShell.Posix);

        printed.Line.Should().Be("claude --x");
        printed.Notice.Should().BeNull();
    }

    [Theory]
    [InlineData(LaunchShell.Posix)]
    [InlineData(LaunchShell.PowerShell)]
    public void An_unbound_line_is_byte_identical(LaunchShell shell)
    {
        string text = LaunchTextDefaults.Render("/w", "hello", shell);

        LaunchLineAccount.ForWindow(new LaunchText(LaunchText.DefaultCli, text), null, shell)
            .Should().Be(new PrintedLaunchLine(text, null));
        LaunchTextDefaults.DiscoveryResumeLine("/p", Session, shell, configDirectory: null)
            .Should().Be(LaunchTextDefaults.DiscoveryResumeLine("/p", Session, shell));
    }

    [Fact]
    public void The_task_drive_resume_fresh_and_gate_lines_carry_the_account()
    {
        TaskDriveLaunch launch = TaskLaunch() with { ConfigDirectory = Directory };

        TaskDriveLaunchText resumed = LaunchTextDefaults.RenderTaskDrive(launch with { ResumeSessionId = Session }, LaunchShell.Posix);
        TaskDriveLaunchText gate = LaunchTextDefaults.RenderTaskDrive(launch, LaunchShell.PowerShell);

        resumed.Line.Should().StartWith($"cd \"/wt/task\" && CLAUDE_CONFIG_DIR='{Directory}' claude --resume {Session} ");
        resumed.FreshFallbackLine.Should().StartWith($"cd \"/wt/task\" && CLAUDE_CONFIG_DIR='{Directory}' claude --name ");
        gate.Line.Should().StartWith($"cd \"/wt/task\"; $env:CLAUDE_CONFIG_DIR='{Directory}'; claude --name ");
    }

    [Fact]
    public void The_discovery_resume_line_carries_the_account()
    {
        LaunchTextDefaults.DiscoveryResumeLine("/p", Session, LaunchShell.Posix, QuotedDirectory)
            .Should().Be($"cd \"/p\" && CLAUDE_CONFIG_DIR='/Users/me/it'\\''s/.claude-alt' claude --resume {Session}");
        LaunchTextDefaults.DiscoveryResumeLine("C:\\p", Session, LaunchShell.PowerShell, QuotedDirectory)
            .Should().Be($"cd \"C:\\p\"; $env:CLAUDE_CONFIG_DIR='/Users/me/it''s/.claude-alt'; claude --resume {Session}");
    }

    [Fact]
    public void A_cross_account_resume_prints_the_fresh_line_with_a_sentence_saying_why()
    {
        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(TaskLaunch() with { CrossAccountResume = true }, LaunchShell.Posix);

        text.Line.Should().Be(LaunchTextDefaults.RenderTaskDrive(TaskLaunch(), LaunchShell.Posix).Line);
        text.FreshFallbackLine.Should().BeNull();
        text.CrossAccountNotice.Should().Contain("different Claude Code account").And.Contain("cannot be resumed");
    }

    [Fact]
    public void A_resume_is_offered_only_under_the_account_the_session_registered_under()
    {
        Guid bound = DomainId.New();
        Guid other = DomainId.New();

        TaskDriveResume.Decide(Session, ClaudeCodeSessionAccount.Connection(bound), bound)
            .Should().Be(new TaskDriveResume.Decision(Session, false), "the account matches");
        TaskDriveResume.Decide(Session, ClaudeCodeSessionAccount.Connection(other), bound)
            .Should().Be(new TaskDriveResume.Decision(null, true), "the binding changed since registration");
        TaskDriveResume.Decide(Session, ClaudeCodeSessionAccount.Connection(bound), null)
            .Should().Be(new TaskDriveResume.Decision(null, true), "the binding was cleared since registration");
        TaskDriveResume.Decide(Session, ClaudeCodeSessionAccount.Default, bound)
            .Should().Be(new TaskDriveResume.Decision(null, true), "an old registration counts as the default, and the project is bound");
        TaskDriveResume.Decide(Session, ClaudeCodeSessionAccount.Default, null)
            .Should().Be(new TaskDriveResume.Decision(Session, false), "an old registration on an unbound project resumes as today");
        TaskDriveResume.Decide(Session, ClaudeCodeSessionAccount.Unrecognized, null)
            .Should().Be(new TaskDriveResume.Decision(null, true), "an unrecognized account is never offered a resume");
        TaskDriveResume.Decide(null, ClaudeCodeSessionAccount.Default, bound)
            .Should().Be(new TaskDriveResume.Decision(null, false), "nothing registered is nothing to refuse");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Launch_text_set_refuses_a_text_naming_the_variable_at_either_scope(bool projectScope)
    {
        string[] arguments = projectScope
            ? ["orchestrator", "launch-text", "set", "--project", "web", $"cd /w && CLAUDE_CONFIG_DIR='{Directory}' claude"]
            : ["orchestrator", "launch-text", "set", $"cd /w && CLAUDE_CONFIG_DIR='{Directory}' claude"];
        CommandApp app = new();
        app.Configure(config =>
        {
            CliCommandTree.Configure(config, _ => null);
            config.PropagateExceptions();
        });

        Func<Task> act = () => app.RunAsync(arguments);

        (await act.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should()
            .Contain("comes from the project's binding and is added when the line is printed")
            .And.Contain("h9k project set <p> --claude-code-account")
            .And.Contain("must have its CLAUDE_CONFIG_DIR prefix removed before it is set");
    }

    [Theory]
    [InlineData(LaunchShell.Posix)]
    [InlineData(LaunchShell.PowerShell)]
    public void A_line_show_printed_stores_once_its_prefix_is_removed_and_prints_with_it_exactly_once(LaunchShell shell)
    {
        LaunchText computed = new(LaunchText.DefaultCli, LaunchTextDefaults.Render("/w", "hello", shell));
        string shown = LaunchLineAccount.ForWindow(computed, QuotedDirectory, shell).Line;

        Action storeAsShown = () => LaunchLineAccount.RefuseStoredAccount(shown);
        storeAsShown.Should().Throw<DomainValidationException>();

        string withoutPrefix = shown.Replace(LaunchLineAccount.Prefix(QuotedDirectory, shell), string.Empty, StringComparison.Ordinal);
        LaunchLineAccount.RefuseStoredAccount(withoutPrefix);
        LaunchText stored = OrchestratorLaunchTextResolution.WithText([], LaunchText.DefaultCli, withoutPrefix).Single();
        string shownAgain = LaunchLineAccount.ForWindow(stored, QuotedDirectory, shell).Line;

        shownAgain.Should().Be(shown);
        CountOf(shownAgain, "CLAUDE_CONFIG_DIR").Should().Be(1);
    }

    [Fact]
    public void A_solution_line_carries_the_account_only_when_every_member_agrees()
    {
        ConnectionDetails alt = Connection(Directory);
        ConnectionDetails other = Connection("/Users/me/.claude-other");
        ConnectionDetails[] connections = [alt, other];

        CliClaudeAccount.SolutionLineDirectory([Member(alt.Id), Member(alt.Id)], connections).Should().Be(Directory);
        CliClaudeAccount.SolutionLineDirectory([Member(alt.Id), Member(other.Id)], connections).Should().BeNull();
        CliClaudeAccount.SolutionLineDirectory([Member(alt.Id), Member(null)], connections).Should().BeNull();
        CliClaudeAccount.SolutionLineDirectory([Member(null), Member(null)], connections).Should().BeNull();

        SolutionHomeInit.SolutionLine("/s", "pair", Directory, LaunchShell.Posix)
            .Should().StartWith($"cd \"/s\" && CLAUDE_CONFIG_DIR='{Directory}' claude ");
        SolutionHomeInit.SolutionLine("/s", "pair", null, LaunchShell.Posix)
            .Should().Be(LaunchTextDefaults.Render("/s", OrchestratorRecipeContext.SolutionOpeningMessage("pair"), LaunchShell.Posix));
    }

    private static string Window(string text, string? directory, LaunchShell shell) =>
        LaunchLineAccount.ForWindow(new LaunchText(LaunchText.DefaultCli, text), directory, shell).Line;

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static ConnectionDetails Connection(string directory) => new()
    {
        Id = DomainId.New(),
        Provider = WorkItemProvider.ClaudeCode,
        ExternalAccountId = directory,
    };

    private static ProjectDetails Member(Guid? connectionId) => new()
    {
        Id = DomainId.New(),
        Name = "member",
        ClaudeCodeConnectionId = connectionId,
    };

    private static TaskDriveLaunch TaskLaunch() => new(
        WorktreePath: "/wt/task", AnchorFile: "/runs/r1/task-anchor.md", SettingsFile: "/runs/r1/settings.json",
        SessionName: "abcd1234-interactive", OpeningMessage: "Drive it.",
        Model: AgentModel.Resolve(null, null, null, "claude-opus-5-5[1m]"), Effort: AgentEffort.High,
        SkipPermissions: false, ResumeSessionId: null);
}
