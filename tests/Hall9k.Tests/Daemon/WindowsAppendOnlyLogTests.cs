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
}
