namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human override of how many agent sessions this task's run may hold simultaneously
/// (Decisions Log #109, Brian's ruling 2026-08-30) — overriding the node's global
/// <c>SessionCapPerRun</c> default for this task alone. Deliberately state-agnostic: unlike
/// <see cref="TaskRevised"/>, which is Draft-only because every later state promises the contract
/// will not change out from under a node or a running agent, a session cap is not part of that
/// contract and is meant to be set "even mid-run" — the daemon reads the effective cap fresh at
/// each session dispatch, so a change here never disturbs a session already spawned.
/// <see cref="SessionCap"/> is <see langword="null"/> when this override clears the task back to
/// the node's global default (<c>h9k task set-session-cap &lt;id&gt; default</c>) — the same
/// "settable back to null" recovery every sibling override surface has (independent pre-PR
/// review, cycle 1, adversarial lens: this event originally could only ever raise the value,
/// never clear it back to letting the node's global default decide again).
/// </summary>
public sealed record TaskSessionCapOverridden(
    Guid Id,
    int? SessionCap,
    DateTimeOffset OverriddenAt,
    Guid OverriddenByOwnerId);
