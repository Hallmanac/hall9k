using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Refuses a lifecycle-changing verb before it reaches the store when this process is itself a
/// dispatched run's own headless session (task: a dispatched session cannot drive the project's own
/// lifecycle; decision 13f11af2). <see cref="Hall9k.Daemon.Execution.ClaudeExecutor.SpawnAsync"/>
/// and <c>Hall9k.Cli.Commands.HeadlessLaunch.SpawnDetached</c> are the only two places that ever stamp
/// <see cref="DispatchedRunEnvironment.RunIdVariable"/> onto a child process's own environment, and
/// every headless dispatch this platform makes goes through one of them — an operator's own attended
/// claim (<c>h9k task work</c>, a pasted prompt, <c>--direct-launch</c>) never carries it, so this
/// interceptor is a no-op for a human at a real terminal. The same two sites also stamp
/// <see cref="DispatchedRunEnvironment.TaskIdVariable"/> whenever the session is task-bound, so the
/// refusal below can name the task and not only the run (independent pre-PR review, cycle 1,
/// conformance finding) — a session with no owning task (card publication, courier delivery,
/// project-scoped run-skill discovery) carries no such variable, and the refusal degrades to naming
/// only the run for those, exactly as it always has.
/// <para>
/// Registered with Spectre.Console.Cli's own <c>IConfigurator.SetInterceptor</c>
/// (<see cref="CliCommandTree.Configure(IConfigurator, Func{string, string?})"/>), which runs after
/// Spectre has already resolved argument binding and settings validation but before a command's own
/// <c>ExecuteAsync</c> — nothing a refused verb would have done to the store ever runs. Spectre
/// resolves <c>--help</c> earlier still, before an interceptor is ever reached at all
/// (<c>CommandTreeHelpTests</c>'s own <c>StopOnceBound</c> proves the ordering empirically), so
/// <c>h9k task abandon --help</c> keeps rendering exactly as it always has even from inside a
/// dispatched run.
/// </para>
/// <para>
/// Reads the environment through an injected <see cref="Func{T,TResult}"/>, the same shape
/// <c>ProjectGitHubClient</c> already uses, rather than <see cref="Environment.GetEnvironmentVariable(string)"/>
/// directly — <c>Environment</c> is process-wide and shared across every parallel test class, and a
/// test proving a refusal must never mutate it.
/// </para>
/// </summary>
internal sealed class DispatchedSessionInterceptor(Func<string, string?> environmentVariable) : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (!DispatchedSessionCommandClassification.BySettingsType.TryGetValue(
                settings.GetType(), out (DispatchedSessionAccess Access, string Verb) classification)
            || classification.Access != DispatchedSessionAccess.Refused)
        {
            return;
        }

        string? runId = environmentVariable(DispatchedRunEnvironment.RunIdVariable);
        if (runId.IsBlank())
        {
            return;
        }

        string? taskId = environmentVariable(DispatchedRunEnvironment.TaskIdVariable);
        string whereClause = taskId.IsBlank()
            ? "a dispatched session working inside this task's own worktree"
            : $"a dispatched session working inside task {taskId}'s own worktree";

        throw new DomainBusinessRuleException(
            $"h9k {classification.Verb} is refused: this process is run {runId}, {whereClause}, and a "
            + "dispatched session cannot drive this task's — or another task's or idea's — own lifecycle "
            + "(decision 13f11af2). The orchestrator or a person owns this call; state the need in your "
            + "closing summary instead of retrying it.");
    }
}
