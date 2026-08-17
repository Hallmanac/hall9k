using System.Text;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Incremental tail over a session's stream.jsonl: reads only the bytes past the caller's
/// cursor, buffers a trailing partial line across calls, and stops at the terminal result
/// event. Shared by RunSupervisor (main session) and ReviewEngine (review and fix legs)
/// so neither re-reads a long transcript from the start on every poll.
/// </summary>
internal static class StreamTailReader
{
    internal static async Task<(long Cursor, bool SawResult, AgentResult? Result)> ReadNewLinesAsync(
        string streamFile, long cursor, StringBuilder partialLine, CancellationToken cancellationToken)
    {
        if (!File.Exists(streamFile))
        {
            return (cursor, false, null);
        }

        await using FileStream stream = new(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= cursor)
        {
            return (cursor, false, null);
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
                    if (StreamJsonParser.TryParseResult(line, out AgentResult result))
                    {
                        return (stream.Position, true, result);
                    }
                }
                else
                {
                    partialLine.Append(buffer[i]);
                }
            }
        }

        return (stream.Position, false, null);
    }
}
