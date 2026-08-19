using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class DaemonBinaryTests
{
    // The directory h9k was invoked from, shaped for whichever host runs the test:
    // Path.GetFullPath refuses a base path that is not fully qualified, and a Unix-style
    // /Users/someone/code/hall9k is not one on Windows. Origin incident (2026-08-20): the
    // Unix literal made these two tests fail on CI's windows-latest job, where the base
    // path threw and the resolver returned its null-for-malformed answer.
    private static readonly string CallerDirectory = Path.Combine(Path.GetTempPath(), "code", "hall9k");

    [Fact]
    public void A_relative_override_resolves_against_the_callers_directory_not_the_run_root()
    {
        // The detach intermediary runs in ~/.hall9k, so an override left relative would
        // name a different file there. Origin incident: h9k daemon start --binary
        // ./src/Hall9k.Daemon/bin/Debug/net10.0/h9kd from the repo root blocked for the
        // full 10s start timeout and then reported a startup failure, not a bad path.
        string resolved = DaemonBinary.ResolveOverride("./bin/Debug/h9kd", CallerDirectory)!;

        resolved.Should().Be(Path.Combine(CallerDirectory, "bin", "Debug", "h9kd"));
    }

    [Fact]
    public void An_absolute_override_is_returned_as_given()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "opt", "h9kd");

        string resolved = DaemonBinary.ResolveOverride(absolute, CallerDirectory)!;

        resolved.Should().Be(absolute);
    }

    [Fact]
    public void A_malformed_override_is_null_rather_than_an_exception_out_of_the_start_path()
    {
        // A path Path.GetFullPath refuses (an embedded null character is the only one
        // left on Unix) reaches the start path as an ordinary "no binary there" refusal,
        // not as an unhandled exception with a stack trace.
        DaemonBinary.ResolveOverride("bin/h9\0kd", CallerDirectory).Should().BeNull();
    }
}
