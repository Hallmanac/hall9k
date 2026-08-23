using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A command line that never reached a command has to teach, not crash. Origin incident
/// (2026-08-20): a bare <c>h9k task publish</c> printed
/// <c>Unhandled exception. Spectre.Console.Cli.CommandRuntimeException</c> over eight frames of
/// Spectre internals, which tells an agent nothing it can act on and tells a human that the tool is
/// broken rather than that the call was.
/// </summary>
public sealed class UsageErrorTests
{
    [Fact]
    public async Task A_missing_argument_names_itself_and_shows_a_working_call()
    {
        (int code, string output) = await Invoke("task", "publish");

        code.Should().Be(ExitCodes.Usage);
        output.Should().Contain("missing required argument 'ID'", "the refusal names what was left out");
        output.Should().Contain("h9k task publish 28b19893", "and shows a call that would have worked");
        output.Should().NotContain("Unhandled exception").And.NotContain("Spectre.Console.Cli.Command");
    }

    /// <summary>
    /// The second required argument is the one a caller is likeliest to forget, because the command
    /// looks complete without it.
    /// </summary>
    [Fact]
    public async Task A_command_one_argument_short_gets_its_own_help_and_not_the_root_help()
    {
        (int code, string output) = await Invoke("task", "link-jira", "28b19893");

        code.Should().Be(ExitCodes.Usage);
        output.Should().Contain("missing required argument 'KEY'");
        output.Should().Contain("h9k task link-jira <TASK> <KEY>", "the usage line is the command's own");
    }

    /// <summary>
    /// A settings rule broken before the command runs is the same species of failure and reaches
    /// the same handler: <c>review resolve</c> refuses to guess which verdict was meant.
    /// </summary>
    [Fact]
    public async Task A_broken_settings_rule_quotes_the_rule_and_the_help()
    {
        (int code, string output) = await Invoke("review", "resolve", "28b19893");

        code.Should().Be(ExitCodes.Usage);
        output.Should().Contain("--merge-ready", "the rule that was broken names both verdicts");
        output.Should().Contain("h9k review resolve 28b19893 --merge-ready", "and the example shows one");
    }

    [Fact]
    public async Task An_unrecognised_command_falls_back_to_the_root_help()
    {
        (int code, string output) = await Invoke("tsak", "publish");

        code.Should().Be(ExitCodes.Usage);
        output.Should().Contain("Unknown command 'tsak'");
        output.Should().Contain("h9k [OPTIONS] <COMMAND>", "with every branch listed underneath");
        output.Should().NotContain("Unhandled exception");
    }

    /// <summary>
    /// A stray token after a good command path must not drag the explanation up to the root: the
    /// deepest prefix that names something is the most useful thing to show.
    /// </summary>
    [Fact]
    public async Task An_unknown_option_still_explains_the_command_that_was_reached()
    {
        (int code, string output) = await Invoke("task", "publish", "--nope", "28b19893");

        code.Should().Be(ExitCodes.Usage);
        output.Should().Contain("h9k task publish <ID>", "the reached command explains itself");
    }

    [Theory]
    [InlineData(new[] { "task", "publish" }, new[] { "task", "publish" })]
    [InlineData(new[] { "task", "publish", "--assign" }, new[] { "task", "publish" })]
    [InlineData(new[] { "--version" }, new string[0])]
    [InlineData(new string[0], new string[0])]
    public void The_command_path_stops_at_the_first_option(string[] arguments, string[] expected) =>
        UsageError.CommandPath(arguments).Should().Equal(expected);

    /// <summary>
    /// Exactly what Program.cs does: run the real command tree, hand whatever Spectre propagated to
    /// <see cref="UsageError"/>, and read what the caller would have seen on stderr.
    /// </summary>
    private static async Task<(int Code, string Output)> Invoke(params string[] arguments)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        CommandApp app = new();
        app.Configure(CliCommandTree.Configure);

        try
        {
            int code = await app.RunAsync(arguments, cts.Token);
            return (code, string.Empty);
        }
        catch (CommandAppException exception)
        {
            StringWriter error = new();
            int code = await UsageError.ExplainAsync(exception, arguments, error, cts.Token);
            return (code, error.ToString());
        }
    }
}
