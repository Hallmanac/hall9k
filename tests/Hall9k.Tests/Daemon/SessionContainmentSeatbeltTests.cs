using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The real seatbelt, on a Mac host only: every other platform returns at once, and CI has no macOS
/// leg, so the evidence for this class is the Mac host gate's own run. It builds its command through
/// <see cref="SessionContainment.Apply"/>, the same constant and composition <see cref="ClaudeExecutor"/>
/// uses, and spawns it through the real process manager. The only processes it creates besides the
/// sandboxed script are <c>sleep</c> processes, and each is ended by its own pid; nothing here runs
/// pkill, killall, <c>kill -1</c>, or a pattern or glob in a kill or rm.
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class SessionContainmentSeatbeltTests : IDisposable
{
    private static readonly TimeSpan ObservationDeadline = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("hall9k-containment-seatbelt-").FullName;
    private readonly List<int> _sleepProcessIds = [];

    public void Dispose()
    {
        foreach (int processId in _sleepProcessIds)
        {
            EndSleep(processId);
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_contained_session_can_signal_what_it_started_but_not_what_it_did_not()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using Process outsideSleep = Process.Start(new ProcessStartInfo("/bin/sleep", "600") { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start the outside sleep.");
        _sleepProcessIds.Add(outsideSleep.Id);

        string script = Path.Combine(_directory, "session.sh");
        await File.WriteAllTextAsync(script, $$"""
            echo $$ > "{{_directory}}/session.pid"
            ( sleep 600 & echo $! > "{{_directory}}/detached.pid" )
            detached=$(cat "{{_directory}}/detached.pid")
            kill -TERM "$detached" 2> "{{_directory}}/kill-detached.err"
            echo $? > "{{_directory}}/kill-detached.status"
            kill -TERM {{outsideSleep.Id}} 2> "{{_directory}}/kill-outside.err"
            echo $? > "{{_directory}}/kill-outside.status"
            /bin/ps -p $$ > "{{_directory}}/ps.out" 2> "{{_directory}}/ps.err"
            echo $? > "{{_directory}}/ps.status"
            echo done > "{{_directory}}/done"
            """);

        string command = SessionContainment.ForCurrentPlatform().Apply($"/bin/sh \"{script}\"", enabled: true);
        command.Should().Contain("sandbox-exec", "this test is only meaningful if the executor's own composition wrapped it");

        SpawnedProcess spawned = ProcessManagers.ForCurrentPlatform().Spawn(new ProcessSpawnRequest(
            command, _directory, [], null, Path.Combine(_directory, "stdout"), Path.Combine(_directory, "stderr")));

        await WaitForFileAsync("done");
        if (File.Exists(Path.Combine(_directory, "detached.pid")))
        {
            _sleepProcessIds.Add(int.Parse((await File.ReadAllTextAsync(Path.Combine(_directory, "detached.pid"))).Trim()));
        }

        Read("session.pid").Should().Be(spawned.ProcessId.ToString(), "the exec chain keeps the recorded pid the session's own");
        Read("kill-detached.status").Should().Be("0", "a process the session detached with ( sleep & ) is still its own to signal");
        Read("kill-outside.status").Should().NotBe("0");
        Read("kill-outside.err").Should().Contain("Operation not permitted");
        outsideSleep.HasExited.Should().BeFalse("the signal to a process outside the sandbox must not have been delivered");
        Read("ps.status").Should().Be("0", "/bin/ps is the one exec allowed outside the profile");
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(_directory, name)).Trim();

    private async Task WaitForFileAsync(string name)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ObservationDeadline;
        while (!File.Exists(Path.Combine(_directory, name)))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The contained script never wrote {name}. stderr: {await ReadIfPresentAsync("stderr")}");
            }

            await Task.Delay(50);
        }
    }

    private async Task<string> ReadIfPresentAsync(string name) =>
        File.Exists(Path.Combine(_directory, name)) ? await File.ReadAllTextAsync(Path.Combine(_directory, name)) : string.Empty;

    /// <summary>Ends one sleep this test started, by its own pid, and only if it is still a sleep.</summary>
    private static void EndSleep(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited && process.ProcessName == "sleep")
            {
                process.Kill();
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
        }
    }
}
