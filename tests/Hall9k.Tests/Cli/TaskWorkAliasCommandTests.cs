using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k task work</c> is <c>h9k task drive</c> under its old name, kept hidden for one release
/// (task: <c>work</c> becomes <c>drive</c>). The tests walk the shipped tree for the parts a caller
/// sees, and drive the alias's own seam for the stderr line, because a real run would need the store.
/// </summary>
public sealed partial class TaskWorkAliasCommandTests
{
    [Theory]
    [InlineData("work")]
    [InlineData("drive")]
    public void The_old_and_the_new_verb_bind_the_same_settings(string verb)
    {
        (string name, CommandSettings settings) = Bind(verb, _ => null);

        name.Should().Be(verb);
        settings.Should().BeOfType<TaskDriveCommand.Settings>(
            "the alias is the same command, so every option and the dispatched-session classification are shared");
    }

    [Fact]
    public void The_alias_is_the_drive_command_with_one_extra_line()
    {
        typeof(TaskWorkAliasCommand).IsAssignableTo(typeof(TaskDriveCommand)).Should().BeTrue();
    }

    [Fact]
    public async Task The_alias_writes_its_notice_to_stderr_before_the_drive_runs_and_keeps_its_exit_code()
    {
        StringWriter error = new();
        string? errorWhenDriveStarted = null;

        int exitCode = await TaskWorkAliasCommand.RunAsync(
            error,
            () =>
            {
                errorWhenDriveStarted = error.ToString();
                return Task.FromResult(7);
            },
            CancellationToken.None);

        exitCode.Should().Be(7, "scripts keep the exit code the drive returned");
        errorWhenDriveStarted.Should().Be(
            "h9k task work is renamed h9k task drive; work will be removed in a later release." + Environment.NewLine,
            "the notice is one line, written before the drive does anything");
        error.ToString().Should().Be(errorWhenDriveStarted, "nothing else is written to stderr");
    }

    [Fact]
    public void The_alias_is_absent_from_the_task_help_and_the_root_help()
    {
        string taskHelp = CommandTreeHelpTests.Help(["task"]);
        string rootHelp = CommandTreeHelpTests.Help([]);

        CommandLine().IsMatch(taskHelp).Should().BeFalse("h9k task --help does not list the hidden alias");
        taskHelp.Should().Contain("drive", "the new name is listed");
        taskHelp.Should().NotContain("task work");
        rootHelp.Should().NotContain("task work");
    }

    [Theory]
    [InlineData("work")]
    [InlineData("drive")]
    public void A_dispatched_session_is_refused_either_name_the_same_way(string verb)
    {
        string runId = DomainId.New().ToString();

        Action act = () => Bind(verb, name => name == DispatchedRunEnvironment.RunIdVariable ? runId : null);

        act.Should().Throw<DomainBusinessRuleException>()
            .Which.Message.Should().Contain("task drive").And.Contain(runId);
    }

    // A line of the COMMANDS table that names the old verb, as opposed to prose that mentions work.
    [GeneratedRegex(@"^\s+work\s", RegexOptions.Multiline)]
    private static partial Regex CommandLine();

    /// <summary>
    /// Parses and binds one invocation through the shipped tree and stops at the interceptor, handing
    /// back what it saw; whatever the dispatched-session interceptor throws first propagates instead.
    /// </summary>
    private static (string Name, CommandSettings Settings) Bind(string verb, Func<string, string?> environmentVariable)
    {
        CaptureContext capture = new();
        CommandApp app = new();
        app.Configure(config =>
        {
            CliCommandTree.Configure(config, environmentVariable);
            config.SetInterceptor(capture);
            config.UseStrictParsing();
        });

        try
        {
            app.Run(["task", verb, "28b19893"]);
        }
        catch (CaptureContext.Captured)
        {
            // Bound and validated: exactly where this stops.
        }

        return capture.Context is { } context && capture.Settings is { } settings
            ? (context.Name, settings)
            : throw new InvalidOperationException($"'task {verb}' never reached a command.");
    }

    private sealed class CaptureContext : ICommandInterceptor
    {
        public CommandContext? Context { get; private set; }

        public CommandSettings? Settings { get; private set; }

        public void Intercept(CommandContext context, CommandSettings settings)
        {
            Context = context;
            Settings = settings;
            throw new Captured();
        }

        public sealed class Captured : Exception;
    }
}
