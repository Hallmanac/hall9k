using System.Diagnostics;

namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// Spawns through <c>/bin/sh -c "exec ..."</c>: <c>exec</c> replaces the shell's own process
/// image with the real command, so the pid this returns is the real command's pid for its
/// whole life, never an intermediary's — the same trick <see cref="Hall9k.Daemon.Execution.ClaudeExecutor"/>
/// used inline before this seam existed. Redirection is native shell syntax, so the child
/// owns its stdout/stderr file handle directly (log #2): nothing here is a pipe this
/// process would need to stay alive to keep draining.
/// </summary>
public sealed class UnixProcessManager : ProcessManagerBase
{
    public override SpawnedProcess Spawn(ProcessSpawnRequest request)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
            },
        };
        foreach ((string name, string value) in request.Environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"exec {ShellRedirection.Wrap(request)}");

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {request.Command}");
        }

        return new SpawnedProcess(process.Id, ReadStartedAt(process));
    }

    /// <summary>
    /// <c>pgrep -P &lt;pid&gt;</c>, native to both macOS and Linux, walked breadth-first by the
    /// shared base helper. Any failure (pgrep missing, a transient shell-out error) is swallowed
    /// and reads as "no children found" — best-effort naming only, never the kill decision itself
    /// (<see cref="ProcessManagerBase.CollectDescendants"/>'s own doc).
    /// </summary>
    protected override IReadOnlyList<int> CollectDescendants(int processId) =>
        CollectDescendantsBreadthFirst(processId, ChildrenOf);

    private static IEnumerable<int> ChildrenOf(int parentProcessId)
    {
        try
        {
            using Process pgrep = new()
            {
                StartInfo = new ProcessStartInfo("pgrep")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            pgrep.StartInfo.ArgumentList.Add("-P");
            pgrep.StartInfo.ArgumentList.Add(parentProcessId.ToString());
            pgrep.Start();
            return ParsePids(ReadOutputWithBoundedWait(pgrep, ChildProcessQueryTimeout));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }
}
