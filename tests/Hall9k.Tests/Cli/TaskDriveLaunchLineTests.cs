using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The line <c>h9k task drive</c> prints. The target shell is passed in, so one host renders both
/// the bash and the PowerShell form.
/// </summary>
public sealed class TaskDriveLaunchLineTests
{
    private static readonly Guid ResumedSession = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void A_fresh_bash_line_cds_into_the_worktree_and_names_every_argument()
    {
        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(Launch(), LaunchShell.Posix);

        text.Line.Should().Be(
            "cd \"/wt/task\" && claude --name \"abcd1234-interactive\" --settings \"/runs/r1/settings.json\" "
            + "--append-system-prompt-file \"/runs/r1/task-anchor.md\" --model \"claude-opus-5-5[1m]\" --effort high "
            + "\"Drive it.\"");
        text.FreshFallbackLine.Should().BeNull("a fresh line has nothing to fall back from");
        text.EffortNotice.Should().BeNull();
    }

    [Fact]
    public void A_fresh_powershell_line_chains_with_a_semicolon()
    {
        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(Launch(), LaunchShell.PowerShell);

        text.Line.Should().StartWith("cd \"/wt/task\"; claude --name ");
        text.Line.Should().NotContain("&&");
    }

    [Fact]
    public void Quotes_and_expansions_are_escaped_for_the_shell_the_line_is_written_for()
    {
        TaskDriveLaunch launch = Launch() with { WorktreePath = "/wt/a \"b\" $HOME `x`", OpeningMessage = "say \"hi\" $(id)" };

        string bash = LaunchTextDefaults.RenderTaskDrive(launch, LaunchShell.Posix).Line;
        string powerShell = LaunchTextDefaults.RenderTaskDrive(launch, LaunchShell.PowerShell).Line;

        bash.Should().Contain("cd \"/wt/a \\\"b\\\" \\$HOME \\`x\\`\"");
        bash.Should().EndWith("\"say \\\"hi\\\" \\$(id)\"");
        powerShell.Should().Contain("cd \"/wt/a `\"b`\" `$HOME ``x``\"");
        powerShell.Should().EndWith("\"say `\"hi`\" `$(id)\"");
    }

    [Fact]
    public void The_line_does_not_take_the_orchestrator_lines_lean_flags()
    {
        string line = LaunchTextDefaults.RenderTaskDrive(Launch(), LaunchShell.Posix).Line;

        line.Should().NotContain("--setting-sources");
        line.Should().NotContain("--strict-mcp-config");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Skip_permissions_is_passed_only_when_the_project_allows_it(bool skipPermissions)
    {
        string line = LaunchTextDefaults.RenderTaskDrive(Launch() with { SkipPermissions = skipPermissions }, LaunchShell.Posix).Line;

        line.Contains("--dangerously-skip-permissions", StringComparison.Ordinal).Should().Be(skipPermissions);
    }

    [Fact]
    public void A_resume_line_replaces_the_opening_message_and_keeps_the_anchor_settings_model_and_effort()
    {
        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(Launch() with { ResumeSessionId = ResumedSession }, LaunchShell.Posix);

        text.Line.Should().Be(
            $"cd \"/wt/task\" && claude --resume {ResumedSession} --name \"abcd1234-interactive\" "
            + "--settings \"/runs/r1/settings.json\" --append-system-prompt-file \"/runs/r1/task-anchor.md\" "
            + "--model \"claude-opus-5-5[1m]\" --effort high");
        text.Line.Should().NotContain("Drive it.");
    }

    [Fact]
    public void A_resume_line_carries_the_fresh_line_as_its_fallback()
    {
        TaskDriveLaunch fresh = Launch();

        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(fresh with { ResumeSessionId = ResumedSession }, LaunchShell.Posix);

        text.FreshFallbackLine.Should().Be(LaunchTextDefaults.RenderTaskDrive(fresh, LaunchShell.Posix).Line);
        text.FreshFallbackLine.Should().Contain("--append-system-prompt-file", "the fallback is a whole fresh line, anchor included");
        text.FreshFallbackLine.Should().NotContain("--resume");
    }

    [Fact]
    public void A_powershell_resume_line_uses_the_powershell_separator_in_both_lines()
    {
        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(Launch() with { ResumeSessionId = ResumedSession }, LaunchShell.PowerShell);

        text.Line.Should().Contain("\"; claude --resume ");
        text.FreshFallbackLine.Should().Contain("\"; claude --name ");
    }

    [Fact]
    public void With_no_effort_set_the_line_omits_it_and_says_the_models_default_applies()
    {
        TaskDriveLaunchText text = LaunchTextDefaults.RenderTaskDrive(Launch() with { Effort = AgentEffort.Unknown }, LaunchShell.Posix);

        text.Line.Should().NotContain("--effort");
        text.Line.Should().Contain("--model \"claude-opus-5-5[1m]\"");
        text.EffortNotice.Should().Be(LaunchTextDefaults.NoEffortNotice);
        text.EffortNotice.Should().Contain("model's own default");
    }

    [Fact]
    public void The_model_and_effort_are_the_ones_given_never_a_default_of_the_renderer()
    {
        TaskDriveLaunch launch = Launch() with { Model = AgentModel.Sonnet, Effort = AgentEffort.ExtraHigh };

        string line = LaunchTextDefaults.RenderTaskDrive(launch, LaunchShell.Posix).Line;

        line.Should().Contain("--model \"sonnet\" --effort xhigh");
    }

    [Fact]
    public void The_orchestrator_line_renders_the_same_text_it_always_did_for_the_host_shell()
    {
        LaunchTextDefaults.Render("/w", "hello").Should().Be(
            LaunchTextDefaults.Render("/w", "hello", LaunchTextDefaults.HostShell));
        LaunchTextDefaults.Render("/w", "hello", LaunchShell.PowerShell).Should().StartWith("cd \"/w\"; claude --strict-mcp-config");
        LaunchTextDefaults.Render("/w", "hello", LaunchShell.Posix).Should().StartWith("cd \"/w\" && claude --strict-mcp-config");
    }

    private static TaskDriveLaunch Launch() => new(
        WorktreePath: "/wt/task", AnchorFile: "/runs/r1/task-anchor.md", SettingsFile: "/runs/r1/settings.json",
        SessionName: "abcd1234-interactive", OpeningMessage: "Drive it.",
        Model: AgentModel.Resolve(null, null, null, "claude-opus-5-5[1m]"), Effort: AgentEffort.High,
        SkipPermissions: false, ResumeSessionId: null);
}
