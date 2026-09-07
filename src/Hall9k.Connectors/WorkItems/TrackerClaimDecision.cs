using Hall9k.Domain.Features.Tasks.Documents;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// What a <c>tracker-assignee</c> claim gate concluded about one task (idea 64c75e43). An
/// in-process display-and-decision outcome, never persisted as itself (AGENTS.md: enums only for
/// unpersisted in-process outcomes) — what lands in Postgres is
/// <see cref="TrackerClaimHold"/>, the published measurement behind a held task's own line.
/// </summary>
public enum TrackerClaimVerdict
{
    /// <summary>Nothing to check: the project's gate is off, or this task carries no gated reference.</summary>
    NotGated,

    /// <summary>The tracker shows the item assigned to this install's own identity. The claim proceeds.</summary>
    Assigned,

    /// <summary>The tracker shows the item assigned to somebody else. The claim waits.</summary>
    HeldByOther,

    /// <summary>The tracker shows the item assigned to nobody at all. The claim waits.</summary>
    Unassigned,

    /// <summary>The tracker could not be read, so the gate fails closed and the claim waits.</summary>
    Unreadable,
}

/// <summary>
/// One gate check's whole answer, and the one place its sentences are written (idea 64c75e43):
/// the warning <c>h9k task assign</c> prints, the identical refusal <c>h9k task work</c> and
/// <c>h9k task start</c> exit 70 with, the line the daemon logs, and the one-clause reason a held
/// task's row reads on <c>h9k status</c>, <c>h9k task show</c> and <c>h9k project show</c> — in
/// the same voice <c>DispatchPressure.ReasonLine</c> uses for the concurrency ceiling.
/// <para>
/// Both the dispatcher — which has just made the read — and the CLI — which is reading the
/// measurement the dispatcher published (<see cref="TrackerClaimHold"/>, via
/// <see cref="FromHold"/>) — compose from this same record, so the board and the daemon cannot
/// drift into two different sentences about the same hold.
/// </para>
/// </summary>
/// <param name="Verdict">What the read concluded.</param>
/// <param name="Provider">Whose item is gating the claim: <c>jira</c> or <c>github</c>.</param>
/// <param name="ItemKey">The item as the people who filed it say it out loud: <c>PROJ-123</c>, or <c>owner/repo#42</c>.</param>
/// <param name="ItemUrl">The card or issue in a browser, so the lever is one click; null when the reference could not be turned into one.</param>
/// <param name="Identity">This install's own tracker identity, as the tracker reported it; null when that read itself failed.</param>
/// <param name="Holder">Who the tracker says holds the item, as a human reads it; null when nobody does, or when nothing could be read.</param>
/// <param name="Error">The tracker's own sentence, verbatim, when the item could not be read at all.</param>
/// <param name="AuthenticationRefusal">Whether <paramref name="Error"/> was the tracker refusing these credentials rather than failing to answer — what tells a token problem apart from an outage.</param>
/// <param name="Lever">What ends the hold, in the imperative: assign yourself in the tracker, renew the token with the command printed, restore the connection, or wait out the outage.</param>
/// <param name="ObservedAt">When this install read the tracker. Never the tracker's own time: the assignee field carries none.</param>
/// <param name="Assignee">
/// The matching holder when the gate passed — what <c>TrackerAssignmentObserved</c> records as
/// the evidence the claim rested on. Null on every other verdict, since there is no match to name.
/// </param>
public sealed record TrackerClaimDecision(
    TrackerClaimVerdict Verdict,
    string Provider,
    string ItemKey,
    Uri? ItemUrl,
    string? Identity,
    string? Holder,
    string? Error,
    bool AuthenticationRefusal,
    string? Lever,
    DateTimeOffset ObservedAt,
    TrackerAssignee? Assignee = null)
{
    /// <summary>Nothing was checked, because nothing needed checking — the gate is off, or the task carries no gated item.</summary>
    public static TrackerClaimDecision NotGated { get; } = new(
        TrackerClaimVerdict.NotGated, string.Empty, string.Empty, null, null, null, null, false, null,
        DateTimeOffset.MinValue);

    /// <summary>Whether the claim may proceed. Only an observed assignment to this identity passes; everything else waits.</summary>
    public bool Passes => Verdict is TrackerClaimVerdict.NotGated or TrackerClaimVerdict.Assigned;

    /// <summary>Whether this check produced a hold worth publishing and explaining at all.</summary>
    public bool Holds => !Passes;

    /// <summary>The tracker as a human names it in a sentence.</summary>
    private string Tracker => Provider == "jira" ? "Jira" : "GitHub";

    /// <summary>
    /// The item as it is spoken in a sentence bound for a terminal. <see cref="ItemKey"/> itself
    /// is the reference recorded on the task, verbatim — whatever <c>h9k task link-jira</c> was
    /// handed, which is not necessarily a key this platform could parse — so it is relayed text
    /// on the way out, sanitised exactly where it is displayed and nowhere earlier: the gate
    /// parses and compares the raw value, and folding a control character out of it before that
    /// would let a reference read as a card it does not name (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    private string Item => TrackerAssignee.Rendered(ItemKey);

    /// <summary>
    /// The one sentence every door says: what the tracker showed, that this project's claim gate
    /// is what turns that into a wait, and the lever that ends it. <c>h9k task assign</c> prints
    /// it as a warning and assigns anyway — the tracker stays the single go signal, so the task
    /// simply waits in the queue — while <c>h9k task work</c> and <c>h9k task start</c> refuse
    /// with it, word for word, because there the human is asking to start the work now.
    /// </summary>
    public string RefusalLine => Verdict switch
    {
        TrackerClaimVerdict.HeldByOther =>
            $"{Tracker} shows {Item} assigned to {Holder}, not to this install{IdentityClause}. This "
            + "project's claim gate is tracker-assignee, so the tracker's own assignment is the one act "
            + "that hands out work here — nothing on this install claims the task while somebody else "
            + $"holds the item. {Lever}",
        TrackerClaimVerdict.Unassigned =>
            $"{Tracker} shows {Item} assigned to nobody. This project's claim gate is tracker-assignee, "
            + "so the tracker's own assignment is the one act that hands out work here — nothing on this "
            + $"install claims the task until the item is assigned to it{IdentityClause}. {Lever}",
        TrackerClaimVerdict.Unreadable =>
            $"{Tracker} could not be read, so this project's tracker-assignee claim gate fails closed and "
            + $"{Item} is not claimed here. {Tracker} reported: {Error} {Lever}",
        _ => string.Empty,
    };

    /// <summary>
    /// The same hold in the one-clause voice <c>DispatchPressure.ReasonLine</c> uses for the
    /// concurrency ceiling: the cause, for the line under a queued row. The lever belongs to the
    /// fuller <see cref="RefusalLine"/> a command prints, not to a row in a table — except on the
    /// unreadable path, where the error and what ends it are the whole point of the line and a
    /// reader has no other way to see them.
    /// </summary>
    public string ReasonLine => Verdict switch
    {
        TrackerClaimVerdict.HeldByOther =>
            $"waiting for {Tracker} to show {Item} assigned to you — {Holder} holds it",
        TrackerClaimVerdict.Unassigned =>
            $"waiting for {Tracker} to show {Item} assigned to you — nobody holds it",
        TrackerClaimVerdict.Unreadable =>
            $"waiting for {Tracker} to show {Item} assigned to you — {Tracker} could not be read, so the "
            + $"gate fails closed: {Error} {Lever}",
        _ => string.Empty,
    };

    /// <summary>
    /// Who this install is, named only when the tracker actually said — the never-guess rule
    /// applied to the one field a failed identity read leaves empty (AGENTS.md).
    /// </summary>
    private string IdentityClause =>
        Identity is { Length: > 0 } identity ? $" ({TrackerAssignee.Rendered(identity)})" : string.Empty;

    /// <summary>This check as the measurement the daemon publishes for the CLI surfaces to read.</summary>
    public TrackerClaimHold ToHold(Guid taskId, Guid nodeId, string machineName) => new()
    {
        Id = TrackerClaimHold.KeyFor(taskId, nodeId),
        TaskId = taskId,
        NodeId = nodeId,
        MachineName = machineName,
        Provider = Provider,
        ItemKey = ItemKey,
        ItemUrl = ItemUrl?.ToString(),
        Identity = Identity,
        Holder = Holder,
        Error = Error,
        AuthenticationRefusal = AuthenticationRefusal,
        Lever = Lever,
        ObservedAt = ObservedAt,
    };

    /// <summary>
    /// The published measurement read back as the decision that produced it, so a CLI surface
    /// composes the identical sentence the daemon would have. The verdict is reconstructed from
    /// the same two fields that distinguished it when it was written: an error means the read
    /// failed, and a holder means somebody else has it. A passing check publishes no hold at all
    /// (the dispatcher deletes any it finds), so there is no <see cref="TrackerClaimVerdict.Assigned"/>
    /// document to read back.
    /// </summary>
    public static TrackerClaimDecision FromHold(TrackerClaimHold hold) => new(
        hold.Error is not null
            ? TrackerClaimVerdict.Unreadable
            : hold.Holder is { Length: > 0 }
                ? TrackerClaimVerdict.HeldByOther
                : TrackerClaimVerdict.Unassigned,
        hold.Provider,
        hold.ItemKey,
        hold.ItemUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed : null,
        hold.Identity,
        hold.Holder,
        hold.Error,
        hold.AuthenticationRefusal,
        hold.Lever,
        hold.ObservedAt);
}
