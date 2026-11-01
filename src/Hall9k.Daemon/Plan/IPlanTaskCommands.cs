namespace Hall9k.Daemon.Plan;

/// <summary>
/// What a finished child <c>h9k</c> command did: its exit code, whether it was killed for outrunning its bound, and the
/// file its output was kept in. The output is never read onto a stream, because it can quote a session's text.
/// </summary>
public sealed record TaskCommandResult(int ExitCode, bool TimedOut, string OutputFile)
{
    /// <summary>Whether the command exited zero inside its bound.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// The three task writes a dispatched session is refused (<c>task revise</c>, <c>task publish</c>, <c>task challenge</c>),
/// which a plan's daemon does itself. Behind an interface because the real implementation runs the installed <c>h9k</c> as a
/// child process: <c>TaskFileParser</c>, the tracker work, and the ledger record all live in <c>Hall9k.Cli</c>, which the
/// daemon does not reference, so the child gets the publish gate, the tracker item, and the record exactly as by hand.
/// </summary>
public interface IPlanTaskCommands
{
    /// <summary><c>h9k task revise &lt;id&gt; --file &lt;revision&gt;</c>.</summary>
    Task<TaskCommandResult> ReviseAsync(Guid planId, Guid sessionId, Guid taskId, string revisionFile, CancellationToken cancellationToken);

    /// <summary><c>h9k task publish &lt;id&gt; --no-queue</c>, with the attestations the plan recorded at start.</summary>
    Task<TaskCommandResult> PublishAsync(
        Guid planId, Guid taskId, bool noExistingItem, bool untracked, CancellationToken cancellationToken);
}
