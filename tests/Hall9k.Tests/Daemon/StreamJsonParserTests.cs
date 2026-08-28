using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run.Events;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class StreamJsonParserTests
{
    /// <summary>
    /// Trimmed from a real run's stream.jsonl (2026-08-18). The shape that matters: a cached
    /// session bills 118 fresh input tokens and 8.4 million cache tokens, which is exactly how
    /// the event store ended up showing 444,443 output tokens against 822 input tokens (log #30).
    /// </summary>
    private const string CachedSessionResult =
        """
        {"is_error":false,"duration_api_ms":1025191,"num_turns":75,"stop_reason":"end_turn","session_id":"01a01754-5361-70d5-8e89-0231a83d9a4c","total_cost_usd":16.19052,"usage":{"input_tokens":118,"cache_creation_input_tokens":196080,"cache_read_input_tokens":8239942,"output_tokens":80515,"output_tokens_details":{"thinking_tokens":45241},"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard","cache_creation":{"ephemeral_1h_input_tokens":196080,"ephemeral_5m_input_tokens":0},"iterations":[{"input_tokens":2,"output_tokens":2256,"cache_read_input_tokens":211535,"cache_creation_input_tokens":477,"type":"message"}]},"modelUsage":{"claude-fable-5":{"inputTokens":118,"outputTokens":80515,"cacheReadInputTokens":8239942,"cacheCreationInputTokens":196080,"costUSD":16.188472}},"permission_denials":[],"subtype":"success","result":"The work is complete.","type":"result","duration_ms":1136191,"uuid":"cafe2287-6ab3-416f-9005-469deed33dba"}
        """;

    [Fact]
    public void A_cached_session_records_every_input_token_the_payload_reports()
    {
        StreamJsonParser.TryParseResult(CachedSessionResult, out AgentResult result).Should().BeTrue();

        result.IsError.Should().BeFalse();
        result.InputTokens.Should().Be(118, "fresh prompt input stays its own field");
        result.CacheReadInputTokens.Should().Be(8_239_942, "cache reads are where a resumed session's input actually lives");
        result.CacheCreationInputTokens.Should().Be(196_080, "cache writes price differently again, so they stay separate");
        result.OutputTokens.Should().Be(80_515);
        result.TotalInputTokens.Should().Be(8_436_140);
        result.Turns.Should().Be(75, "num_turns is top-level on the result payload, not under usage");
        result.Summary.Should().Be("The work is complete.");
    }

    [Fact]
    public void A_result_with_no_num_turns_field_records_null_rather_than_a_guess()
    {
        const string turnless =
            """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":10,"output_tokens":5}}""";

        StreamJsonParser.TryParseResult(turnless, out AgentResult result).Should().BeTrue();

        result.Turns.Should().BeNull(
            "an absent num_turns is a session this parser could not measure, not one that took no round trips");
    }

    [Fact]
    public void The_reported_cost_is_recorded_as_observed_and_never_recomputed()
    {
        StreamJsonParser.TryParseResult(CachedSessionResult, out AgentResult result).Should().BeTrue();

        result.CostUsd.Should().Be(16.19052m, "the result reported it; the daemon does not price tokens itself");
    }

    [Fact]
    public void The_parsed_usage_lands_on_the_event_the_run_stream_stores()
    {
        StreamJsonParser.TryParseResult(CachedSessionResult, out AgentResult result).Should().BeTrue();

        Guid runId = Guid.Parse("01a01754-4f0e-7775-af1e-3aca2e67be8b");
        DateTimeOffset recordedAt = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        TokensRecorded recorded = result.ToTokensRecorded(runId, recordedAt);

        recorded.Should().Be(new TokensRecorded(runId, 118, 80_515, 16.19052m, recordedAt, 8_239_942, 196_080));
    }

    [Fact]
    public void Absent_cache_fields_record_as_zero_rather_than_a_guess()
    {
        const string uncachedResult =
            """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":1200,"output_tokens":300},"total_cost_usd":0.0123}""";

        StreamJsonParser.TryParseResult(uncachedResult, out AgentResult result).Should().BeTrue();

        result.InputTokens.Should().Be(1200);
        result.CacheReadInputTokens.Should().Be(0);
        result.CacheCreationInputTokens.Should().Be(0);
        result.TotalInputTokens.Should().Be(1200);
    }

    [Fact]
    public void A_result_without_usage_records_zeros_and_no_cost()
    {
        const string usageless = """{"type":"result","subtype":"success","is_error":false,"result":"done"}""";

        StreamJsonParser.TryParseResult(usageless, out AgentResult result).Should().BeTrue();

        result.InputTokens.Should().Be(0);
        result.CacheReadInputTokens.Should().Be(0);
        result.CacheCreationInputTokens.Should().Be(0);
        result.OutputTokens.Should().Be(0);
        result.CostUsd.Should().BeNull("an unreported cost is unknown, not zero");
    }

    [Fact]
    public void An_error_result_still_reports_the_tokens_it_burned()
    {
        const string erroredResult =
            """{"is_error":true,"total_cost_usd":1.728278,"usage":{"input_tokens":14,"cache_creation_input_tokens":37635,"cache_read_input_tokens":257857,"output_tokens":7439},"subtype":"error_during_execution","type":"result"}""";

        StreamJsonParser.TryParseResult(erroredResult, out AgentResult result).Should().BeTrue();

        result.IsError.Should().BeTrue();
        result.TotalInputTokens.Should().Be(295_506, "a failed session still costs what it cost");
        result.OutputTokens.Should().Be(7439);
        result.CostUsd.Should().Be(1.728278m);
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"the result is 42"}]}}""")]
    [InlineData("""{"type":"result",""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Lines_that_are_not_a_terminal_result_are_ignored(string line) =>
        StreamJsonParser.TryParseResult(line, out _).Should().BeFalse();
}
