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
}
