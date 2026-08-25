using System.Diagnostics;
using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class DaemonLogRotationTests : IDisposable
{
    private const long Threshold = 8;

    private readonly string logFile = Path.Combine(
        Path.GetTempPath(), $"h9kd-rotation-{Path.GetRandomFileName()}.log");

    public void Dispose()
    {
        File.Delete(logFile);
        File.Delete(DaemonLogRotation.PreviousLogFile(logFile));
    }

    [Fact]
    public void A_missing_log_is_not_rotated()
    {
        DaemonLogRotation.RotateIfOversized(logFile, Threshold).Should().BeFalse();
        File.Exists(DaemonLogRotation.PreviousLogFile(logFile)).Should().BeFalse();
    }

    [Fact]
    public void A_log_within_budget_is_left_alone()
    {
        File.WriteAllText(logFile, "small\n");

        DaemonLogRotation.RotateIfOversized(logFile, Threshold).Should().BeFalse();

        File.ReadAllText(logFile).Should().Be("small\n");
        File.Exists(DaemonLogRotation.PreviousLogFile(logFile)).Should().BeFalse();
    }

    [Fact]
    public void An_oversized_log_is_copied_aside_keeping_one_generation_and_truncated_in_place()
    {
        File.WriteAllText(DaemonLogRotation.PreviousLogFile(logFile), "the generation before that\n");
        string contents = new string('x', (int)Threshold) + "\n";
        File.WriteAllText(logFile, contents);

        DaemonLogRotation.RotateIfOversized(logFile, Threshold).Should().BeTrue();

        // Truncated in place, not renamed away: the file the daemon's descriptor points
        // at is still there, and it is empty.
        File.Exists(logFile).Should().BeTrue();
        new FileInfo(logFile).Length.Should().Be(0);
        File.ReadAllText(DaemonLogRotation.PreviousLogFile(logFile)).Should().Be(contents);
    }

    [Fact]
    public void A_writer_holding_the_log_open_keeps_writing_into_the_truncated_file()
    {
        // The case rotation exists for. /bin/sh with >> is exactly the daemon's own
        // arrangement (DaemonLifecycle.SpawnDetached, and launchd's StandardOutPath):
        // one O_APPEND descriptor held open for the process's whole lifetime. A rename
        // would leave that descriptor on the rolled-aside generation and every later
        // line would vanish from the log; truncating the same file keeps it landing.
        if (OperatingSystem.IsWindows())
        {
            // Models a real O_APPEND descriptor (/bin/sh's own >>), which is the Unix
            // side of the story. Windows has no shell equivalent to model here — a plain
            // cmd.exe >> handle does NOT re-resolve end-of-file per write, which is
            // exactly why h9kd never relies on one; see WindowsAppendOnlyLogTests for the
            // Windows-side coverage of the handle it uses instead.
            return;
        }

        string trigger = logFile + ".rotated";
        using Process writer = StartAppendingWriter(trigger);
        try
        {
            WaitUntil(() => File.Exists(logFile) && new FileInfo(logFile).Length > Threshold);

            DaemonLogRotation.RotateIfOversized(logFile, Threshold).Should().BeTrue();

            File.WriteAllText(trigger, string.Empty);
            writer.WaitForExit(milliseconds: 10_000).Should().BeTrue();

            File.ReadAllText(logFile).Should().Be("after the roll\n");
            File.ReadAllText(DaemonLogRotation.PreviousLogFile(logFile)).Should().Be("before the roll\n");
        }
        finally
        {
            File.Delete(trigger);
        }
    }

    [Fact]
    public void A_second_rotation_of_an_already_emptied_log_is_a_no_op()
    {
        File.WriteAllText(logFile, new string('x', (int)Threshold + 1));

        DaemonLogRotation.RotateIfOversized(logFile, Threshold).Should().BeTrue();
        DaemonLogRotation.RotateIfOversized(logFile, Threshold).Should().BeFalse();

        // The generation kept is the one that was actually oversized — a losing racer
        // must not overwrite it with the emptied log.
        new FileInfo(DaemonLogRotation.PreviousLogFile(logFile)).Length.Should().Be(Threshold + 1);
    }

    /// <summary>
    /// A stand-in for the daemon: stdout redirected onto the log with &gt;&gt; for the
    /// process's lifetime, writing one line before the rotation and one after it.
    /// </summary>
    private Process StartAppendingWriter(string triggerFile)
    {
        ProcessStartInfo shell = new() { FileName = "/bin/sh", UseShellExecute = false };
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add(
            "exec >>\"$1\"; echo 'before the roll'; while [ ! -f \"$2\" ]; do sleep 0.02; done; echo 'after the roll'");
        shell.ArgumentList.Add("sh");
        shell.ArgumentList.Add(logFile);
        shell.ArgumentList.Add(triggerFile);

        Process? writer = Process.Start(shell);
        writer.Should().NotBeNull();
        return writer;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        condition().Should().BeTrue("the writer should have written its first line");
    }
}
