namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Whether h9k currently has an attached interactive child process (<c>h9k task work</c>'s own
/// spawned Claude Code session) sharing this terminal's foreground process group. Program.cs's
/// global Ctrl-C handler reads this to suppress its own escalate-to-terminate window while that
/// child is alive: the terminal already delivers SIGINT to the child directly and independently
/// of this process, so a second press within h9k's own escalation window is legitimate input to
/// the child — including the double-tap that is Claude Code's own exit gesture — not an
/// instruction to kill h9k out from under a launch it has not yet recorded as ended (adversarial
/// review, cycle 1: the second press otherwise terminated h9k before AppendSessionEndedAsync
/// ever ran, leaving InteractiveSessionStarted unpaired — the exact failure the escalation
/// window exists elsewhere to avoid).
/// </summary>
internal static class InteractiveChildGuard
{
    private static volatile bool attached;

    public static bool Attached => attached;

    /// <summary>Marks a child attached for the scope's lifetime; always paired with Dispose via `using`.</summary>
    public static IDisposable Enter()
    {
        attached = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => attached = false;
    }
}
