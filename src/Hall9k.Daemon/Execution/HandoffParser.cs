using System.Text;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Reads the run's handoff off the agent's own session-end result (Decisions Log #36).
/// <para>
/// This extends the result parsing the daemon already does rather than adding a session: the
/// terminal result event's summary text is captured today, and the handoff is a marked block
/// inside it. A separate summarizer session would be both more expensive and worse informed —
/// the agent that just did the work is the one that knows what it deliberately left undone.
/// </para>
/// <para>
/// The marker is matched the way the review loop matches its own (log #23): the LAST marker
/// wins, because agents sometimes quote the instructions before answering. Everything after
/// it is the handoff, since the prompt asks for it as the final thing in the message.
/// </para>
/// </summary>
public static class HandoffParser
{
    /// <summary>
    /// The marker line the build prompt asks for. Declared here, and used by the prompt
    /// builder from here, so the instruction and the parser can never drift apart.
    /// </summary>
    public const string Marker = "HANDOFF:";

    /// <summary>
    /// The event's text budget. Streams carry milestones (log #6), so the handoff that travels
    /// is bounded while the run directory's handoff.md keeps whatever the agent actually wrote.
    /// The prompt asks for brevity; this is the backstop for when it is not honored.
    /// </summary>
    public const int MaxEventLength = 4000;

    /// <summary>
    /// The handoff block, or null when the session's result carried none. Null is a real
    /// answer here — a session that ended without a handoff is recorded as having ended
    /// without one, never as an empty one.
    /// </summary>
    public static string? Parse(string? summary)
    {
        if (summary.IsBlank())
        {
            return null;
        }

        string[] lines = summary.Split('\n');
        int start = -1;
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].TrimStart().StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
            {
                start = index;
            }
        }

        if (start < 0)
        {
            return null;
        }

        string firstLine = lines[start].TrimStart()[Marker.Length..];
        string handoff = string.Join('\n', [firstLine, .. lines[(start + 1)..]]).Trim();
        return handoff.IsBlank() ? null : handoff;
    }

    /// <summary>
    /// The handoff cut to the event's budget. An over-long handoff is truncated with the
    /// truncation stated and the whole copy named, because a silently clipped handoff reads
    /// exactly like a complete one (the AGENTS.md never-guess rule, applied to omission).
    /// </summary>
    public static string BoundForEvent(string handoff, string runDirectory) => BoundForEvent(handoff, [runDirectory]);

    /// <summary>
    /// The same bound over a composed handoff, naming every run directory the text was drawn
    /// from. Composition is why the plural matters: truncation can cut a later run's section
    /// away entirely, so the note has to say where each whole copy still lives.
    /// </summary>
    public static string BoundForEvent(string handoff, IReadOnlyList<string> runDirectories) =>
        handoff.Length <= MaxEventLength
            ? handoff
            : handoff[..MaxEventLength].TrimEnd()
                + $"\n\n[Truncated at {MaxEventLength} characters. The whole handoff"
                + (runDirectories.Count == 1 ? " is at " : "s are at ")
                + string.Join(", ", runDirectories.Select(RunPaths.HandoffFile)) + ".]";

    /// <summary>One run's authored handoff, on its way into the task's composed one.</summary>
    public sealed record RunHandoff(Guid RunId, string RunDirectory, string Text);

    /// <summary>
    /// The handoff the task hands down, composed from every run that carried its pull request
    /// (Decisions Log #36), in dispatch order.
    /// <para>
    /// A handoff belongs to a run, but what a dependent wants is what the blocker <em>task</em>
    /// ended up doing — and since decision #22 makes review follow-ups automatic, essentially
    /// every merged pull request is the work of an original run plus at least one follow-up.
    /// Handing down only the run that observed the merge would hand down the thread-resolution
    /// pass and leave the run that wrote the feature unread. Dispatch order puts the run that
    /// built the thing first, because that is the sentence a dependent needs most.
    /// </para>
    /// <para>
    /// A single contributor renders as its own text with no framing: the overwhelmingly common
    /// shape should not pay for the rare one.
    /// </para>
    /// </summary>
    public static string Compose(IReadOnlyList<RunHandoff> handoffs)
    {
        if (handoffs.Count == 1)
        {
            return handoffs[0].Text;
        }

        StringBuilder composed = new();
        composed.AppendLine(
            $"This task's pull request was carried by {handoffs.Count} runs. Each one's handoff follows, in");
        composed.AppendLine("dispatch order — the run that opened the work first, its follow-ups after it.");

        int position = 0;
        foreach (RunHandoff handoff in handoffs)
        {
            position++;
            composed.AppendLine();
            composed.AppendLine($"[Run {position} of {handoffs.Count} · {ShortId(handoff.RunId)}]");
            composed.AppendLine();
            composed.AppendLine(handoff.Text);
        }

        return composed.ToString().TrimEnd();
    }

    /// <summary>The eight-character run id humans read on every other h9k surface.</summary>
    private static string ShortId(Guid id) => id.ToString("N")[^8..];
}
