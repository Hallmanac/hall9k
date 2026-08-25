using FluentAssertions;
using Hall9k.Daemon;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class WindowsAppendOnlyLogTests : IDisposable
{
    private readonly string logFile = Path.Combine(
        Path.GetTempPath(), $"h9k-append-only-{Path.GetRandomFileName()}.log");

    public void Dispose() => File.Delete(logFile);

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
}
