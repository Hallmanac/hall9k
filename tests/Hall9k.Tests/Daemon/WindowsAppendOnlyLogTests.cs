using System.Text;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
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
            // The rotation tests roll a previous generation aside; nothing else creates one, and
            // File.Delete on a path that was never created is a no-op.
            File.Delete(DaemonLogRotation.PreviousLogFile(logFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Teardown, not an assertion. Several tests here deliberately put a holder on the
            // log, and a delete needs DELETE access that a holder's share mode may refuse, so
            // an unlinkable temp file is worth leaving in %TEMP% rather than failing a test
            // whose own checks already passed.
        }
    }

    /// <summary>
    /// Reads a log through a share mode that admits other writers, rather than
    /// <see cref="File.ReadAllText(string)"/>'s own default of <see cref="FileShare.Read"/>. None
    /// of these assertions are about who else has the file open, and on a real machine something
    /// transient (an on-access scanner, the search indexer) touches a freshly written temp file
    /// often enough that the stricter read is a coin flip: observed as an intermittent "used by
    /// another process" on the restart-handoff test below, 2026-09-17, with the Restart Manager
    /// naming no holder at all a tenth of a second later.
    /// </summary>
    private static string ReadSharing(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader text = new(stream);
        return text.ReadToEnd();
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
    public void The_launcher_handoff_shape_admits_the_takeover_on_the_first_attempt_and_a_reader_with_it()
    {
        // The handoff shape itself, run for real rather than described. Both shipped Windows
        // launch paths now hand h9kd an inheritable FILE_APPEND_DATA handle their launcher
        // opened with FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE (PLAN.md §16 PLACEHOLDER-d4e64dfa),
        // and this holds a handle of exactly that shape while asserting what h9kd can do
        // underneath it: take the log over on the FIRST attempt, with no wait and no Win32
        // error 32 warning at start, and leave a reader following the log unaffected.
        //
        // What this replaces is the same test written against the shape that shipped before:
        // cmd.exe /c "h9kd < NUL >> h9kd.log 2>&1", where cmd.exe held the log with
        // FILE_SHARE_READ only for the daemon's whole run and the takeover was refused every
        // single time. That is the regression this asserts against — a launcher-held handle
        // that excludes writers, whatever opens it.
        //
        // Windows-only, and the Windows CI leg is the leg that matters: mandatory sharing and
        // an inherited standard handle have no ubuntu equivalent to reproduce them with.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The FileStream owns the handle from here, exactly as WindowsAppendOnlyLog's own writer
        // does, so disposing it is what releases the launcher's hold.
        using FileStream inherited = new(WindowsDaemonLaunch.OpenInheritableAppendLog(logFile), FileAccess.Write);

        // Stands in for a line written before Main — a missing runtime, an assembly that would
        // not load — which is the whole reason the handle is passed down rather than left for the
        // daemon to open once it is already running.
        inherited.Write(Encoding.UTF8.GetBytes("a line through the launcher's own handle\r\n"));
        inherited.Flush();

        List<TimeSpan> waits = [];
        using StreamWriter takenOver = WindowsAppendOnlyLog.OpenAppendWriter(
            logFile, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(150), waits.Add);
        waits.Should().BeEmpty("the launcher's share mode refuses nobody, so there is nothing to retry");

        takenOver.WriteLine("a line through the daemon's own handle");

        using FileStream reader = new(
            logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader text = new(reader);
        text.ReadToEnd().Should()
            .Contain("a line through the launcher's own handle")
            .And.Contain("a line through the daemon's own handle");
    }

    [Fact]
    public void An_oversized_log_rotates_while_the_daemon_and_its_launcher_both_hold_it()
    {
        // LogRotationService's five-minute tick, which is what enforces the 8 MB budget while a
        // daemon runs, and which on Windows never once succeeded before PLAN.md §16 PLACEHOLDER-d4e64dfa: the
        // cmd.exe redirect's FILE_SHARE_READ refused DaemonLogRotation's FileAccess.ReadWrite
        // open just as flatly as it refused the takeover, so every tick logged "Log rotation
        // failed; will retry next tick" instead. The handles held here are the two a running
        // daemon actually has — the inherited launcher handle and its own taken-over writer.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using FileStream inherited = new(WindowsDaemonLaunch.OpenInheritableAppendLog(logFile), FileAccess.Write);
        using StreamWriter daemonWriter = WindowsAppendOnlyLog.OpenAppendWriter(logFile);
        daemonWriter.WriteLine(new string('x', 4096));

        DaemonLogRotation.RotateIfOversized(logFile, thresholdBytes: 1024).Should().BeTrue();

        // The truncation landed, and the writer that was open across it still appends at the
        // new end of file rather than at a position cached when it was opened.
        daemonWriter.WriteLine("after the rotation");
        new FileInfo(logFile).Length.Should().BeLessThan(1024);
        ReadSharing(DaemonLogRotation.PreviousLogFile(logFile)).Should().Contain("xxxx");
    }

    [Fact]
    public void The_restart_handoff_lands_both_daemons_lines_with_no_nul_padding()
    {
        // h9k daemon restart and h9k update --restart stop one daemon and start another, and the
        // start path rotates the log on its way in (DaemonLifecycle.StartAsync). Under cmd.exe's
        // >> redirect that sequence had two failure modes: a second redirect's open could be
        // refused outright by the first cmd.exe's FILE_SHARE_READ if it had not finished dying,
        // and a handle whose write position was cached at open time would, after a truncation,
        // write at the old offset and leave Windows to zero-fill the gap — a log that reads back
        // at its pre-rotation size, padded with NULs. Both handles here are FILE_APPEND_DATA, so
        // the outgoing and incoming daemons can overlap freely and a rotation between them costs
        // nothing but the lines it rolled aside.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (FileStream outgoingInherited = new(
            WindowsDaemonLaunch.OpenInheritableAppendLog(logFile), FileAccess.Write))
        using (StreamWriter outgoingWriter = WindowsAppendOnlyLog.OpenAppendWriter(logFile))
        {
            outgoingWriter.WriteLine(new string('o', 4096));

            // The incoming daemon's launcher opens its own handle while the outgoing daemon still
            // holds both of its own — the overlap a restart genuinely has, since nothing waits for
            // a dying process's handle table to be torn down.
            using FileStream incomingInherited = new(
                WindowsDaemonLaunch.OpenInheritableAppendLog(logFile), FileAccess.Write);
            using StreamWriter incomingWriter = WindowsAppendOnlyLog.OpenAppendWriter(logFile);

            DaemonLogRotation.RotateIfOversized(logFile, thresholdBytes: 1024).Should().BeTrue();

            outgoingWriter.WriteLine("the outgoing daemon's last line");
            incomingWriter.WriteLine("the incoming daemon's first line");
        }

        string written = ReadSharing(logFile);
        written.Should().Contain("the outgoing daemon's last line");
        written.Should().Contain("the incoming daemon's first line");
        written.Should().NotContain("\0", "a cached write position is what leaves a zero-filled gap behind a truncation");
    }

    [Theory]
    // GetFinalPathNameByHandle always answers in extended-length form; DaemonRuntime.LogFile
    // never holds one, so a comparison that skipped the prefix would never match and the
    // legacy-launcher remedy would silently never be named.
    [InlineData(@"\\?\C:\Users\someone\.hall9k\h9kd.log", @"C:\Users\someone\.hall9k\h9kd.log", true)]
    [InlineData(@"\\?\UNC\fileserver\logs\h9kd.log", @"\\fileserver\logs\h9kd.log", true)]
    // Windows paths are case-insensitive, and the two sides come from different places: one
    // from the kernel, one from a config file or a default composed at startup.
    [InlineData(@"\\?\C:\Users\someone\.hall9k\H9KD.LOG", @"C:\Users\someone\.hall9k\h9kd.log", true)]
    // A different file in the same directory is the case that must not match: it would send a
    // reader after a stale autostart registration that has nothing to do with the real holder.
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

}
