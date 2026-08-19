using System.Text.Json;
using Hall9k.Domain.Features.Run.Events;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The terminal result event's observed usage. The input side is split the way the payload
/// splits it (fresh prompt input, cache reads, cache writes) because the three price
/// differently; CostUsd is whatever the result reported, never recomputed from these counts.
/// </summary>
public sealed record AgentResult(
    bool IsError,
    long InputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long OutputTokens,
    decimal? CostUsd,
    string? Summary = null)
{
    public TokensRecorded ToTokensRecorded(Guid runId, DateTimeOffset recordedAt) =>
        new(runId, InputTokens, OutputTokens, CostUsd, recordedAt, CacheReadInputTokens, CacheCreationInputTokens);

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
        result = new AgentResult(true, 0, 0, 0, 0, null);
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

            string? summary = root.TryGetProperty("result", out JsonElement text)
                && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

            result = new AgentResult(
                isError, inputTokens, cacheReadInputTokens, cacheCreationInputTokens, outputTokens, costUsd, summary);
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
