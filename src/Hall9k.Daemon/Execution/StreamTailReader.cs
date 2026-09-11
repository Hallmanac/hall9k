using System.Text;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Incremental tail over a session's stream.jsonl: reads only the bytes past the caller's
/// cursor, buffers a trailing partial line across calls, and stops at the next result
/// event — which, on a stream holding more than one, is a leg finishing rather than the
/// session's own terminal line (<see cref="ReadFinalResultAsync"/> is what a caller re-reads
/// the whole file with once it independently confirms the process has exited). Shared by
/// RunSupervisor (main session) and ReviewEngine (review and fix legs) so neither re-reads a
/// long transcript from the start on every poll.
/// </summary>
internal static class StreamTailReader
{
    internal static async Task<(long Cursor, bool SawResult)> ReadNewLinesAsync(
        string streamFile, long cursor, StringBuilder partialLine, CancellationToken cancellationToken)
    {
        if (!File.Exists(streamFile))
        {
            return (cursor, false);
        }

        await using FileStream stream = new(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= cursor)
        {
            return (cursor, false);
        }

        stream.Seek(cursor, SeekOrigin.Begin);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        char[] buffer = new char[8192];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    string line = partialLine.ToString();
                    partialLine.Clear();
                    if (StreamJsonParser.TryParseResult(line, out AgentResult _))
                    {
                        return (stream.Position, true);
                    }
                }
                else
                {
                    partialLine.Append(buffer[i]);
                }
            }
        }

        return (stream.Position, false);
    }

    /// <summary>
    /// Re-reads the whole stream file once a caller's incremental tail has seen a result
    /// line, and returns the session's actual combined result. Claude Code can print more than
    /// one top-level "result" event into a single stream — a background subagent's task
    /// notification produces its own short reaction leg's result ahead of the session's real
    /// terminal one — so <see cref="ReadNewLinesAsync"/> finding the first one only ever signals
    /// that a leg is done, never which line (or lines) actually account for the whole thing
    /// (discovery cc9b7aec, run 01a07574-4db1: a 76-minute, 302-turn session recorded 1,814
    /// output tokens because the daemon read a 5-turn reaction leg instead).
    ///
    /// <c>total_cost_usd</c> is cumulative to the moment a line is printed, so two lines
    /// sharing the exact same cost describe the same underlying spend rather than two separate
    /// legs — the reaction-leg shape above prints the parent session's own running cost onto a
    /// subagent's own result line. Lines are grouped by cost first, keeping the larger-turn-count
    /// line per group, so a reaction leg's own small usage figure is discarded rather than
    /// counted twice. What is left after that dedupe is one line per genuinely separate billing
    /// increment (the shape a resumed-in-place leg or a review-fix session's own trailing leg
    /// produces): usage and <c>num_turns</c> there are per-leg and disjoint, so both sum; cost
    /// only ever grows, so the largest wins; and <c>is_error</c>/the summary text come from the
    /// leg carrying that largest cost, since it is the chronologically final one. A stream
    /// holding exactly one result line, or several that all dedupe to one group, returns that
    /// line unchanged.
    /// </summary>
    internal static async Task<AgentResult> ReadFinalResultAsync(string streamFile, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        List<AgentResult> results = [];
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (StreamJsonParser.TryParseResult(line, out AgentResult candidate))
            {
                results.Add(candidate);
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException($"Stream file holds no parseable result line: {streamFile}");
        }

        if (results.Count == 1)
        {
            return results[0];
        }

        // A missing cost can't be deduped by value — grouped by index instead, so it stands as
        // its own leg rather than silently merging with every other line missing one.
        List<AgentResult> legs = [.. results
            .Select((result, index) => (result, key: result.CostUsd is { } cost ? (object)cost : index))
            .GroupBy(item => item.key, item => item.result)
            .Select(group => group.OrderByDescending(result => result.Turns ?? -1).First())];

        if (legs.Count == 1)
        {
            return legs[0];
        }

        AgentResult finalLeg = legs
            .OrderByDescending(result => result.CostUsd ?? -1m)
            .ThenByDescending(result => result.Turns ?? -1)
            .First();

        return finalLeg with
        {
            InputTokens = legs.Sum(result => result.InputTokens),
            CacheReadInputTokens = legs.Sum(result => result.CacheReadInputTokens),
            CacheCreationInputTokens = legs.Sum(result => result.CacheCreationInputTokens),
            OutputTokens = legs.Sum(result => result.OutputTokens),
            // A leg missing its own num_turns makes the sum itself unobserved, not partial —
            // StreamJsonParser.TryParseResult already refuses to guess a missing turns count as
            // zero, and summing only the legs that do carry one would read as an observed total
            // for a session that never happened (AGENTS.md's never-guess-at-unobserved-facts).
            Turns = legs.All(result => result.Turns.HasValue) ? legs.Sum(result => result.Turns ?? 0) : null,
        };
    }

    /// <summary>
    /// Whether a still-running session's own stream file has printed real evidence of work yet —
    /// any line reporting nonzero token usage, whether that is the session's own terminal
    /// "result" line or an intermediate turn (<see cref="StreamJsonParser.LineReportsNonzeroUsage"/>'s
    /// own doc). Used by <see cref="Hall9k.Daemon.Execution.LaunchHoldMonitor"/>'s own
    /// probe-still-running clear (task: a session that exits at once with no work done is treated
    /// as the node failing to launch sessions) — "the process is still alive" alone is not this
    /// feature's own evidence (criterion 3: "the first launch that records tokens clears the
    /// hold"); a process stuck retrying a failing API call stays alive without ever doing any work
    /// (independent pre-PR review, cycle 1, conformance lens). Reads the whole file rather than
    /// tailing from a cursor, the same one-shot read <see cref="ReadFinalResultAsync"/> uses,
    /// since this is called at most once per probe rather than on every poll.
    /// </summary>
    internal static async Task<bool> HasRecordedUsageAsync(string streamFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(streamFile))
        {
            return false;
        }

        await using FileStream stream = new(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (StreamJsonParser.LineReportsNonzeroUsage(line))
            {
                return true;
            }
        }

        return false;
    }
}
