using System.Text;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class DaemonLogTests : IDisposable
{
    private readonly string logFile = Path.Combine(
        Path.GetTempPath(), $"h9kd-log-{Path.GetRandomFileName()}.log");

    public void Dispose()
    {
        File.Delete(logFile);
        File.Delete(logFile + ".1");
    }

    [Fact]
    public void A_missing_log_tails_to_nothing()
    {
        DaemonLog.Tail(logFile, 5).Should().BeEmpty();
        DaemonLog.CurrentLength(logFile).Should().Be(0);
        DaemonLog.FindCatchUpReport(logFile, 0).Should().BeNull();
    }

    [Fact]
    public void The_tail_is_the_last_lines_in_order()
    {
        File.WriteAllText(logFile, "one\ntwo\nthree\nfour\n");

        DaemonLog.Tail(logFile, 2).Should().Equal("three", "four");
        DaemonLog.Tail(logFile, 10).Should().Equal("one", "two", "three", "four");
    }

    [Fact]
    public void A_log_far_larger_than_the_read_budget_still_tails_correctly()
    {
        // The log is append-only across every start and a single failed startup writes
        // kilobytes of stack trace, so the whole file must never be read to print a few
        // lines. 4 MB here is well past the 64 KB the tail actually reads.
        using (StreamWriter writer = new(logFile, append: false, Encoding.UTF8))
        {
            string filler = new('x', 200);
            for (int line = 0; line < 20_000; line++)
            {
                writer.Write($"line {line} {filler}\n");
            }

            writer.Write("last line\n");
        }

        new FileInfo(logFile).Length.Should().BeGreaterThan(4 * 1024 * 1024);

        IReadOnlyList<string> tail = DaemonLog.Tail(logFile, 3);

        tail.Should().HaveCount(3);
        tail[^1].Should().Be("last line");
        // A read that starts mid-file starts mid-line; the fragment is dropped, so
        // every line handed back is a whole one.
        tail.Should().OnlyContain(line => line == "last line" || line.StartsWith("line 19", StringComparison.Ordinal));
    }

    [Fact]
    public void The_catch_up_report_is_found_only_past_the_offset_the_caller_took()
    {
        File.WriteAllText(logFile, "info: Catch-up complete for the previous run\n");
        long offset = DaemonLog.CurrentLength(logFile);

        DaemonLog.FindCatchUpReport(logFile, offset).Should().BeNull();

        File.AppendAllText(logFile, "info: Catch-up complete: adopted 1, swept 2\n");

        DaemonLog.FindCatchUpReport(logFile, offset).Should().Be("Catch-up complete: adopted 1, swept 2");
    }

    [Fact]
    public void A_failure_line_past_the_offset_is_found_whole()
    {
        File.WriteAllText(logFile, "info: starting\n");
        long offset = DaemonLog.CurrentLength(logFile);
        File.AppendAllText(logFile, "info: connecting\nfail: Npgsql could not connect\ninfo: exiting\n");

        DaemonLog.FindLine(logFile, offset, "fail:").Should().Be("fail: Npgsql could not connect");
        DaemonLog.FindLine(logFile, offset, "nothing here").Should().BeNull();
    }
}
