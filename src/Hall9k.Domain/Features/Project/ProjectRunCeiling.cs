namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One project's own run ceiling against what it is carrying right now (Decisions Log #140):
/// <see cref="Cap"/> is <c>ProjectAggregate.MaxParallelTasks</c>, denominated in task runs to
/// match the node's own <c>DaemonOptions.MaxConcurrentTaskRuns</c> (Decisions Log #111), and
/// <see cref="LiveRuns"/> is how many of that project's runs the measuring node was carrying
/// when it looked. Pure, so the one rule both the dispatcher and <c>h9k status</c> read is
/// testable without a database.
/// <para>
/// A cap is a <em>ceiling</em>, never a reservation and never a share: nothing is set aside for
/// an idle project, and a project whose cap exceeds its share simply fills whatever the node
/// ceiling and the other projects' activity leave free. Its motive is pacing token spend — a
/// project capped at 1 serializes its work so a week's allocation spreads across the week —
/// which is why it counts runs rather than sessions: a run's review passes and fix sessions are
/// that run's own sessions (bounded per run by <c>DaemonOptions.SessionCapPerRun</c>), so they
/// cost its slot and never a second one.
/// </para>
/// <para>
/// Null <see cref="Cap"/> is the default and means uncapped — the node ceiling alone decides,
/// byte-for-byte the behaviour every project had before this existed. Zero is <em>pause</em>:
/// deliberate and sticky, for a project that is broken, budget-frozen, or shelved. Nothing in
/// the platform ever raises a cap on its own; the forgotten-pause footgun is answered with
/// visibility (<c>h9k status</c> names what is held and why) rather than automation.
/// </para>
/// </summary>
/// <param name="LiveRuns">
/// The project's live task runs as the measuring node observed them. An interactive claim
/// (<c>h9k task work</c>, <c>h9k task start</c>) is not among them: those carry the
/// ceiling-exempt <see cref="System.Guid.Empty"/> node-id sentinel, so they cost zero runs at
/// the project level exactly as they already do at the node level (Decisions Log #111).
/// </param>
/// <param name="Cap">This project's ceiling in runs; null is uncapped, 0 is paused.</param>
public sealed record ProjectRunCeiling(int LiveRuns, int? Cap)
{
    /// <summary>An uncapped project, the default every project starts from.</summary>
    public static ProjectRunCeiling Uncapped(int liveRuns) => new(liveRuns, null);

    /// <summary>Whether this project carries a cap at all; false means the node ceiling alone decides.</summary>
    public bool HasCap => Cap.HasValue;

    /// <summary>
    /// Deliberately stopped: no new claim while the cap stands, however idle the node is. Told
    /// apart from <see cref="AtCap"/> everywhere it is reported, because raising a full cap and
    /// resuming a paused project are the same lever pointed at different problems.
    /// </summary>
    public bool IsPaused => Cap == 0;

    /// <summary>Nothing more of this project's may start until one of its runs finishes.</summary>
    public bool AtCap => Cap is { } cap && LiveRuns >= cap;

    /// <summary>
    /// A project carrying more runs than its cap allows — which the dispatcher cannot cause but
    /// can observe: a review park a human resolved, or a run startup adoption re-entered, hands
    /// a worktree back to a session tree that had released its slot, and a cap lowered while
    /// runs are live leaves the ones already running alone (a cap gates claims, it never kills
    /// work). Reported as what it is rather than as "3 of 1", the same reading
    /// <c>NodeLoad</c>/<c>DispatchPressure</c> already give an over-ceiling node.
    /// </summary>
    public bool OverCap => Cap is { } cap && LiveRuns > cap;

    /// <summary>
    /// Whether one more of this project's runs may be claimed, given how many the current sweep
    /// has already claimed for it. Asked per candidate rather than once per sweep, because the
    /// sweep's own claims fill the cap as it goes: at a cap of 2 with one run live, the first
    /// candidate is admitted and the second is not.
    /// </summary>
    public bool Admits(int claimedThisSweep) =>
        Cap is not { } cap || LiveRuns + claimedThisSweep < cap;
}
