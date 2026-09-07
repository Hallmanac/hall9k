using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The fixed method's token-counting half, isolated from the process it shells out to (task: an
/// operator starts a lean node or project orchestrator window; the method has to be the same on
/// every run so numbers compare across projects and across time).
/// </summary>
public sealed class OrchestratorMeasureProbeTests
{
    [Fact]
    public void It_sums_every_input_side_usage_field()
    {
        const string json = """
            {"type":"result","usage":{"input_tokens":120,"cache_creation_input_tokens":900,"cache_read_input_tokens":18000,"output_tokens":4}}
            """;

        OrchestratorMeasureProbe.ParseTurnOneTokens(json).Should().Be(120 + 900 + 18000);
    }

    [Fact]
    public void A_missing_usage_object_is_reported_rather_than_guessed()
    {
        const string json = """{"type":"result"}""";

        Action act = () => OrchestratorMeasureProbe.ParseTurnOneTokens(json);

        act.Should().Throw<DomainValidationException>().WithMessage("*usage*");
    }

    [Fact]
    public void Malformed_output_is_reported_rather_than_guessed()
    {
        Action act = () => OrchestratorMeasureProbe.ParseTurnOneTokens("not json");

        act.Should().Throw<DomainValidationException>();
    }
}
