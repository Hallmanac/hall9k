using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Hall9k.Daemon.ProcessManagement;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// One suite, run for real on whichever OS is executing it (<see cref="ProcessManagers.ForCurrentPlatform"/>
/// picks <see cref="UnixProcessManager"/> or <see cref="WindowsProcessManager"/> the same way
/// <c>Program.cs</c> does) — the parity this task's acceptance criteria ask for is exactly this:
/// identical assertions passing against both real implementations, each proven on its own CI leg
/// rather than asserted from a fake. Commands are the one thing that cannot be written once, since
/// there is no shell both platforms share; each helper below picks the native equivalent.
/// </summary>
public sealed class ProcessManagerParityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hall9k-process-manager-parity-").FullName;
    private readonly IProcessManager _processManager = ProcessManagers.ForCurrentPlatform();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Spawn_reports_a_live_identity_for_a_process_that_is_still_running()
    {
        (string stdout, string stderr) = Files();
        string pidFilePath = Path.Combine(_directory, $"nested-child-pid-{Path.GetRandomFileName()}.txt");
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            NestedSleepCommand(pidFilePath), _directory, [], null, stdout, stderr));

        try
        {
            // NestedSleepCommand, not a one-line echo: an echo-and-exit command can complete
            // and be reaped before this assertion even runs, so IsAlive would legitimately
            // (and flakily) see "gone" and fail on a defect-free seam. A still-running
            // command is the only way this assertion means anything deterministically.
            _processManager.IsAlive(spawned.ProcessId, spawned.StartedAt).Should().BeTrue(
                "the identity Spawn just handed back names the process it started, not something already gone");
        }
        finally
        {
            _processManager.Terminate(spawned.ProcessId, spawned.StartedAt);
        }
    }

    [Fact]
    public async Task Spawn_redirects_real_output_to_the_requested_stdout_file()
    {
        (string stdout, string stderr) = Files();
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            EchoCommand("hall9k-parity-marker"), _directory, [], null, stdout, stderr));

        try
        {
            string output = await WaitForContentAsync(stdout);
            output.Should().Contain("hall9k-parity-marker",
                "the child owns its stdout file handle directly (log #2) — the marker has to land there without this test's process touching it");
        }
        finally
        {
            _processManager.Terminate(spawned.ProcessId, spawned.StartedAt);
        }
    }

    [Fact]
    public async Task Spawn_carries_stdin_from_the_requested_file()
    {
        string stdin = Path.Combine(_directory, "stdin.txt");
        await File.WriteAllTextAsync(stdin, "hall9k-parity-stdin\n");
        (string stdout, string stderr) = Files();

        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            CopyStandardInputCommand(), _directory, [], stdin, stdout, stderr));

        try
        {
            string output = await WaitForContentAsync(stdout);
            output.Should().Contain("hall9k-parity-stdin");
        }
        finally
        {
            _processManager.Terminate(spawned.ProcessId, spawned.StartedAt);
        }
    }

    [Fact]
    public async Task Spawn_carries_the_requested_environment_to_the_child()
    {
        (string stdout, string stderr) = Files();
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            PrintEnvironmentVariableCommand("HALL9K_PARITY_VAR"), _directory,
            [new KeyValuePair<string, string>("HALL9K_PARITY_VAR", "hall9k-parity-value")], null, stdout, stderr));

        try
        {
            string output = await WaitForContentAsync(stdout);
            output.Should().Contain("hall9k-parity-value");
        }
        finally
        {
            _processManager.Terminate(spawned.ProcessId, spawned.StartedAt);
        }
    }

    [Fact]
    public async Task IsAlive_turns_false_once_the_spawned_process_actually_exits()
    {
        (string stdout, string stderr) = Files();
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            EchoCommand("done"), _directory, [], null, stdout, stderr));

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (_processManager.IsAlive(spawned.ProcessId, spawned.StartedAt) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        _processManager.IsAlive(spawned.ProcessId, spawned.StartedAt).Should().BeFalse(
            "a command with nothing left to do has to actually exit, not linger as an intermediary");
    }

    [Fact]
    public void IsAlive_is_false_for_an_identity_this_seam_never_spawned()
    {
        // Reattach (Decisions Log #2) hinges on this being honest: a pid this seam does
        // not recognize, whatever the OS is doing with that number right now, is never
        // reported alive.
        _processManager.IsAlive(int.MaxValue - 1, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public async Task Terminate_kills_the_whole_process_tree_not_just_the_returned_pid()
    {
        (string stdout, string stderr) = Files();
        string pidFilePath = Path.Combine(_directory, $"nested-child-pid-{Path.GetRandomFileName()}.txt");
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            NestedSleepCommand(pidFilePath), _directory, [], null, stdout, stderr));

        // Give the nested child a moment to actually start before pulling the tree down
        // from under it — otherwise this could terminate before the grandchild exists at
        // all, proving nothing about kill-tree specifically.
        await Task.Delay(TimeSpan.FromSeconds(1));
        _processManager.IsAlive(spawned.ProcessId, spawned.StartedAt).Should().BeTrue(
            "the nested sleep has to actually be running for a tree-kill to mean anything");

        // What actually proves kill-tree, rather than a plain kill of spawned.ProcessId:
        // spawned.ProcessId is the wrapper (cmd.exe on Windows, the exec'd sh on Unix) and
        // dies from either a tree-kill or a plain one, so asserting on it alone (as this
        // test used to) cannot tell the two apart. The nested command writes its own real
        // child's pid to a file rather than being found by name and a start-time window —
        // a global process-table search can latch onto an unrelated same-named process
        // from a concurrent test or an unrelated shell on the machine, which either fails
        // a defect-free seam or lets a real kill-tree bug pass green.
        (int nestedChildProcessId, DateTimeOffset nestedChildStartedAt) = await AwaitNestedChildAsync(pidFilePath);

        _processManager.Terminate(spawned.ProcessId, spawned.StartedAt);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (_processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeFalse(
            "Terminate promises kill-tree — a long-sleeping nested child left running behind it would strand a real agent session's descendants exactly the way AbandonProcessGroup exists to prevent on the launchd side");
    }

    private (string Stdout, string Stderr) Files()
    {
        string suffix = Path.GetRandomFileName();
        return (Path.Combine(_directory, $"stdout-{suffix}.log"), Path.Combine(_directory, $"stderr-{suffix}.log"));
    }

    /// <summary>
    /// Reads the nested child's own pid back from the file <see cref="NestedSleepCommand"/>
    /// wrote it to, rather than searching the whole process table by name and a start-time
    /// window — a search that can latch onto an unrelated same-named process elsewhere on
    /// the machine (another test's <c>sleep</c>, a concurrent shell script) and prove
    /// nothing about this test's own tree.
    /// </summary>
    private static async Task<(int ProcessId, DateTimeOffset StartedAt)> AwaitNestedChildAsync(string pidFilePath)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(pidFilePath) &&
                int.TryParse((await File.ReadAllTextAsync(pidFilePath)).Trim(), out int nestedChildProcessId))
            {
                try
                {
                    using Process nestedChild = Process.GetProcessById(nestedChildProcessId);
                    if (TryReadStartTime(nestedChild) is { } started)
                    {
                        return (nestedChildProcessId, started);
                    }
                }
                catch (ArgumentException)
                {
                    // The pid file raced its own process's exit; fall through and retry.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new InvalidOperationException("The nested child's pid file never appeared.");
    }

    private static DateTimeOffset? TryReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static async Task<string> WaitForContentAsync(string filePath)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(filePath))
            {
                string content = await File.ReadAllTextAsync(filePath);
                if (content.Length > 0)
                {
                    return content;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return File.Exists(filePath) ? await File.ReadAllTextAsync(filePath) : string.Empty;
    }

    private static string EchoCommand(string marker) => $"echo {marker}";

    private static string CopyStandardInputCommand() =>
        OperatingSystem.IsWindows() ? "findstr \"^\"" : "cat";

    private static string PrintEnvironmentVariableCommand(string variableName) =>
        OperatingSystem.IsWindows() ? $"echo %{variableName}%" : $"echo ${variableName}";

    /// <summary>
    /// A command that spawns its own child, outlives its own immediate process long
    /// enough to prove Terminate reaches that child too (not just whatever pid Spawn
    /// returned), and writes that child's real pid to <paramref name="pidFilePath"/> so
    /// the test can identify it without searching the process table. Windows already gets
    /// the parent/child shape for free (the returned pid is always cmd.exe with the real
    /// command as its child) but has no shell built-in for "my last background job's pid"
    /// the way <c>$!</c> is on Unix, so it goes through PowerShell instead — passed as
    /// <c>-EncodedCommand</c> base64 rather than <c>-Command "..."</c> so the script's own
    /// quotes never have to survive <see cref="ShellRedirection"/> and
    /// <c>WindowsCommandLine.WrapForCmdExe</c>'s cmd.exe quoting on top of PowerShell's.
    /// </summary>
    private static string NestedSleepCommand(string pidFilePath) => OperatingSystem.IsWindows()
        ? $"powershell -NoProfile -NonInteractive -EncodedCommand {EncodeNestedPingScript(pidFilePath)}"
        // \$ rather than $: this whole string sits inside the outer exec'd sh's own
        // double-quoted argument (see UnixProcessManager), and double quotes do not
        // protect $ from that outer shell's own parameter expansion — unescaped, "$!"
        // would resolve to the outer shell's (empty) last-background-job pid before the
        // inner "sh -c" ever saw it, leaving the pid file blank.
        : $"sh -c \"sleep 30 & echo \\$! > '{pidFilePath}'; wait\"";

    private static string EncodeNestedPingScript(string pidFilePath)
    {
        string script =
            $"$p = Start-Process -FilePath ping -ArgumentList '-n','30','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            $"Set-Content -Path '{pidFilePath}' -Value $p.Id; " +
            "Wait-Process -Id $p.Id";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
}
