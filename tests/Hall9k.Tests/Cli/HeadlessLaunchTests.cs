using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="HeadlessLaunch"/> against a real, harmless stand-in for <c>claude</c> (a shell
/// script pointed at through <c>HALL9K_CLAUDE_PATH</c>, exactly as h9k task work's own
/// <c>ClaudeBinary()</c> resolves it), since a real launch is otherwise unobservable from a test
/// with no Claude Code install to hand. Unix-only: <see cref="HeadlessLaunch"/> dispatches to a
/// different, cmd.exe-based implementation on Windows this repository's CI machine cannot exercise
/// (task 8a56af78-h9k's own handoff names this as a known, accepted gap — the Windows path is
/// composed from two already-proven precedents, <c>WindowsProcessManager</c> and
/// <c>DaemonLifecycle.SpawnDetachedWindows</c>, rather than independently verified here).
/// </summary>
[Collection("Hall9kHome")]
public sealed class HeadlessLaunchTests : IDisposable
{
    private readonly string _scratchDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-headless-launch-{Guid.NewGuid():N}");
    private readonly string? _previousClaudePath = Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH");

    public HeadlessLaunchTests() => Directory.CreateDirectory(_scratchDirectory);

    [Fact]
    public async Task A_real_spawn_reads_the_prompt_file_and_writes_the_stream_file_under_the_process_it_reports()
    {
        // HeadlessLaunch's Windows path is not exercised here — see the class doc.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string promptFile = Path.Combine(_scratchDirectory, "prompt.md");
        string streamFile = Path.Combine(_scratchDirectory, "stream.jsonl");
        string standardErrorFile = Path.Combine(_scratchDirectory, "stderr.log");
        string settingsFile = Path.Combine(_scratchDirectory, "settings.json");
        await File.WriteAllTextAsync(promptFile, "the actual prompt content");
        await File.WriteAllTextAsync(settingsFile, "{}");

        // The stand-in for `claude -p ...`: a script that ignores every flag HeadlessLaunch
        // passes it (a bare `cat` would instead try to open "-p", "--model", etc. as filenames —
        // caught only by actually running this, not by proofreading it), cats stdin to stdout so
        // the stream file's content proves the real redirection wiring works, then sleeps briefly
        // — found only by actually running this (self-review, task 8a56af78-h9k): a `cat` alone
        // finishes reading a short prompt file faster than HeadlessLaunch's own settle window,
        // making a perfectly successful launch indistinguishable from one that never started.
        string fakeClaude = Path.Combine(_scratchDirectory, "fake-claude.sh");
        await File.WriteAllTextAsync(fakeClaude, "#!/bin/sh\ncat\nsleep 2\n");
        MakeExecutable(fakeClaude);
        Environment.SetEnvironmentVariable("HALL9K_CLAUDE_PATH", fakeClaude);

        (int processId, DateTimeOffset startedAt) = HeadlessLaunch.SpawnDetached(
            _scratchDirectory, Guid.NewGuid(), "test-headless-build", AgentModel.Sonnet, promptFile, streamFile,
            standardErrorFile, settingsFile, skipPermissions: false);

        processId.Should().BePositive();
        startedAt.Should().NotBe(DateTimeOffset.MinValue, "the process really started, so its start time is observed, not the never-started sentinel");

        // cat finishes almost instantly once it hits EOF on the redirected prompt file — this
        // polls rather than sleeping a fixed amount, since CI machines vary in how fast a freshly
        // spawned process is scheduled.
        string streamContent = await PollUntilNotEmptyAsync(streamFile, TimeSpan.FromSeconds(5));
        streamContent.Should().Be("the actual prompt content");

        // The reported pid is a real process identity (log #2): stale after cat exits, exactly as
        // an ordinary headless dispatch's own pid goes stale once its session ends.
        Func<bool> stillAlive = () =>
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        };
        await PollUntilAsync(() => !stillAlive(), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The failure mode found by actually running this procedure rather than proofreading it
    /// (self-review, task 8a56af78-h9k): a missing binary does not fail the detach wrapper's own
    /// exit code — the "command not found" surfaces asynchronously, inside the backgrounded job,
    /// so <see cref="HeadlessLaunch"/> has to notice the captured pid is already gone rather than
    /// trusting a clean wrapper exit.
    /// </summary>
    [Fact]
    public async Task A_missing_claude_binary_fails_clearly_instead_of_reporting_a_dead_pid_as_alive()
    {
        // HeadlessLaunch's Windows path is not exercised here — see the class doc.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string promptFile = Path.Combine(_scratchDirectory, "prompt.md");
        string streamFile = Path.Combine(_scratchDirectory, "stream.jsonl");
        string standardErrorFile = Path.Combine(_scratchDirectory, "stderr.log");
        string settingsFile = Path.Combine(_scratchDirectory, "settings.json");
        await File.WriteAllTextAsync(promptFile, "prompt");
        await File.WriteAllTextAsync(settingsFile, "{}");

        Environment.SetEnvironmentVariable("HALL9K_CLAUDE_PATH", "hall9k-test-binary-that-does-not-exist-xyz");

        Action act = () => HeadlessLaunch.SpawnDetached(
            _scratchDirectory, Guid.NewGuid(), "test-headless-build", AgentModel.Sonnet, promptFile, streamFile,
            standardErrorFile, settingsFile, skipPermissions: false);

        act.Should().Throw<InvalidOperationException>()
            .Where(exception => exception.Message.Contains("already exited")
                && exception.Message.Contains("claude binary could not be started"));
    }

    private static void MakeExecutable(string path)
    {
        using Process chmod = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "chmod",
                ArgumentList = { "+x", path },
                UseShellExecute = false,
            },
        };
        chmod.Start();
        chmod.WaitForExit();
    }

    private static async Task<string> PollUntilNotEmptyAsync(string path, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                string content = await File.ReadAllTextAsync(path);
                if (content.Length > 0)
                {
                    return content;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{path} was never written within {timeout}.");
    }

    private static async Task PollUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Condition never became true within {timeout}.");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_CLAUDE_PATH", _previousClaudePath);
        try
        {
            Directory.Delete(_scratchDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
