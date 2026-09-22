using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="DispatchedSessionInterceptor"/> (task: a dispatched session cannot drive the
/// project's own lifecycle), driven entirely through its own injected environment reader — never
/// <see cref="Environment.GetEnvironmentVariable(string)"/>, which is process-wide and shared
/// across every parallel test class.
/// </summary>
public sealed class DispatchedSessionInterceptorTests
{
    private static readonly CommandContext Context =
        new([], new NoRemainingArguments(), "abandon", data: null);

    [Fact]
    public void A_refused_verb_is_refused_with_the_variable_set()
    {
        string runId = DomainId.New().ToString();
        DispatchedSessionInterceptor interceptor = new(name => name == DispatchedRunEnvironment.RunIdVariable ? runId : null);

        Action act = () => interceptor.Intercept(Context, new TaskAbandonCommand.Settings());

        DomainBusinessRuleException exception = act.Should().Throw<DomainBusinessRuleException>()
            .Which;
        exception.Message.Should().Contain("task abandon", "the refusal names the verb it refused");
        exception.Message.Should().Contain(runId, "the refusal names the run it refused inside");
        exception.Message.Should().Contain("orchestrator", "the refusal says who owns this call instead");
    }

    /// <summary>
    /// The task-bound spawn sites (<c>ClaudeExecutor.SpawnAsync</c>,
    /// <see cref="Hall9k.Cli.Commands.HeadlessLaunch.SpawnDetached"/>) stamp
    /// <see cref="DispatchedRunEnvironment.TaskIdVariable"/> alongside the run id, and the refusal
    /// has to name it too (independent pre-PR review, cycle 1, conformance finding) — an agent or an
    /// operator reading the refusal off a log could otherwise tell which run it came from but not
    /// which task, the fact that actually matters for finding the worktree.
    /// </summary>
    [Fact]
    public void A_refused_verb_names_the_task_when_the_task_variable_is_set()
    {
        string runId = DomainId.New().ToString();
        string taskId = DomainId.New().ToString();
        DispatchedSessionInterceptor interceptor = new(name => name switch
        {
            _ when name == DispatchedRunEnvironment.RunIdVariable => runId,
            _ when name == DispatchedRunEnvironment.TaskIdVariable => taskId,
            _ => null,
        });

        Action act = () => interceptor.Intercept(Context, new TaskAbandonCommand.Settings());

        DomainBusinessRuleException exception = act.Should().Throw<DomainBusinessRuleException>()
            .Which;
        exception.Message.Should().Contain(taskId, "the refusal names the task it refused inside, not only the run");
    }

    [Fact]
    public void The_same_refused_verb_is_untouched_with_the_variable_absent()
    {
        DispatchedSessionInterceptor interceptor = new(_ => null);

        Action act = () => interceptor.Intercept(Context, new TaskAbandonCommand.Settings());

        act.Should().NotThrow("an operator's own attended session never carries this variable, and must never be refused");
    }

    [Fact]
    public void An_allowed_verb_is_untouched_even_with_the_variable_set()
    {
        string runId = DomainId.New().ToString();
        DispatchedSessionInterceptor interceptor = new(name => name == DispatchedRunEnvironment.RunIdVariable ? runId : null);

        Action act = () => interceptor.Intercept(Context, new TaskVerifyCommand.Settings());

        act.Should().NotThrow("h9k task verify is a dispatched session's own legitimate act on its own run");
    }

    /// <summary>
    /// <c>pr reply</c> is the only route a review-feedback follow-up has to answer a review thread
    /// at all (<c>PullRequestReplyCommand</c>'s own doc), so classifying it Refused rather than
    /// Allowed silently breaks every such follow-up (independent pre-PR review, cycle 1, conformance
    /// and adversarial findings, both high severity).
    /// </summary>
    [Fact]
    public void Pr_reply_is_untouched_even_with_the_variable_set()
    {
        string runId = DomainId.New().ToString();
        DispatchedSessionInterceptor interceptor = new(name => name == DispatchedRunEnvironment.RunIdVariable ? runId : null);

        Action act = () => interceptor.Intercept(Context, new PullRequestReplyCommand.Settings());

        act.Should().NotThrow("pr reply posts no lifecycle state of its own and is the only route a follow-up has into a review thread");
    }

    /// <summary>
    /// Spectre resolves <c>--help</c> before an interceptor is ever reached at all
    /// (<see cref="CommandTreeHelpTests.Parse"/>'s own <c>StopOnceBound</c> proves the ordering
    /// empirically for the shipped tree), so a refused verb's help must still render even with the
    /// dispatched-run variable set — this drives the real, registered interceptor through the full
    /// pipeline rather than calling <see cref="DispatchedSessionInterceptor.Intercept"/> directly,
    /// since the property under test is Spectre's own pipeline ordering, not this class's logic.
    /// </summary>
    [Fact]
    public void Help_on_a_refused_verb_still_renders_with_the_variable_set()
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });

        CommandApp app = new();
        app.Configure(config =>
        {
            CliCommandTree.Configure(config, _ => DomainId.New().ToString());
            config.ConfigureConsole(console);
        });

        app.Run(["task", "abandon", "--help"]);

        writer.ToString().Should().Contain("DESCRIPTION:", "h9k task abandon --help still has to explain itself from inside a dispatched run");
    }

    private sealed class NoRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } = Array.Empty<string>().ToLookup(value => value, value => (string?)value);

        public IReadOnlyList<string> Raw { get; } = [];
    }
}
