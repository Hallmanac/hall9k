using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class WindowsAppendOnlyLogTests : IDisposable
{
    private readonly string logFile = Path.Combine(
        Path.GetTempPath(), $"h9k-append-only-{Path.GetRandomFileName()}.log");

    public void Dispose()
    {
        try
        {
            File.Delete(logFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Teardown, not an assertion. Every test here deliberately puts a holder on the
            // log, and a delete needs DELETE access that a holder's share mode may refuse, so
            // an unlinkable temp file is worth leaving in %TEMP% rather than failing a test
            // whose own checks already passed. The waits below are what keep that from being
            // the normal outcome.
        }
    }

    [Fact]
    public void A_writer_survives_a_truncate_from_another_handle()
    {
        // The case WindowsAppendOnlyLog exists for: DaemonLogRotation truncates the log
        // through a second handle while this one keeps writing. A plain cmd.exe >> handle
        // does not survive that (its write position is cached at open time, so the next
        // write after a truncate lands at the old offset and Windows zero-fills the gap) —
        // this proves the FILE_APPEND_DATA-only handle re-resolves end-of-file instead, the
        // same guarantee DaemonLogRotationTests proves for a real O_APPEND descriptor on Unix.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (StreamWriter writer = WindowsAppendOnlyLog.OpenAppendWriter(logFile))
        {
            writer.WriteLine("before the truncate");
            writer.Flush();

            using (FileStream truncator = new(
                logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                truncator.SetLength(0);
            }

            writer.WriteLine("after the truncate");
            writer.Flush();
        }

        // File.ReadAllText opens with the default FileShare.Read, which conflicts with
        // the FILE_APPEND_DATA handle's write-type access unless that handle has already
        // closed — the assertion only needs the bytes on disk, not the live handle.
        File.ReadAllText(logFile).Should().Be("after the truncate\r\n");
    }

    [Fact]
    public void A_writer_appends_to_existing_content_without_inserting_a_byte_order_mark()
    {
        // Encoding.UTF8 carries a BOM preamble that StreamWriter only suppresses when the
        // stream already reports Position > 0 — never true for a freshly opened
        // FILE_APPEND_DATA handle even though it writes at end-of-file, so a naive
        // Encoding.UTF8 writer stamps EF BB BF into the middle of an already-populated log
        // on every daemon start. A_writer_survives_a_truncate_from_another_handle above
        // writes before any truncation happens, so the preamble flag is already set by the
        // time its asserted line lands — only a writer opened against pre-existing content
        // exercises the bug this test guards against.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(logFile, "existing content\r\n");

        using (StreamWriter writer = WindowsAppendOnlyLog.OpenAppendWriter(logFile))
        {
            writer.WriteLine("appended");
        }

        byte[] expected = System.Text.Encoding.UTF8.GetBytes("existing content\r\nappended\r\n");
        File.ReadAllBytes(logFile).Should().Equal(expected);
    }

    [Fact]
    public void The_open_mode_admits_a_reader_following_the_log_while_the_daemon_writes_it()
    {
        // The share mode is a contract with the operator's window, not an implementation
        // detail: the orchestrator recipe's own start-up step arms a Get-Content -Wait
        // reader on h9kd.log and leaves it running for the session's whole life. Narrow
        // this open's FILE_SHARE_* mask and that reader is the one Windows refuses, so the
        // recipe would have to change. It does not, and this is why.
        //
        // There is no POSIX half of this test because there is nothing to assert there: the
        // Unix sink is the launching shell's own O_APPEND redirect (DaemonLifecycle's
        // /bin/sh intermediary, or launchd's StandardOutPath), no mandatory sharing exists
        // to refuse a reader, and DaemonLogRotationTests already covers that descriptor's
        // append semantics.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using StreamWriter writer = WindowsAppendOnlyLog.OpenAppendWriter(logFile);
        writer.WriteLine("a line the operator's window should see");

        using FileStream reader = new(
            logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader text = new(reader);
        text.ReadToEnd().Should().Contain("a line the operator's window should see");
    }

    [Fact]
    public void A_transient_sharing_violation_is_retried_rather_than_fallen_back_from_at_once()
    {
        // Criterion the two mailbox issue #1 reports asked for: a holder that lets go is
        // waited out instead of costing the daemon its rotation-safe handle for the rest of
        // its run. The wait is driven through the injected sleep rather than the wall clock,
        // so this proves the retry deterministically — the holder is released from inside
        // the second wait, which can only happen if the open really did fail, retry, fail
        // again, and try a third time.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FileStream? exclusive = new(logFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<TimeSpan> waits = [];
        try
        {
            using (StreamWriter writer = WindowsAppendOnlyLog.OpenAppendWriter(
                logFile,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100),
                wait =>
                {
                    waits.Add(wait);
                    if (waits.Count == 2)
                    {
                        exclusive?.Dispose();
                        exclusive = null;
                    }
                }))
            {
                writer.WriteLine("after the holder let go");
            }
        }
        finally
        {
            exclusive?.Dispose();
        }

        waits.Should().HaveCount(2).And.AllBeEquivalentTo(TimeSpan.FromMilliseconds(100));
        File.ReadAllText(logFile).Should().Be("after the holder let go\r\n");
    }

    [Fact]
    public void A_sharing_violation_that_never_clears_names_the_holder_it_lost_to()
    {
        // The other half of the same criterion: when the retry budget really does run out,
        // the fallback has to say who won rather than leave a reader with Win32 error 32 and
        // no lead. Both reports on mailbox issue #1 had only the number, and both reasoned
        // their way to the wrong holder from it. Here the holder is this test process, so the
        // pid in the message is one the assertion can name exactly.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using FileStream exclusive = new(logFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<TimeSpan> waits = [];

        Action open = () => WindowsAppendOnlyLog.OpenAppendWriter(
            logFile, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100), waits.Add);

        open.Should().Throw<IOException>()
            .Which.Message.Should()
                .Contain("Win32 error 32")
                .And.Contain("after retrying for 0.3s")
                .And.Contain($"pid {Environment.ProcessId}");

        // 300ms budget over 100ms waits is four attempts, so three waits between them.
        waits.Should().HaveCount(3);
    }

    [Fact]
    public void A_failure_that_is_not_a_sharing_violation_is_not_retried_at_all()
    {
        // Retrying a denied ACL or a missing directory buys nothing and costs the whole
        // budget before the daemon logs its first line, so only errors 32 and 33 wait.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string unreachable = Path.Combine(logFile, "no-such-directory", "h9kd.log");
        List<TimeSpan> waits = [];

        Action open = () => WindowsAppendOnlyLog.OpenAppendWriter(
            unreachable, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), waits.Add);

        open.Should().Throw<IOException>().Which.Message.Should().NotContain("Win32 error 32");
        waits.Should().BeEmpty();
    }

    [Fact]
    public void The_launcher_handoff_shape_refuses_the_open_and_still_admits_a_reader()
    {
        // The handoff shape itself, run for real rather than described: both shipped Windows
        // launch paths start h9kd as cmd.exe /c "h9kd < NUL >> h9kd.log 2>&1", and cmd.exe
        // opens an append redirect's target with FILE_SHARE_READ only and holds it for the
        // whole run. So this is what a daemon's own takeover attempt is actually up against,
        // and the two facts this asserts are the two the mailbox reports each got half of:
        // the open is refused no matter how long it retries (the holder is the daemon's own
        // launcher, which outlives it), and a reader following the log is entirely unaffected
        // — the node's orchestrator window was never blinded by this.
        //
        // This test runs on the Windows CI leg, which is the leg that matters: it is a
        // cmd.exe fact, and the ubuntu leg has no cmd.exe and no mandatory sharing to
        // reproduce it with. The one piece no test in this file can reach is the short
        // circuit for that same holder inside OpenAppendWriter, which keys on this process's
        // own stdout already being the log: pointing a test process's real standard handles
        // at a file mid-run would corrupt every other test's output in the same collection,
        // so its path comparison is covered directly through DescribesSameFile below instead.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string marker = "h9k-handoff-marker";
        string inner = $"(echo {marker}& ping -n 60 127.0.0.1) >> \"{logFile}\" 2>&1";
        ProcessStartInfo launcher = new()
        {
            FileName = "cmd.exe",
            Arguments = WindowsCommandLine.WrapForCmdExe(inner),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process? holder = Process.Start(launcher);
        holder.Should().NotBeNull();
        try
        {
            // Waiting on the marker rather than on a clock: once a reader has read it
            // through cmd.exe's own live redirect, the redirect is provably open — and that
            // read is itself the reader half of the assertion.
            ReadUntilMarkerVisible(marker);

            Action open = () => WindowsAppendOnlyLog.OpenAppendWriter(logFile);
            open.Should().Throw<IOException>()
                .Which.Message.Should()
                    .Contain("Win32 error 32")
                    // The image name, not the Restart Manager's own FileDescription prose
                    // ("Windows Command Processor"), which is localized and would make this
                    // assertion a statement about the runner's display language.
                    .And.Contain("cmd (pid");
        }
        finally
        {
            holder!.Kill(entireProcessTree: true);
            // Waited on, not fired and forgotten: Dispose deletes the log, and cmd.exe's
            // redirect handle carries no FILE_SHARE_DELETE, so a delete racing a not-quite-dead
            // holder cannot land — and cmd.exe is not the only holder, which is why waiting on
            // it alone is not enough. The `ping` inside the redirected block inherited that same
            // handle when cmd.exe created it, Kill(entireProcessTree) issues TerminateProcess
            // across the tree and returns without waiting on any of it, and each process's
            // handle table is torn down on its own schedule. So wait on the condition that
            // actually matters rather than on one of the two processes: that no handle onto the
            // log is left at all. Dispose tolerates a delete that still cannot land, so this
            // wait is what keeps the log from merely leaking into %TEMP% on every run.
            holder.WaitForExit();
            WaitUntilNothingHoldsTheLog();
        }
    }

    [Theory]
    // GetFinalPathNameByHandle always answers in extended-length form; DaemonRuntime.LogFile
    // never holds one, so a comparison that skipped the prefix would never match and the
    // short circuit would silently never fire.
    [InlineData(@"\\?\C:\Users\someone\.hall9k\h9kd.log", @"C:\Users\someone\.hall9k\h9kd.log", true)]
    [InlineData(@"\\?\UNC\fileserver\logs\h9kd.log", @"\\fileserver\logs\h9kd.log", true)]
    // Windows paths are case-insensitive, and the two sides come from different places: one
    // from the kernel, one from a config file or a default composed at startup.
    [InlineData(@"\\?\C:\Users\someone\.hall9k\H9KD.LOG", @"C:\Users\someone\.hall9k\h9kd.log", true)]
    // A different file in the same directory is the case that must not short-circuit: it
    // would report a structurally permanent holder where the real one may well let go.
    [InlineData(@"\\?\C:\Users\someone\.hall9k\h9kd.log.1", @"C:\Users\someone\.hall9k\h9kd.log", false)]
    [InlineData(@"\\?\C:\Users\someone\.hall9k\h9kd.log", @"C:\Users\someone\.hall9k\other.log", false)]
    public void An_extended_length_handle_path_is_matched_against_the_configured_log_path(
        string resolvedHandlePath, string logFilePath, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsAppendOnlyLog.DescribesSameFile(resolvedHandlePath, logFilePath).Should().Be(expected);
    }

    [Fact]
    public void The_holder_lookup_names_this_process_when_this_process_is_the_holder()
    {
        // WindowsFileLockHolders is the only thing standing between a reader and a bare
        // error number, so it gets its own coverage against a holder whose identity the test
        // already knows. Restart Manager marshalling is the risk here: a struct laid out
        // wrong reads a garbage pid rather than failing loudly.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using FileStream exclusive = new(logFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        WindowsFileLockHolders.Describe(logFile).Should().Contain($"pid {Environment.ProcessId}");
    }

    /// <summary>
    /// Blocks until an exclusive open of the log succeeds, which is true only once every handle
    /// onto it is gone — the launcher's own, and any child that inherited the launcher's. The
    /// deadline is generous and falling through it is not a failure: <see cref="Dispose"/>
    /// tolerates a file it cannot unlink, so the worst a loaded runner costs here is a stray
    /// temp file rather than a hung suite or a red test.
    /// </summary>
    private void WaitUntilNothingHoldsTheLog()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!File.Exists(logFile))
            {
                return;
            }

            try
            {
                using FileStream exclusive = new(logFile, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void ReadUntilMarkerVisible(string marker)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(logFile))
            {
                using FileStream reader = new(
                    logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader text = new(reader);
                if (text.ReadToEnd().Contains(marker, StringComparison.Ordinal))
                {
                    return;
                }
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"cmd.exe never wrote {marker} into {logFile} — its >> redirect was never observed open, "
            + "so this test cannot say anything about what happens while it is.");
    }
}
