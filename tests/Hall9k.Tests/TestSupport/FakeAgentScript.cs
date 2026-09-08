using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// A scripted stand-in for a Claude session, described as steps — pause, emit a stream line,
/// exit — rather than as shell text, so the same script can be rendered for whichever shell
/// <see cref="PlatformShell"/> picks. The child is a real operating-system process with a real
/// pid, which is the whole point: <c>RunSupervisor</c> probes the pid's liveness and tails the
/// file the child writes, so a fake that ran in-process would exercise neither.
/// </summary>
internal sealed class FakeAgentScript
{
    private readonly List<Step> _steps = [];

    public static FakeAgentScript New() => new();

    /// <summary>Waits before the next step, the way a real session pauses between messages.</summary>
    public FakeAgentScript Pause(double seconds)
    {
        _steps.Add(new Step.Pause(seconds));
        return this;
    }

    /// <summary>Appends one line to the session's stream file.</summary>
    public FakeAgentScript Emit(string line)
    {
        _steps.Add(new Step.Emit(line));
        return this;
    }

    /// <summary>Ends the process with the given code, the way a session that dies mid-flight does.</summary>
    public FakeAgentScript Exit(int code)
    {
        _steps.Add(new Step.Exit(code));
        return this;
    }

    /// <summary>
    /// Starts the script as a child process writing to the two named files. The process is
    /// deliberately neither disposed nor awaited: the caller wants only its pid, and a script
    /// that outlives the test — one standing in for an agent still working — is exactly what
    /// several of these tests need.
    /// </summary>
    public Process Start(string standardOutputFile, string standardErrorFile)
    {
        Process process = new()
        {
            StartInfo = PlatformShell.StartInfo(
                RenderPosix(standardOutputFile, standardErrorFile),
                RenderPowerShell(standardOutputFile, standardErrorFile)),
        };
        process.Start();
        return process;
    }

    /// <summary>
    /// The shell's own redirection puts the whole script's output in the two files, exactly as
    /// <c>Hall9k.Daemon.ProcessManagement.ShellRedirection</c> does for a real spawn.
    /// </summary>
    private string RenderPosix(string standardOutputFile, string standardErrorFile)
    {
        IEnumerable<string> commands = _steps.Select(step => step switch
        {
            Step.Pause pause => $"sleep {pause.Seconds.ToString("0.###", CultureInfo.InvariantCulture)}",
            // printf, not echo: a result line's JSON carries an escaped newline, and sh's own
            // echo expands it — splitting the line in half and leaving nothing parseable. The
            // format string wants a literal backslash-n, hence the doubled backslash here.
            Step.Emit emit => $"printf '%s\\n' {PlatformShell.PosixLiteral(emit.Line)}",
            Step.Exit exit => $"exit {exit.Code}",
            _ => throw new InvalidOperationException($"unhandled step '{step}'"),
        });

        return $"({string.Join("; ", commands)}) > \"{standardOutputFile}\" 2> \"{standardErrorFile}\"";
    }

    /// <summary>
    /// PowerShell writes the files itself, through <c>System.IO.File</c>, rather than through the
    /// shell's redirection operators: <c>&gt;</c> means UTF-16 in Windows PowerShell and UTF-8 in
    /// PowerShell 7, while the tail reader this feeds
    /// (<c>Hall9k.Daemon.Execution.StreamTailReader</c>) reads UTF-8 unconditionally. Both files
    /// are truncated up front for the same reason the redirection operator does it: a stale file
    /// left by an earlier run would otherwise read as this one's output.
    /// </summary>
    private string RenderPowerShell(string standardOutputFile, string standardErrorFile)
    {
        string outputLiteral = PlatformShell.PowerShellLiteral(standardOutputFile);
        StringBuilder script = new();
        script.AppendLine($"[IO.File]::WriteAllText({outputLiteral}, '')");
        script.AppendLine(
            $"[IO.File]::WriteAllText({PlatformShell.PowerShellLiteral(standardErrorFile)}, '')");
        foreach (Step step in _steps)
        {
            script.AppendLine(step switch
            {
                Step.Pause pause => $"Start-Sleep -Milliseconds {(int)Math.Round(pause.Seconds * 1000)}",
                Step.Emit emit =>
                    $"[IO.File]::AppendAllText({outputLiteral}, " +
                    $"{PlatformShell.PowerShellLiteral(emit.Line)} + \"`n\")",
                Step.Exit exit => $"exit {exit.Code}",
                _ => throw new InvalidOperationException($"unhandled step '{step}'"),
            });
        }

        return script.ToString();
    }

    private abstract record Step
    {
        public sealed record Pause(double Seconds) : Step;

        public sealed record Emit(string Line) : Step;

        public sealed record Exit(int Code) : Step;
    }
}
