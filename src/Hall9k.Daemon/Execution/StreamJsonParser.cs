using System.Text.Json;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
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
/// <param name="Subtype">
/// Claude Code's own <c>subtype</c> field on the terminal result — <c>"success"</c>,
/// <c>"error_max_turns"</c> (the session's own <c>--max-turns</c> cap was reached),
/// <c>"error_during_execution"</c>, and others. Null when the payload carried none or an
/// unparseable one, never guessed at as a specific value: <see cref="MaxTurnsResultSubtype"/> is
/// the one value any caller reads today, and only ever to tell an honest turn-budget cutoff
/// apart from every other error shape (AGENTS.md's never-guess rule) rather than inferring it
/// from <see cref="Turns"/> crossing a task's own declared limit, which a session that simply
/// ran long for an unrelated reason could also do.
/// </param>
public sealed record AgentResult(
    bool IsError,
    long InputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long OutputTokens,
    decimal? CostUsd,
    int? Turns,
    string? Summary = null,
    int? DurationMs = null,
    string? Subtype = null)
{
    public TokensRecorded ToTokensRecorded(Guid runId, DateTimeOffset recordedAt, AgentModel model) =>
        new(runId, InputTokens, OutputTokens, CostUsd, recordedAt, CacheReadInputTokens, CacheCreationInputTokens, model);

    /// <summary>
    /// The publication-errand equivalent of <see cref="ToTokensRecorded"/>: same fields, a
    /// different event type because it rides the task's own stream rather than a run's (see
    /// <see cref="PublicationTokensRecorded"/>'s own doc for why that has to be a distinct type).
    /// </summary>
    public PublicationTokensRecorded ToPublicationTokensRecorded(Guid taskId, DateTimeOffset recordedAt, AgentModel model) =>
        new(taskId, InputTokens, OutputTokens, CostUsd, recordedAt, CacheReadInputTokens, CacheCreationInputTokens, model);

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
    /// <summary>Claude Code's own <c>subtype</c> value for a session cut off by its own <c>--max-turns</c> cap — <see cref="AgentResult.Subtype"/>'s own doc.</summary>
    public const string MaxTurnsResultSubtype = "error_max_turns";

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

            string? subtype = root.TryGetProperty("subtype", out JsonElement subtypeElement)
                && subtypeElement.ValueKind == JsonValueKind.String
                ? subtypeElement.GetString()
                : null;

            // Alongside num_turns on the result payload, not under usage — and the same
            // never-guess discipline: absent or unparseable is null, never zero, so a session
            // this parser could not time never reads as one that finished instantly (task: a
            // session that exits at once with no work done is treated as the node failing to
            // launch sessions — the launch-failure classifier depends on this being an honest
            // "unknown" rather than a guessed zero).
            int? durationMs = root.TryGetProperty("duration_ms", out JsonElement durationElement)
                && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetInt32(out int durationValue)
                ? durationValue
                : null;

            result = new AgentResult(
                isError, inputTokens, cacheReadInputTokens, cacheCreationInputTokens, outputTokens, costUsd, turns,
                summary, durationMs, subtype);
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

    /// <summary>
    /// Whether this stream-json line carries a "usage" object reporting any nonzero token count —
    /// wherever the line puts it: the terminal "result" line's own top-level "usage"
    /// (<see cref="TryParseResult"/>'s own field), or an intermediate "assistant" turn's own
    /// "message.usage" (a per-turn field this parser otherwise never reads — log #6's own
    /// "everything else is transcript" choice still holds for every other field on that line).
    /// Used only as a lighter-weight evidence check on a session that has not finished yet
    /// (<see cref="Hall9k.Daemon.Execution.LaunchHoldMonitor"/>'s own probe-still-running clear,
    /// task: a session that exits at once with no work done is treated as the node failing to
    /// launch sessions, independent pre-PR review, cycle 1, conformance lens) — never to
    /// reconstruct a session's actual totals, which is <see cref="StreamTailReader.ReadFinalResultAsync"/>'s
    /// own job once the session is actually over.
    /// </summary>
    public static bool LineReportsNonzeroUsage(string line)
    {
        if (!line.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("usage", out JsonElement usage) && HasNonzeroTokenCount(usage))
            {
                return true;
            }

            return root.TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("usage", out JsonElement nestedUsage)
                && HasNonzeroTokenCount(nestedUsage);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNonzeroTokenCount(JsonElement usage) =>
        ReadTokenCount(usage, "input_tokens") > 0
        || ReadTokenCount(usage, "cache_read_input_tokens") > 0
        || ReadTokenCount(usage, "cache_creation_input_tokens") > 0
        || ReadTokenCount(usage, "output_tokens") > 0;

    /// <summary>
    /// One line's own contribution to a still-running session's live token spend — every token
    /// billed for THIS turn alone, read the same two places <see cref="LineReportsNonzeroUsage"/>
    /// already looks (a line's own top-level "usage", or an intermediate "assistant" turn's
    /// "message.usage"). The terminal "result" line is deliberately excluded and always reads
    /// zero here: its own "usage" is the whole session's cumulative total (<see cref="TryParseResult"/>'s
    /// own field, and <see cref="StreamTailReader.ReadFinalResultAsync"/>'s own doc on
    /// <c>total_cost_usd</c> being "cumulative to the moment a line is printed"), so folding it into
    /// a sum across every line would double-count every turn that led up to it. Used only by
    /// <see cref="Hall9k.Daemon.Review.SpikeEngine.EndRunsOverTokenBudgetAsync"/>'s own live-budget
    /// watch, where a session's declared token budget has to be checked WHILE it is still running —
    /// <see cref="Domain.Features.Run.Projections.RunDetails"/>'s own token totals only ever
    /// accumulate once <c>TokensRecorded</c> lands at session end, which is too late for a budget
    /// that is meant to end the session, not merely describe it afterward.
    /// </summary>
    public static long ReadLineTokenSpend(string line)
    {
        if (!line.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("type", out JsonElement type) && type.GetString() == "result")
            {
                return 0;
            }

            if (root.TryGetProperty("usage", out JsonElement usage))
            {
                return SumTokenCount(usage);
            }

            return root.TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("usage", out JsonElement nestedUsage)
                ? SumTokenCount(nestedUsage)
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static long SumTokenCount(JsonElement usage) =>
        ReadTokenCount(usage, "input_tokens")
        + ReadTokenCount(usage, "cache_read_input_tokens")
        + ReadTokenCount(usage, "cache_creation_input_tokens")
        + ReadTokenCount(usage, "output_tokens");
}
