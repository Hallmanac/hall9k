using Hall9k.Cli.Infrastructure;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Whether a row wants a human, and how loudly. An in-process display outcome, never persisted.
/// </summary>
internal enum AttentionLevel
{
    /// <summary>Nothing is owed: the platform has the next move, or the work is finished.</summary>
    None,

    /// <summary>
    /// Waiting on something that clears itself — a dependency still closing out, a blocker
    /// already retried and rebuilding. Rendered dim on purpose: the reader is meant to be able
    /// to consciously ignore it. Origin incident (2026-08-21): two dependency holds read as red
    /// NeedsHuman for hours after their blocker had been retried and was already rebuilding, and
    /// the human had to ask an orchestrator session what, if anything, was actually needed.
    /// </summary>
    WaitingHandled,

    /// <summary>Nothing moves until a human acts.</summary>
    NeedsYou,
}

/// <summary>
/// The third surface: needs-you or not, and when yes, the one-line cause and the lever
/// (Decisions Log #66, absorbing backlog 28). Everything here is read off records the platform
/// already keeps — park reasons, dependency-failure records, failure reasons, observed check and
/// thread counts — so the board and <c>h9k task show</c> cannot tell different stories.
/// <para>
/// A red row without a reason delegates the investigation to the human it was meant to spare,
/// and a reason without a next action is not done: <see cref="Lever"/> names a command wherever
/// the platform has one, and says plainly when it has none rather than inventing one.
/// </para>
/// </summary>
/// <param name="Level">How loudly the row is asking.</param>
/// <param name="Cause">One line, from recorded facts: why this row is looking at you.</param>
/// <param name="Lever">
/// The next act, as a command where one exists. Empty only when there is genuinely nothing to
/// type — a self-clearing wait, or a question the platform records but has no command to answer.
/// </param>
internal sealed record TaskAttention(AttentionLevel Level, string Cause = "", string Lever = "")
{
    /// <summary>A row that owes the reader nothing.</summary>
    public static readonly TaskAttention None = new(AttentionLevel.None);

    public bool NeedsYou => Level == AttentionLevel.NeedsYou;

    public bool HasCause => Cause.IsNotBlank();

    /// <summary>
    /// The column: yes or no, at a glance, before any of the words are read. Waiting-but-handled
    /// gets its own dim marker rather than sharing the red one, which is the whole distinction.
    /// </summary>
    public string Marker => Level switch
    {
        AttentionLevel.NeedsYou => "[red bold]needs you[/]",
        AttentionLevel.WaitingHandled => "[dim]waiting[/]",
        _ => string.Empty,
    };

    /// <summary>
    /// The cause and the lever as the one line they were promised to be. Both halves go through
    /// <see cref="ExternalText"/>: the cause may quote text an agent or a reviewer wrote, and the
    /// lever is a composed command only where the platform has one — where it has none, it is the
    /// pull request's URL, which <c>h9k task resolve --pr</c> stores exactly as it was typed. So
    /// neither half is Hall9k's own prose, and escaping markup alone would leave the terminal's
    /// control characters intact, which is the distinction <see cref="ExternalText"/> exists to
    /// draw.
    /// </summary>
    public string Markup
    {
        get
        {
            if (!HasCause)
            {
                return string.Empty;
            }

            string colour = Level == AttentionLevel.NeedsYou ? "red" : "dim";
            string cause = $"[{colour}]{ExternalText.OneLineMarkup(Cause)}[/]";
            return Lever.IsNotBlank() ? $"{cause} [dim]→[/] {ExternalText.OneLineMarkup(Lever)}" : cause;
        }
    }
}
