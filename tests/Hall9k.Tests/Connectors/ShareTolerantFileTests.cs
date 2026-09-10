using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The read every gate-output classification goes through. The case that matters is the one
/// <c>File.ReadAllText</c> cannot serve: a file some other live handle still holds open for
/// writing, which is what a gate's redirected log is for as long as the gate's own shell has not
/// finished dying (see <see cref="ShareTolerantFile"/>'s own origin incident).
/// </summary>
public sealed class ShareTolerantFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"hall9k-share-{Guid.NewGuid():N}");

    public ShareTolerantFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void A_file_another_handle_still_holds_open_for_writing_is_read_not_refused()
    {
        string path = Path.Combine(_directory, "verify-gate.log");
        using FileStream writer = new(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using StreamWriter text = new(writer, leaveOpen: true);
        text.Write("Npgsql.NpgsqlException: Connection refused");
        text.Flush();

        // The writer is deliberately still open here — exactly the state a killed gate's own
        // shell leaves its redirected log in, since Process.Kill only asks the operating system
        // to terminate and returns before the tree is actually gone.
        ShareTolerantFile.TryReadAllText(path).Should().Contain(
            "Npgsql.NpgsqlException: Connection refused",
            "a live writer is the normal state of a gate log at classification time, not a reason "
            + "to report the gate's output as unobservable");
    }

    [Fact]
    public void A_file_held_exclusively_reads_as_unobserved_rather_than_as_empty()
    {
        string path = Path.Combine(_directory, "verify-gate.log");
        File.WriteAllText(path, "Npgsql.NpgsqlException: Connection refused");
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        ShareTolerantFile.TryReadAllText(path).Should().BeNull(
            "null is the honest answer for a file that exists and could not be read — a caller "
            + "classifying on this text has to tell that apart from a writer that produced nothing");
    }

    [Fact]
    public void A_file_that_does_not_exist_reads_as_empty_not_as_unobserved()
    {
        ShareTolerantFile.TryReadAllText(Path.Combine(_directory, "never-written.log")).Should().BeEmpty(
            "a gate that never created its log genuinely produced no output — that is an "
            + "observation, not a gap");
    }

    [Fact]
    public void A_log_under_a_directory_that_does_not_exist_reads_as_empty_too()
    {
        string path = Path.Combine(_directory, "run-that-never-started", "verify-gate.log");

        ShareTolerantFile.TryReadAllText(path).Should().BeEmpty(
            "a run directory nobody ever created holds no output either — the same observation "
            + "as a log file that was never written, not a read that failed");
    }

    [Fact]
    public void A_path_that_exists_but_cannot_be_opened_as_a_file_reads_as_unobserved()
    {
        // A directory is the portable stand-in for every path File.Exists answers false for
        // without the file being absent — a denied ACL is the case that matters in the field, and
        // it is the same conflation: something is there, nobody can read it, and the pre-check
        // this method deliberately no longer performs called that an empty log (Copilot review,
        // PR #313).
        ShareTolerantFile.TryReadAllText(_directory).Should().BeNull(
            "a path that exists and cannot be read as a file is the unobserved case, and "
            + "classifying it as a gate that printed nothing is exactly the guess this type "
            + "exists to refuse");
    }

    public void Dispose() => TemporaryTree.TryDelete(_directory);
}
