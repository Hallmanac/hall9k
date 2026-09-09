using System.Diagnostics;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.ProcessManagement;

/// <summary>
/// The Windows half of the seam (Decisions Log #3, S1-14): spawns through
/// <c>cmd.exe /c "..."</c> rather than <see cref="UnixProcessManager"/>'s <c>/bin/sh -c
/// "exec ..."</c>, because Windows has no <c>exec</c> equivalent that replaces a running
/// process's image in place — there is no way to hand back a pid that IS the real command
/// for its whole life without one. cmd.exe stays as the pid this returns instead, waiting
/// on the real command exactly as long as it runs (a plain <c>/c "command"</c> blocks until
/// the child exits), so <see cref="ProcessManagerBase.IsAlive"/> reads true for precisely
/// the real command's lifetime and <see cref="ProcessManagerBase.Terminate"/>'s kill-tree
/// takes cmd.exe and the real command together — the intermediary layer costs nothing a
/// caller can observe. Redirection is native cmd.exe syntax (<c>&lt;</c>, <c>&gt;</c>,
/// <c>2&gt;</c> all mean the same thing there as in <c>/bin/sh</c>), so the child owns its
/// stdout/stderr file handle directly, same as the Unix side (log #2).
/// </summary>
public sealed class WindowsProcessManager : ProcessManagerBase
{
    public override SpawnedProcess Spawn(ProcessSpawnRequest request)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach ((string name, string value) in request.Environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        // The raw Arguments string, never ArgumentList (see WindowsCommandLine): the
        // command this wraps already carries its own embedded quotes (a quoted claude
        // flag value, a quoted redirected file path), and ArgumentList would
        // C-runtime-escape them in a way cmd.exe's own /c parsing does not undo.
        process.StartInfo.Arguments = WindowsCommandLine.WrapForCmdExe(ShellRedirection.Wrap(request));

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {request.Command}");
        }

        return new SpawnedProcess(process.Id, ReadStartedAt(process));
    }

    /// <summary>
    /// Queries WMI's <c>Win32_Process</c> through PowerShell's own CIM cmdlets — no bundled .NET
    /// API exposes a process's children, and <c>wmic</c> is no longer guaranteed present, so this
    /// shells out the same way <see cref="UnixProcessManager"/> shells out to <c>pgrep</c>. One
    /// query fetches every process's own pid/parent-pid pair rather than one query per tree node
    /// (independent pre-PR review, cycle 1, conformance lens): the breadth-first walk this
    /// still does is over the map already read into memory, not over fresh PowerShell start-ups —
    /// a several-hundred-millisecond cost <see cref="ProcessManagerBase.TerminateTree"/> otherwise
    /// paid once per process in the tree, twice over on its root-still-alive branch, on the thread
    /// that just parsed a session's terminal result and is about to start the next gate. Any
    /// failure is swallowed and reads as "no children found" — best-effort naming only, never the
    /// kill decision itself (<see cref="ProcessManagerBase.CollectDescendants"/>'s own doc).
    /// </summary>
    protected override IReadOnlyList<int> CollectDescendants(int processId)
    {
        ILookup<int, int> childrenByParentId = QueryAllProcessParentPairs()
            .ToLookup(pair => pair.ParentProcessId, pair => pair.ProcessId);
        return CollectDescendantsBreadthFirst(processId, parentId => childrenByParentId[parentId]);
    }

    private static IEnumerable<(int ProcessId, int ParentProcessId)> QueryAllProcessParentPairs()
    {
        try
        {
            using Process query = new()
            {
                StartInfo = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            query.StartInfo.ArgumentList.Add("-NoProfile");
            query.StartInfo.ArgumentList.Add("-NonInteractive");
            query.StartInfo.ArgumentList.Add("-Command");
            query.StartInfo.ArgumentList.Add(
                "Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId),$($_.ParentProcessId)\" }");
            query.Start();
            return ParseProcessParentPairs(ReadOutputWithBoundedWait(query, ChildProcessQueryTimeout));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    private static IEnumerable<(int ProcessId, int ParentProcessId)> ParseProcessParentPairs(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int processId) && int.TryParse(parts[1], out int parentProcessId))
            {
                yield return (processId, parentProcessId);
            }
        }
    }
}
