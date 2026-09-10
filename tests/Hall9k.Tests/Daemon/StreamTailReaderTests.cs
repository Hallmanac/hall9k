using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class StreamTailReaderTests : IDisposable
{
    private readonly string _streamFile = Path.Combine(Path.GetTempPath(), $"stream-{Guid.NewGuid():N}.jsonl");

    /// <summary>
    /// Trimmed from run 01a07574-4db1's own stream.jsonl (discovery cc9b7aec): a background
    /// subagent's task notification produced its own short reaction leg's result first, ahead
    /// of the whole 302-turn session's own result.
    /// </summary>
    private const string ReactionLegResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":5,"duration_ms":29137,"total_cost_usd":24.780697800000002,"usage":{"input_tokens":10,"cache_creation_input_tokens":59074,"cache_read_input_tokens":324100,"output_tokens":1814}}""";

    private const string WholeSessionResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":302,"duration_ms":4586641,"total_cost_usd":24.780697800000002,"usage":{"input_tokens":602,"cache_creation_input_tokens":454222,"cache_read_input_tokens":97887837,"output_tokens":177695}}""";

    [Fact]
    public async Task A_stream_holding_two_result_lines_picks_the_one_that_accounts_for_the_whole_session()
    {
        await File.WriteAllTextAsync(_streamFile, $"{ReactionLegResult}\n{WholeSessionResult}\n");

        AgentResult result = await StreamTailReader.ReadFinalResultAsync(_streamFile, CancellationToken.None);

        result.Turns.Should().Be(302, "the reaction leg's own 5 turns undercount the session by 60x");
        result.CacheReadInputTokens.Should().Be(97_887_837, "not the reaction leg's 324,100");
        result.OutputTokens.Should().Be(177_695, "not the reaction leg's 1,814 (log #2's own under-count)");
    }

    [Fact]
    public async Task A_stream_holding_one_result_line_records_exactly_what_it_records_today()
    {
        await File.WriteAllTextAsync(_streamFile, $"{WholeSessionResult}\n");

        AgentResult result = await StreamTailReader.ReadFinalResultAsync(_streamFile, CancellationToken.None);

        result.Turns.Should().Be(302);
        result.CacheReadInputTokens.Should().Be(97_887_837);
        result.OutputTokens.Should().Be(177_695);
    }

    [Fact]
    public async Task A_smaller_result_line_trailing_a_larger_one_does_not_replace_it()
    {
        // The reverse file order from the build-session case above — a resumed fix session's
        // own shape (acceptance criteria: "writes its first leg's result line first and its
        // second leg's after", the first the larger of the two). The line accounting for more
        // of the session wins regardless of which one the file holds last.
        await File.WriteAllTextAsync(_streamFile, $"{WholeSessionResult}\n{ReactionLegResult}\n");

        AgentResult result = await StreamTailReader.ReadFinalResultAsync(_streamFile, CancellationToken.None);

        result.Turns.Should().Be(302);
    }

    /// <summary>
    /// Trimmed from a real review-fix session's own stream.jsonl (adversarial pre-PR review,
    /// cycle 1, task a5f07d69: <c>review-fix-1-01a071be.stream.jsonl</c>). Unlike the reaction-leg
    /// shape above, the two lines here carry different <c>total_cost_usd</c> — a genuinely
    /// separate second leg, not a duplicate view of the first — so both legs' own usage and
    /// turns must sum, and the later leg's higher cost is what the whole session actually cost.
    /// </summary>
    private const string FirstFixLegResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":17,"total_cost_usd":0.7061382,"usage":{"input_tokens":100,"cache_creation_input_tokens":50000,"cache_read_input_tokens":1109851,"output_tokens":13842}}""";

    private const string SecondFixLegResult =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":5,"total_cost_usd":0.8553866,"usage":{"input_tokens":20,"cache_creation_input_tokens":9000,"cache_read_input_tokens":537002,"output_tokens":2096}}""";

    [Fact]
    public async Task Two_result_lines_with_different_costs_sum_their_disjoint_usage_and_take_the_larger_cost()
    {
        await File.WriteAllTextAsync(_streamFile, $"{FirstFixLegResult}\n{SecondFixLegResult}\n");

        AgentResult result = await StreamTailReader.ReadFinalResultAsync(_streamFile, CancellationToken.None);

        result.OutputTokens.Should().Be(13_842 + 2_096, "each leg billed its own, disjoint usage");
        result.CacheReadInputTokens.Should().Be(1_109_851 + 537_002);
        result.Turns.Should().Be(17 + 5, "both legs' own round trips, not just the larger leg's");
        result.CostUsd.Should().Be(0.8553866m, "cost is cumulative, so the later, larger figure is the true total");
    }

    public void Dispose()
    {
        if (File.Exists(_streamFile))
        {
            File.Delete(_streamFile);
        }
    }
}
