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
    public void A_test_assertion_or_build_error_stays_a_real_failure(string gateOutput) =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_absent_output_is_never_guessed_as_infrastructure(string? gateOutput) =>
        GateInfrastructureFailureClassifier.IsInfrastructureFailure(gateOutput).Should().BeFalse();
}
