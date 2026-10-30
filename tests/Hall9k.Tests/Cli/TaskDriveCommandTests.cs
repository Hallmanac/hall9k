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
/// <c>h9k task drive</c> is the only spelling of the interactive-claim verb. The tests walk the
/// shipped tree for the parts a caller sees: what the verb binds, where it is listed, and that a
/// dispatched session is refused it.
/// </summary>
public sealed class TaskDriveCommandTests
{
    [Fact]
    public void The_verb_binds_the_drive_settings()
    {
        (string name, CommandSettings settings) = Bind("drive", _ => null);

        name.Should().Be("drive");
        settings.Should().BeOfType<TaskDriveCommand.Settings>();
    }

    [Fact]
    public void The_task_help_lists_drive()
    {
        string taskHelp = CommandTreeHelpTests.Help(["task"]);

        taskHelp.Should().Contain("drive");
    }

    [Fact]
    public void A_dispatched_session_is_refused_the_drive_with_its_run_id()
    {
        string runId = DomainId.New().ToString();

        Action act = () => Bind("drive", name => name == DispatchedRunEnvironment.RunIdVariable ? runId : null);

        act.Should().Throw<DomainBusinessRuleException>()
            .Which.Message.Should().Contain("task drive").And.Contain(runId);
    }

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
