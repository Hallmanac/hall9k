using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run.Projections;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// Whether <c>h9k task drive</c> may offer a resume line. A conversation lives in the transcripts of the
/// config directory that started it, and the printed line carries the project's current binding, so a resume
/// is offered only when the registered session's account equals that binding. Otherwise the fresh line is the
/// line to paste, with a sentence saying why; transcripts are never copied between directories.
/// </summary>
public static class TaskDriveResume
{
    /// <param name="ResumeSessionId">The conversation to resume, or null for a fresh line.</param>
    /// <param name="CrossAccount">True when a registered conversation exists but another account holds it.</param>
    public sealed record Decision(Guid? ResumeSessionId, bool CrossAccount);

    /// <summary>The decision for <paramref name="run"/>'s registered session against the project's <paramref name="binding"/>.</summary>
    public static Decision For(RunDetails run, Guid? binding) =>
        Decide(run.RegisteredInteractiveClaudeSessionId, run.RegisteredInteractiveClaudeAccount, binding);

    /// <summary>
    /// <paramref name="registeredAccount"/> is the account the registration recorded, the default for one written
    /// before the account was; <paramref name="binding"/> is the project's claude-code connection, or null.
    /// </summary>
    public static Decision Decide(Guid? registeredSessionId, ClaudeCodeSessionAccount registeredAccount, Guid? binding) =>
        registeredSessionId switch
        {
            null => new Decision(null, CrossAccount: false),
            { } sessionId when registeredAccount.Matches(binding) => new Decision(sessionId, CrossAccount: false),
            _ => new Decision(null, CrossAccount: true),
        };
}
