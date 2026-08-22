using Hall9k.Cli.Infrastructure;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Whether the agent process a phase would describe was actually seen to be there. An
/// in-process display outcome, never persisted (AGENTS.md: enums only for unpersisted
/// outcomes).
/// </summary>
internal enum SessionLiveness
{
    /// <summary>The phase describes no session at all — gates running, a pull request being watched.</summary>
    NotApplicable,

    /// <summary>A session is recorded and its process answered to its recorded identity.</summary>
    Alive,

    /// <summary>A session is recorded and its process is gone: the run believes it is running, the machine disagrees.</summary>
    Gone,

    /// <summary>
    /// A session is recorded and this machine cannot honestly answer for it — the run belongs
    /// to another node, or only half its identity was recorded (a bare pid, log #2). Said out
    /// loud rather than assumed either way (the never-guess rule).
    /// </summary>
    Unobserved,
}

/// <summary>
/// What the machinery is doing right now, for a Working or Delivered row: the second of the
/// board's three surfaces (Decisions Log #66). Composed from the run's records plus an
/// observation of the recorded process, never from the run state alone — a run sits in
/// UnderReview both while a reviewer reads and while nothing at all is running, and the
/// difference is the whole point.
/// <para>
/// Origin incident (2026-08-22): the board said ClosingOut while a fix agent was actively
/// editing the worktree, and the orchestrator nearly rewrote history under it. A phase that
/// claims a session is doing something without observing the process is the same defect
/// pointing the other way, so <see cref="Liveness"/> qualifies every phase that names one.
/// </para>
/// </summary>
/// <param name="Text">The phase itself, in the words a human would use — "building", "gates", "watching PR #24".</param>
/// <param name="Liveness">What was observed about the session the text describes.</param>
/// <param name="Detail">
/// The recorded facts that make the phase specific: which gates failed, which lens is still
/// reading, how many threads are unresolved. Empty when the phase says everything already.
/// </param>
internal sealed record TaskPhase(string Text, SessionLiveness Liveness, string Detail = "")
{
    /// <summary>A row with no phase at all: nothing is running and nothing is being watched.</summary>
    public static readonly TaskPhase None = new(string.Empty, SessionLiveness.NotApplicable);

    public bool HasPhase => Text.IsNotBlank();

    /// <summary>
    /// The phase as one line: what is happening, what the records say about it, and — only
    /// when a session is named — what was actually observed of it. A phase that names no
    /// session says nothing about liveness rather than reassuring the reader about a process
    /// that does not exist.
    /// <para>
    /// The detail quotes text Hall9k did not author — a failing check is named by whoever named
    /// the workflow job, read straight off <c>gh pr view</c> — so it is sanitised as well as
    /// escaped. <c>EscapeMarkup()</c> alone neutralises Spectre's syntax and not the terminal's:
    /// a check name carrying a newline would break the one-line guarantee this line's layout is
    /// measured against, and one carrying an escape sequence would repaint the rows above it.
    /// </para>
    /// </summary>
    public string Markup
    {
        get
        {
            if (!HasPhase)
            {
                return string.Empty;
            }

            List<string> parts = [$"[dim]{ExternalText.OneLineMarkup(Text)}[/]"];
            if (Detail.IsNotBlank())
            {
                parts.Add($"[dim]{ExternalText.OneLineMarkup(Detail)}[/]");
            }

            if (LivenessMarkup is { } observed)
            {
                parts.Add(observed);
            }

            return string.Join(" [dim]·[/] ", parts);
        }
    }

    /// <summary>
    /// What was observed of the session, in the reader's terms. Alive is stated rather than
    /// left implicit, because "session alive" is exactly the sentence the orchestrator needed
    /// to read before touching the worktree. A phase that names no session says nothing here
    /// rather than reassuring a reader about a process that does not exist.
    /// </summary>
    private string? LivenessMarkup => Liveness switch
    {
        SessionLiveness.Alive => "[dim]session alive[/]",
        SessionLiveness.Gone => "[red]the recorded process is gone[/]",
        SessionLiveness.Unobserved => "[dim]session liveness not observed here[/]",
        _ => null,
    };
}
