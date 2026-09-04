using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The never-guess rule, applied exactly as backlog 40 applied it to budget exhaustion
/// (backlog 53): only the literal, recognizable shape of a connection-class failure — Npgsql
/// connection refused/reset/timeout, the SSLRequest handshake mismatch, Testcontainers itself
/// failing to start — classifies as infrastructure. Anything else, including a test's own
/// assertion output, stays a real failure.
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
    public void A_connection_class_signature_classifies_as_infrastructure(string gateOutput) =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeTrue();

    [Theory]
    [InlineData("Gate 'test' exited 1. Output: Assert.Equal() Failure\nExpected: 3\nActual:   4")]
    [InlineData("Gate 'test' exited 1. Output: Xunit.Sdk.EqualException: expected true, was false")]
    [InlineData("Gate 'boom' exited 3. Output: exploding")]
    [InlineData("Gate 'build' exited 1. Output: error CS0246: The type or namespace could not be found")]
    [InlineData("Gate 'test' exited 1. Output: Npgsql.PostgresException: 23505: duplicate key value violates unique constraint")]
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

    // A directory rather than a captured-output string, deliberately: the gate's own console
    // output cannot be relied on here (see IsUnresolvedGateWaitTimeout's own doc comment) —
    // CrossProcessContainerGate.AcquireAsync instead writes a durable file directly into a
    // directory VerificationRunner names via GateWaitEvidenceDirectoryEnvironmentVariable, and
    // that file's presence at kill time, not any text scan, is what these two methods check.
    [Fact]
    public void A_leftover_wait_evidence_file_classifies_as_a_timeout_infrastructure_failure()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-wait-test-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "waiting-123-abc.txt"),
                "Waiting on cross-process container gate /tmp/hall9k-postgres-container-gate " +
                "(842s elapsed, 4 max concurrent) — every permit is currently held " +
                "(by this process's own other classes, or by another process on this machine)");

            GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(directory).Should().BeTrue();
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
            GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(directory)
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
        GateInfrastructureFailureClassifier.IsUnresolvedGateWaitTimeout(gateWaitEvidenceDirectory).Should().BeFalse();

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

            GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(directory)
                .Should().Contain("Waiting on cross-process container gate");
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
            GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(directory).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnresolvedGateWaitExcerpt_is_null_for_a_nonexistent_directory() =>
        GateInfrastructureFailureClassifier.UnresolvedGateWaitExcerpt(null).Should().BeNull();
}
