using System.Text.Json;

namespace Hall9k.Daemon.Execution;

public sealed record AgentResult(bool IsError, long InputTokens, long OutputTokens, decimal? CostUsd);

/// <summary>
/// Minimal, tolerant reader of claude's stream-json lines. The only line the daemon must
/// understand is the terminal "result" event — the completion signal (log #2). Everything
/// else is transcript, kept on disk, never parsed into events (log #6).
/// </summary>
public static class StreamJsonParser
{
    public static bool TryParseResult(string line, out AgentResult result)
    {
        result = new AgentResult(true, 0, 0, null);
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
            long outputTokens = 0;
            if (root.TryGetProperty("usage", out JsonElement usage))
            {
                inputTokens = usage.TryGetProperty("input_tokens", out JsonElement input) ? input.GetInt64() : 0;
                outputTokens = usage.TryGetProperty("output_tokens", out JsonElement output) ? output.GetInt64() : 0;
            }

            decimal? costUsd = root.TryGetProperty("total_cost_usd", out JsonElement cost)
                && cost.ValueKind == JsonValueKind.Number
                ? cost.GetDecimal()
                : null;

            result = new AgentResult(isError, inputTokens, outputTokens, costUsd);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
