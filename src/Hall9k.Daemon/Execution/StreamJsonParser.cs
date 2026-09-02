using System.Text.Json;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The terminal result event's observed usage. The input side is split the way the payload
/// splits it (fresh prompt input, cache reads, cache writes) because the three price
/// differently; CostUsd is whatever the result reported, never recomputed from these counts.
/// Turns is claude's own `num_turns` count — the session's own record of how many round trips
/// it took, read back per pass so any future before-versus-after production comparison of a
/// review-prompt change is a query rather than a re-measurement. Null when the result payload carried no
/// `num_turns` field or an unparseable one — never guessed at as zero, which would read as a
/// session that took no round trips at all rather than one this parser could not measure.
/// </summary>
public sealed record AgentResult(
    bool IsError,
    long InputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long OutputTokens,
    decimal? CostUsd,
    int? Turns,
    string? Summary = null)
{
    public TokensRecorded ToTokensRecorded(Guid runId, DateTimeOffset recordedAt, AgentModel model) =>
        new(runId, InputTokens, OutputTokens, CostUsd, recordedAt, CacheReadInputTokens, CacheCreationInputTokens, model);

    /// <summary>Every input token the session was billed for, whatever the cache did with it.</summary>
    public long TotalInputTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}

/// <summary>
/// Minimal, tolerant reader of claude's stream-json lines. The only line the daemon must
/// understand is the terminal "result" event — the completion signal (log #2). Everything
/// else is transcript, kept on disk, never parsed into events (log #6).
/// </summary>
public static class StreamJsonParser
{
    public static bool TryParseResult(string line, out AgentResult result)
    {
        result = new AgentResult(true, 0, 0, 0, 0, null, null);
        if (!line.Contains("\"result\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "result")
            {
                return false;
            }

            bool isError = root.TryGetProperty("is_error", out JsonElement error) && error.GetBoolean();

            long inputTokens = 0;
            long cacheReadInputTokens = 0;
            long cacheCreationInputTokens = 0;
            long outputTokens = 0;
            if (root.TryGetProperty("usage", out JsonElement usage))
            {
                // A cached session reports nearly all of its input under cache_read_input_tokens;
                // reading only input_tokens undercounts the input side by orders of magnitude
                // (log #30). An absent field is zero, never inferred from the others.
                inputTokens = ReadTokenCount(usage, "input_tokens");
                cacheReadInputTokens = ReadTokenCount(usage, "cache_read_input_tokens");
                cacheCreationInputTokens = ReadTokenCount(usage, "cache_creation_input_tokens");
                outputTokens = ReadTokenCount(usage, "output_tokens");
            }

            decimal? costUsd = root.TryGetProperty("total_cost_usd", out JsonElement cost)
                && cost.ValueKind == JsonValueKind.Number
                ? cost.GetDecimal()
                : null;

            // Top-level on the result payload, alongside total_cost_usd — not under usage,
            // which only ever carries token counts. Null rather than 0 when absent or
            // unparseable: this is what a before-versus-after production comparison measures
            // per pass, and a guessed zero would read as an observed fact about a session that
            // never happened.
            int? turns = root.TryGetProperty("num_turns", out JsonElement turnsElement)
                && turnsElement.ValueKind == JsonValueKind.Number
                && turnsElement.TryGetInt32(out int turnsValue)
                ? turnsValue
                : null;

            string? summary = root.TryGetProperty("result", out JsonElement text)
                && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

            result = new AgentResult(
                isError, inputTokens, cacheReadInputTokens, cacheCreationInputTokens, outputTokens, costUsd, turns,
                summary);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long ReadTokenCount(JsonElement usage, string property) =>
        usage.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long count)
                ? count
                : 0;
}
