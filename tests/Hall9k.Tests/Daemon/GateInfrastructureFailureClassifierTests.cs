using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The never-guess rule, applied exactly as backlog 40 applied it to budget exhaustion
/// (backlog 53): only the literal, recognizable shape of a connection-class failure — Npgsql
/// connection refused/reset/timeout, the SSLRequest handshake mismatch, Testcontainers itself
/// failing to start — or MSBuild's own MSB4166 child-node-exited-prematurely shape (Windows
/// field report item 3, ruled 2026-09-01) classifies as infrastructure. Anything else,
/// including a test's own assertion output, stays a real failure.
/// </summary>
public sealed class GateInfrastructureFailureClassifierTests
{
    [Theory]
    [InlineData("Gate 'test' exited 1. Output: Npgsql.NpgsqlException: Connection refused (127.0.0.1:55821)")]
    [InlineData("Gate 'test' exited 1. Output: Failed to connect to 127.0.0.1:55821 / exception while reading from stream")]
    [InlineData("Gate 'test' exited 1. Output: Npgsql: Received unknown response H for SSLRequest")]
    [InlineData("Gate 'test' exited 1. Output: DotNet.Testcontainers.Containers.ContainerNotFoundException: the container could not be started")]
    [InlineData("Gate 'test' exited 1. Output: Docker.DotNet.DockerApiException: 500 Internal Server Error")]
    [InlineData("Gate 'test' exited 1. Output: Connection reset by peer")]
    public void A_recognized_infrastructure_signature_classifies_as_infrastructure(string gateOutput) =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeTrue();

    /// <summary>
    /// Kept out of the shared theory above, unlike every other marker there, so a broken
    /// classifier failing this one specific case does not render the MSB4166 marker text into
    /// this fact's own failing display name — a `[Fact]` has none, where a `[Theory]`'s default
    /// display embeds its `[InlineData]` argument — and get the resulting `dotnet test` output
    /// misclassified as the very infrastructure failure this test exists to recognize (adversarial
    /// review, cycle 1). The pre-existing markers above already carry this same self-reference
    /// risk; this one is isolated rather than the whole theory reworked, since only this entry is
    /// this branch's own change.
    /// </summary>
    [Fact]
    public void An_MSB4166_child_node_crash_classifies_as_infrastructure()
    {
        string gateOutput =
            "Gate 'test' exited 1. Output: MSBUILD : error MSB4166: Child node \"1\" exited prematurely. " +
            "Diagnostic information may be found in files in " +
            "\"C:\\Users\\vssadmin\\AppData\\Local\\Temp\\MSBuild_pid-11064_3.failure.txt\". Shutting down. Fatal error.";
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeTrue();
    }

    [Theory]
    [InlineData("Gate 'test' exited 1. Output: Assert.Equal() Failure\nExpected: 3\nActual:   4")]
    [InlineData("Gate 'test' exited 1. Output: Xunit.Sdk.EqualException: expected true, was false")]
    [InlineData("Gate 'boom' exited 3. Output: exploding")]
    [InlineData("Gate 'build' exited 1. Output: error CS0246: The type or namespace could not be found")]
    [InlineData("Gate 'test' exited 1. Output: Npgsql.PostgresException: 23505: duplicate key value violates unique constraint")]
    [InlineData(
        "Gate 'test' exited 1. Output: Xunit.Sdk.EqualException: expected the log to mention MSBuild, but it did not")]
    public void A_test_assertion_or_build_error_stays_a_real_failure(string gateOutput) =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_output_is_never_guessed_as_infrastructure(string? gateOutput) =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeFalse();

    [Fact]
    public void MatchingExcerpt_carries_the_marker_even_when_it_sits_far_from_either_end() =>
        GateInfrastructureFailureClassifier.MatchingExcerpt(
                new string('x', 5000) + "Npgsql.NpgsqlException: Connection refused" + new string('y', 5000))
            .Should().Contain("Npgsql.NpgsqlException: Connection refused");

    [Fact]
    public void MatchingExcerpt_is_null_for_a_real_failure() =>
        GateInfrastructureFailureClassifier.MatchingExcerpt("Xunit.Sdk.EqualException: expected true, was false")
            .Should().BeNull();

    [Fact]
    public void MatchingExcerpt_names_the_MSB4166_marker_so_the_recorded_retry_cause_says_what_triggered_it() =>
        GateInfrastructureFailureClassifier.MatchingExcerpt(
                "MSBUILD : error MSB4166: Child node \"1\" exited prematurely. Shutting down. Fatal error.")
            .Should().Contain("MSB4166");

    // A directory rather than a captured-output string, deliberately: the gate's own console
    // output cannot be relied on here (see IsUnresolvedGateWaitTimeout's own doc comment) —
    // CrossProcessContainerGate.AcquireAsync instead writes a durable file directly into a
    // directory VerificationRunner names via GateWaitEvidenceDirectoryEnvironmentVariable, and
    // that file's presence at kill time, not any text scan, is what these two methods check.
    [Fact]
    public void A_wait_that_consumed_most_of_the_gate_timeout_classifies_as_a_timeout_infrastructure_failure()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "waiting-123-abc.txt"),
                "Waiting on cross-process container gate /tmp/hall9k-postgres-container-gate " +
                "(842s elapsed, 4 max concurrent) — every permit is currently held " +
                "(by this process's own other classes, or by another process on this machine)");

            // 842s clears 80% of a 15-minute (900s) budget.
            GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(directory, TimeSpan.FromMinutes(15))
                .Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The false positive this narrower check exists to close (independent pre-PR review, cycle
    /// 1): during a busy tier, several classes are queued behind the gate's fixed permit count at
    /// nearly every instant, entirely ordinarily. Evidence that <em>a</em> class was queued at the
    /// moment of the kill is not evidence that the killed gate's own overall run never made
    /// progress — only a wait that consumed most of the run's own budget is that signal.
    /// </summary>
    [Fact]
    public void A_brief_recent_wait_relative_to_a_much_larger_gate_timeout_does_not_classify_as_infrastructure()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "waiting-123-abc.txt"),
                "Waiting on cross-process container gate /tmp/hall9k-postgres-container-gate " +
                "(12s elapsed, 4 max concurrent) — every permit is currently held " +
                "(by this process's own other classes, or by another process on this machine)");

            GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(directory, TimeSpan.FromMinutes(30))
                .Should().BeFalse("12 seconds of ordinary queuing is not proof this gate's own run spent " +
                    "nearly its whole 30-minute budget stuck");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_evidence_directory_cleared_by_a_successful_acquire_does_not_classify_a_later_unrelated_timeout()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            // Empty: CrossProcessContainerGate.AcquireAsync deletes its own evidence file the
            // moment it acquires a permit, exactly as it would here once the wait resolved —
            // an ordinary hang or a real test failure after that point must not be misread as
            // still-queued-on-the-gate just because the directory once held evidence.
            GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(directory, TimeSpan.FromMinutes(15))
                .Should().BeFalse("the wait already resolved; nothing is queued on the gate anymore");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_or_nonexistent_evidence_directory_is_never_guessed_as_an_unresolved_gate_wait(string? gateWaitEvidenceDirectory) =>
        GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(gateWaitEvidenceDirectory, TimeSpan.FromMinutes(15))
            .Should().BeFalse();

    [Fact]
    public void UnresolvedGateWaitExcerpt_carries_the_evidence_file_content()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "waiting-123-abc.txt"),
                "Waiting on cross-process container gate /tmp/hall9k-postgres-container-gate " +
                "(842s elapsed, 4 max concurrent) — every permit is currently held " +
                "(by this process's own other classes, or by another process on this machine)");

            GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(directory, TimeSpan.FromMinutes(15))
                .Should().Contain("Waiting on cross-process container gate");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnresolvedGateWaitExcerpt_is_null_for_a_wait_that_never_cleared_the_gate_timeout_bar()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "waiting-123-abc.txt"),
                "Waiting on cross-process container gate /tmp/hall9k-postgres-container-gate " +
                "(12s elapsed, 4 max concurrent) — every permit is currently held " +
                "(by this process's own other classes, or by another process on this machine)");

            GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(directory, TimeSpan.FromMinutes(30))
                .Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnresolvedGateWaitExcerpt_skips_a_file_larger_than_the_bounded_read()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            // Well past the 4096-byte bound: even though the elapsed figure embedded in it would
            // otherwise clear the gate-timeout bar, the file itself is never read
            // (adversarial review, this cycle — HALL9K_VERIFY_GATE_WAIT_DIR is exported to the
            // agent's own test code too, so this is not a purely hypothetical size).
            string oversized = new string('x', 8192) + " (842s elapsed, 4 max concurrent)";
            File.WriteAllText(Path.Combine(directory, "waiting-123-abc.txt"), oversized);

            GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(directory, TimeSpan.FromMinutes(15))
                .Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnresolvedGateWaitExcerpt_is_null_when_no_evidence_file_exists()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(directory, TimeSpan.FromMinutes(15))
                .Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnresolvedGateWaitExcerpt_is_null_for_a_nonexistent_directory() =>
        GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(null, TimeSpan.FromMinutes(15)).Should().BeNull();
}
