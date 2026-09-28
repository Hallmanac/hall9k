namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The single check behind "a node runs a project's verify gates only after its own operator has
/// accepted that exact gate set" (security review idea 6be68ee2, process-injection finding 1, the
/// local half): a project's <see cref="VerifyCommand"/> list is a shell command an owner-role
/// operator wrote and this node's own <c>h9k project accept-gates</c> vetted, but a REPLICATED
/// change to it — from a teammate's node, or from this owner's own other node — never passed
/// through that vetting here. Comparing <see cref="VerifyCommand.Fingerprint"/> of the accepted
/// list against the current one is what tells "still the vetted set" from "changed since this node
/// last looked", without re-running anything to find out.
/// <para>
/// Deliberately pure and free of every store, session, or host-coupled test class this platform's
/// dispatcher and runner tests are otherwise stuck with (Decisions Log: every dispatcher and runner
/// test class is <c>RequiresDocker</c>) — a plain unit test exercises the whole decision here, and
/// the dispatcher's own hold and the runner's own wait each become a single call against it rather
/// than a rule reimplemented at both call sites.
/// </para>
/// </summary>
public static class GateSetAcceptance
{
    /// <summary>
    /// The one answer: run the gates as they stand (<see cref="Proceed"/> true), or hold — the
    /// <see cref="Diff"/> is what changed, empty when nothing did.
    /// </summary>
    public readonly record struct Decision(bool Proceed, IReadOnlyList<GateDiffLine> Diff);

    /// <summary>Which side of the diff a line belongs to.</summary>
    public enum GateDiffLineKind
    {
        /// <summary>The gate at this position is identical on both sides.</summary>
        Unchanged,

        /// <summary>The accepted list carried this gate at this position; the current list does not (or carries a different one).</summary>
        Removed,

        /// <summary>The current list carries this gate at this position; the accepted list does not (or carried a different one).</summary>
        Added,
    }

    /// <summary>One line of the diff: which side it names, and the gate itself.</summary>
    public readonly record struct GateDiffLine(GateDiffLineKind Kind, VerifyCommand Gate);

    /// <summary>
    /// Whether <paramref name="current"/> — the gates a node is about to run — may proceed against
    /// <paramref name="accepted"/> — the last gate set an operator accepted on this node, or null
    /// when nothing has ever been accepted here.
    /// <para>
    /// An empty <paramref name="current"/> never holds: a project with no gates configured
    /// executes nothing, so there is nothing here for an unaccepted set to have smuggled in
    /// (independent pre-PR review of the origin finding). A zero-gate project that gains a gate by
    /// replication is the opposite case, and does hold, because <paramref name="current"/> is then
    /// non-empty and its fingerprint can never match an empty accepted list's.
    /// </para>
    /// <para>
    /// Order-sensitive, exactly as <see cref="VerifyCommand.Fingerprint"/> is: a reorder with no
    /// other change still holds, because the platform ran the gates in the recorded order and a
    /// reorder is as real a configuration change as a different command string.
    /// </para>
    /// </summary>
    public static Decision Decide(IReadOnlyList<VerifyCommand>? accepted, IReadOnlyList<VerifyCommand> current)
    {
        if (current.Count == 0)
        {
            return new Decision(true, []);
        }

        IReadOnlyList<VerifyCommand> acceptedList = accepted ?? [];
        return VerifyCommand.Fingerprint(acceptedList) == VerifyCommand.Fingerprint(current)
            ? new Decision(true, [])
            : new Decision(false, Diff(acceptedList, current));
    }

    /// <summary>
    /// Whether <c>h9k project accept-gates</c> may record acceptance of
    /// <paramref name="printed"/> — the gate list it just showed the operator — against
    /// <paramref name="currentAtCommitTime"/>, read fresh immediately before the append. The two
    /// disagreeing means the project's own gate set changed while the command was running (another
    /// <c>h9k project set --verify</c>, a replicated change landing mid-review), and recording
    /// acceptance anyway would record the operator's review of a set they were never actually shown
    /// — the command refuses instead and asks them to run it again.
    /// </summary>
    public static bool CanRecordAcceptance(
        IReadOnlyList<VerifyCommand> printed, IReadOnlyList<VerifyCommand> currentAtCommitTime) =>
        VerifyCommand.Fingerprint(printed) == VerifyCommand.Fingerprint(currentAtCommitTime);

    /// <summary>
    /// A positional line-by-line diff, not a minimal edit script: gate order is itself part of what
    /// was accepted, so lining the two lists up by index and calling out every position that
    /// disagrees is the honest reading of "what changed" rather than the shortest one.
    /// </summary>
    private static IReadOnlyList<GateDiffLine> Diff(IReadOnlyList<VerifyCommand> accepted, IReadOnlyList<VerifyCommand> current)
    {
        int max = Math.Max(accepted.Count, current.Count);
        List<GateDiffLine> lines = new(max);
        for (int index = 0; index < max; index++)
        {
            VerifyCommand? previous = index < accepted.Count ? accepted[index] : null;
            VerifyCommand? next = index < current.Count ? current[index] : null;
            if (previous is not null && previous == next)
            {
                lines.Add(new GateDiffLine(GateDiffLineKind.Unchanged, previous));
                continue;
            }

            if (previous is { } removed)
            {
                lines.Add(new GateDiffLine(GateDiffLineKind.Removed, removed));
            }

            if (next is { } added)
            {
                lines.Add(new GateDiffLine(GateDiffLineKind.Added, added));
            }
        }

        return lines;
    }
}
