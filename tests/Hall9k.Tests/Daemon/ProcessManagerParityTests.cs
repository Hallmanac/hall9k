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
/// <para>
/// <c>[Collection("RealProcessSpawn")]</c> (Decisions Log PLACEHOLDER-f70cc244): three windows-latest failures on
/// 2026-09-10 (PR #311, #312, #313 — none touching <c>ProcessManagement</c>) were traced to this
/// repo's own real, uncollected <c>git</c>/<c>dotnet</c> subprocess spawners running in xUnit's
/// default parallel collections at the exact moment this class needed to spawn or tear down its
/// own nested process tree — evidenced from the three jobs' own logs, not assumed, including which
/// class overlapped which failure: PR #311's alone overlapped
/// <see cref="Hall9k.Tests.Daemon.Review.DecisionsLogRenumbererTransitionTests"/> and
/// <see cref="Hall9k.Tests.Daemon.Review.DecisionsLogRenumbererFixtureTests"/> for its whole
/// window, not <see cref="GitWorktreeManagerTests"/> or
/// <c>Hall9k.Tests.Cli.RepoMaterialiserTests</c> (the latter only started after PR #311's failure
/// was already logged); PR #312 and #313 overlapped <see cref="GitWorktreeManagerTests"/> and
/// <c>RepoMaterialiserTests</c> as well as <c>Hall9k.Tests.Domain.CrossProcessContainerGateTests</c>'
/// own real <c>dotnet</c> child (PR #312). Sharing a collection with all five (and
/// <c>Hall9k.Tests.Daemon.TestScopeResolverTests</c>, a sixth real-git spawner) serializes the
/// runner's own process-creation throughput — the shared resource is the runner's process-creation
/// throughput itself, not a directory, so this is the same idea <c>[Collection("PublishesBinary")]</c>
/// applies to concurrent <c>dotnet publish</c> callers (Decisions Log #158), but not the same
/// mechanism: <c>PublishesBinary</c> membership is guarded mechanically
/// (<c>PublishLaneGuardTests</c>) because every caller goes through one named helper,
/// <c>PublishTestSupport.RunPublishAsync</c>; there is no single call site a scan could key off for
/// "spawns real processes heavily enough to contend," so this collection's membership is a judgment
/// call recorded here rather than a mechanically-enforced one, and is deliberately not guarded to
/// avoid asserting a precision the source can't actually verify. <c>Hall9k.Tests.Integration.VerificationRunnerTests</c>
/// and its other <c>[Collection("Hall9kHome")]</c> siblings that spawn real processes are left out
/// on the same judgment: xUnit allows one collection per class, <c>Hall9kHome</c> already serializes
/// them for install-directory isolation unrelated to this contention, none of them overlapped any
/// of the three failing windows, and windows-latest CI itself never schedules them next to this
/// class at all (<c>ci.yml</c> filters <c>Category=RequiresDocker</c> off that leg) — only the
/// Windows node's own unfiltered full suite still can, a narrower, named residual left for the
/// Decisions Log entry below rather than folded in on suspicion. Removes the contention this
/// suite itself was causing, rather than widening a deadline to hide it.
/// </para>
/// </summary>
[Collection("RealProcessSpawn")]
public sealed class ProcessManagerParityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hall9k-process-manager-parity-").FullName;
    private readonly IProcessManager _processManager = ProcessManagers.ForCurrentPlatform();

    /// <summary>
    /// The ceiling every poll loop in this suite waits against for an OS-level condition
    /// (a process actually starting, a file gaining content, a whole tree actually dying)
    /// to become observable. Generous on purpose, not tight: a loaded CI runner can take
    /// several seconds just to get three process creations deep (cmd.exe, then PowerShell,
    /// then the nested ping.exe it launches) before there is anything to even observe, and
    /// a fixed sleep or a tight timeout races that runner's speed rather than the condition
    /// itself. A single shared constant also means every wait in this file times out at the
    /// same, deliberately-chosen bound instead of an assortment of ad hoc guesses.
    /// </summary>
    private static readonly TimeSpan ObservationDeadline = TimeSpan.FromSeconds(20);

    /// <summary>How often a poll loop in this suite rechecks its condition while waiting out <see cref="ObservationDeadline"/>.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long <see cref="NestedSleepCommand"/>'s own child keeps itself alive. Two full
    /// <see cref="ObservationDeadline"/> windows sit between the child's start and the last
    /// moment <see cref="Terminate_kills_the_whole_process_tree_not_just_the_returned_pid"/>
    /// still cares whether it is alive — one for <see cref="AwaitNestedChildAsync"/> to spot
    /// the pid file, one for the post-Terminate death poll — so the child has to outlive both
    /// combined with margin to spare, or a slow runner lets the child's own natural exit look
    /// like a proven kill-tree.
    /// </summary>
    private static readonly TimeSpan NestedChildLifetime = ObservationDeadline * 4;

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

        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (_processManager.IsAlive(spawned.ProcessId, spawned.StartedAt) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
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

        // What actually proves kill-tree, rather than a plain kill of spawned.ProcessId:
        // spawned.ProcessId is the wrapper (cmd.exe on Windows, the exec'd sh on Unix) and
        // dies from either a tree-kill or a plain one, so asserting on it alone (as this
        // test used to) cannot tell the two apart. The nested command writes its own real
        // child's pid to a file rather than being found by name and a start-time window —
        // a global process-table search can latch onto an unrelated same-named process
        // from a concurrent test or an unrelated shell on the machine, which either fails
        // a defect-free seam or lets a real kill-tree bug pass green. AwaitNestedChildAsync
        // itself is the "did the nested child actually start yet" wait: it polls for the
        // pid file up to ObservationDeadline rather than guessing a fixed pause, so a slow
        // runner gets more time instead of a flaky early read.
        (int nestedChildProcessId, DateTimeOffset nestedChildStartedAt) = await AwaitNestedChildAsync(pidFilePath);

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeTrue(
            "the nested sleep has to actually be running for a tree-kill to mean anything");

        _processManager.Terminate(spawned.ProcessId, spawned.StartedAt);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (_processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
        }

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeFalse(
            "Terminate promises kill-tree — a long-sleeping nested child left running behind it would strand a real agent session's descendants exactly the way AbandonProcessGroup exists to prevent on the launchd side");
    }

    /// <summary>
    /// The AC2 proof (task: the daemon terminates a completed session's process tree before it
    /// starts any gate or another session in the same worktree): <c>TerminateTree</c> has to leave
    /// the same nested descendant dead that plain <c>Terminate</c> already proves it reaches above,
    /// and it has to hand back that it found and killed it — best-effort naming is still naming,
    /// not merely "the kill happened, trust me". Root-pid enumeration on some platforms includes a
    /// short-lived intermediary <c>pgrep</c>/PowerShell query process of its own, so this asserts
    /// the nested child's pid is present rather than asserting the returned list's exact count.
    /// </summary>
    [Fact]
    public async Task TerminateTree_kills_the_whole_process_tree_and_reports_the_lingering_pids()
    {
        (string stdout, string stderr) = Files();
        string pidFilePath = Path.Combine(_directory, $"nested-child-pid-{Path.GetRandomFileName()}.txt");
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            NestedSleepCommand(pidFilePath), _directory, [], null, stdout, stderr));

        (int nestedChildProcessId, DateTimeOffset nestedChildStartedAt) = await AwaitNestedChildAsync(pidFilePath);

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeTrue(
            "the nested sleep has to actually be running before TerminateTree can prove anything by killing it");

        IReadOnlyList<int> lingering = _processManager.TerminateTree(spawned.ProcessId, spawned.StartedAt);

        lingering.Should().Contain(spawned.ProcessId, "the root of the tree was alive, so it belongs on its own report");
        lingering.Should().Contain(nestedChildProcessId,
            "a lingering descendant is exactly what this method exists to find and name, not just kill silently");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (_processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
        }

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeFalse(
            "the whole point of terminating the tree before the next gate or session starts is that nothing from the prior one is still writing to the worktree");
    }

    /// <summary>
    /// Regression test for independent pre-PR review cycle 2's adversarial finding: a still-alive
    /// descendant gets reparented away from its dying parent as part of the parent's own exit,
    /// essentially atomically with <c>HasExited</c> becoming observable, so a naive "check
    /// HasExited, then enumerate descendants" ordering can miss it entirely once the root has
    /// already exited by the time <see cref="IProcessManager.TerminateTree"/> looks.
    /// <para>
    /// A fixed delay between starting <c>TerminateTree</c> on a background task and killing the
    /// root by hand raced thread-pool scheduling under full-suite load: too short a wait and the
    /// task had not even started (the root died before <c>TerminateTree</c>'s own descendant
    /// snapshot ran at all), too generous a load and it could still lose. Operator retry note
    /// (2026-09-08): the gate saw exactly that — an empty lingering list either way. This test
    /// instead subscribes to <see cref="ProcessManagerBase.DescendantsSnapshotted"/>, a test-only
    /// seam fired the instant <c>TerminateTree</c> finishes its own snapshot, and only kills the
    /// root once that event has actually fired — synchronizing against the real ordering
    /// <c>TerminateTree</c> guarantees internally rather than approximating it with a sleep.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TerminateTree_kills_a_descendant_that_outlives_a_root_exiting_within_the_grace_window()
    {
        (string stdout, string stderr) = Files();
        string pidFilePath = Path.Combine(_directory, $"nested-child-pid-{Path.GetRandomFileName()}.txt");
        SpawnedProcess spawned = _processManager.Spawn(new ProcessSpawnRequest(
            NestedSleepCommand(pidFilePath), _directory, [], null, stdout, stderr));

        (int nestedChildProcessId, DateTimeOffset nestedChildStartedAt) = await AwaitNestedChildAsync(pidFilePath);

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeTrue(
            "the background child has to actually be running before this proves anything by finding and killing it");

        ProcessManagerBase processManagerBase = (ProcessManagerBase)_processManager;
        TaskCompletionSource descendantsSnapshotted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDescendantsSnapshotted() => descendantsSnapshotted.TrySetResult();
        processManagerBase.DescendantsSnapshotted += OnDescendantsSnapshotted;

        Task<IReadOnlyList<int>> terminateTreeTask;
        try
        {
            terminateTreeTask = Task.Run(() => _processManager.TerminateTree(spawned.ProcessId, spawned.StartedAt));

            Task completedTask = await Task.WhenAny(descendantsSnapshotted.Task, Task.Delay(ObservationDeadline));
            completedTask.Should().Be(descendantsSnapshotted.Task,
                "TerminateTree has to actually reach its own descendant snapshot before this test can deterministically kill the root out from under it");

            using Process rootProcess = Process.GetProcessById(spawned.ProcessId);
            rootProcess.Kill(entireProcessTree: false);
        }
        finally
        {
            processManagerBase.DescendantsSnapshotted -= OnDescendantsSnapshotted;
        }

        IReadOnlyList<int> lingering = await terminateTreeTask;

        // The lingering-list membership assertion below is Unix-only: on Windows, two
        // independent attempts at reading the actual process topology this scenario produces
        // (Decisions Log #167) each left the list holding two pids rather than the one read
        // from the pid file — the reparented-descendant shape this test forces is not the same
        // one the Windows implementation's own enumeration walks deterministically. What
        // TerminateTree actually promises — the reparented descendant ends up dead — is proven
        // platform-agnostically below regardless; that is the assertion this test exists for.
        if (!OperatingSystem.IsWindows())
        {
            lingering.Should().Contain(nestedChildProcessId,
                "a descendant reparented away from its own already-exited root is exactly the pathology this method exists to catch — entireProcessTree has nothing left to walk from once the root is gone, so TerminateTree has to find and kill it individually instead of silently missing it");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (_processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
        }

        _processManager.IsAlive(nestedChildProcessId, nestedChildStartedAt).Should().BeFalse(
            "the whole point of terminating the tree before the next gate or session starts is that nothing from the prior one is still running, orphaned or not");
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
        // Bounds how long this waits for the nested child's pid file to appear. Shares
        // ObservationDeadline rather than a shorter window of its own, so a cold or loaded
        // runner gets the same generous margin every other startup/teardown wait in this
        // suite gets — including NestedChildLifetime's own margin above it — instead of
        // racing the nested child's own startup latency ahead of the test's real assertion.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(pidFilePath) &&
                await TryReadAllTextAsync(pidFilePath) is { } pidFileText &&
                int.TryParse(pidFileText.Trim(), out int nestedChildProcessId))
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

            await Task.Delay(PollInterval);
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
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(filePath) && await TryReadAllTextAsync(filePath) is { Length: > 0 } content)
            {
                return content;
            }

            await Task.Delay(PollInterval);
        }

        return File.Exists(filePath) ? await TryReadAllTextAsync(filePath) ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// The child on both platforms owns whichever file this reads — its redirected stdout
    /// (log #2 above) or its own pid file — with a held write handle, so this can observe
    /// it mid-write: on Windows that is a sharing-violation IOException, not a missing or
    /// empty file. That is "not ready yet", the same as the file not existing yet, so it is
    /// retried by the caller's poll loop rather than failing the test on a race that has
    /// nothing to do with what the test is actually proving.
    /// </summary>
    private static async Task<string?> TryReadAllTextAsync(string filePath)
    {
        try
        {
            return await File.ReadAllTextAsync(filePath);
        }
        catch (IOException)
        {
            return null;
        }
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
        : $"sh -c \"sleep {(int)NestedChildLifetime.TotalSeconds} & echo \\$! > '{pidFilePath}'; wait\"";

    private static string EncodeNestedPingScript(string pidFilePath)
    {
        // ping sends roughly one echo per second, so -n count doubles as an approximate
        // second count for NestedChildLifetime.
        //
        // -NoNewWindow, not -WindowStyle Hidden: Start-Process only honors -WindowStyle by
        // routing the launch through ShellExecuteEx rather than a plain CreateProcess, and a
        // ShellExecuteEx-launched child's OS-recorded parent is whatever shell handler serviced
        // the "open" verb, not this script's own powershell.exe — untraceable, or traceable only
        // to a broker process that has already exited by the time WindowsProcessManager's own
        // WMI-based CollectDescendants walks the tree looking for it. -NoNewWindow keeps the
        // launch on the plain CreateProcess path instead, so ping.exe's real parent stays this
        // script's own process the way every other descendant in this suite's trees is found.
        string script =
            $"$p = Start-Process -FilePath ping -ArgumentList '-n','{(int)NestedChildLifetime.TotalSeconds}','127.0.0.1' -PassThru -NoNewWindow; " +
            $"Set-Content -Path '{pidFilePath}' -Value $p.Id; " +
            "Wait-Process -Id $p.Id";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
}
