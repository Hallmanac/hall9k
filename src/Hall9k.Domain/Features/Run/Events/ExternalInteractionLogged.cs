namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A dispatched agent's own record of an interaction with anything outside its session — another
/// agent session reached through the mesh, a human steering it mid-pass, an external API the
/// prompt did not already route through an observation-gate command of its own. The invariant
/// this event exists to hold (the 2026-09-01 escape-hatch ruling, idea fcaded0b's design rulings
/// 4 and 5): every such interaction is logged unconditionally, even one the interacting party
/// asked the agent to keep quiet, and <see cref="HumanDirected"/> says plainly whether a human —
/// not the agent's own judgment — directed the interaction or its outcome, so the record never
/// reports a human's own call as though it were the agent's independent decision.
/// <para>
/// Landed through <c>h9k task log-interaction</c> (the agent-facing, observation-gate-style CLI
/// surface AGENTS.md's CLI command standards ask for) rather than left to transcript prose, so
/// what reaches this stream is structured: the CLI command is the only writer, and it appends
/// exactly what it was told without verifying the interaction against anything external — there
/// is nothing outside the platform's own channels to read it back from, which is the sense in
/// which this logging is best-effort rather than an enforcement mechanism. A human-directed entry
/// on this task's stream is what <c>Hall9k.Daemon.Review.ReviewEngine.LoadPriorRulingsAndInteractionsAsync</c>
/// hands forward into later review passes, through the same settled-rulings prompt
/// surface (Decisions Log #88, <c>Hall9k.Daemon.Execution.AgentPromptBuilder.AppendSettledRulings</c>)
/// a human's own <c>h9k review resolve</c> verdict already rides in on — a mid-pass human
/// directive teaches a later fresh-context pass exactly as a park ruling does.
/// </para>
/// </summary>
/// <param name="RunId">The run this interaction happened on — this event's own stream id.</param>
/// <param name="LoggedAt">When the command recorded it, not when the interaction itself happened.</param>
/// <param name="Party">
/// Who or what outside this session was interacted with, in the agent's own words — another
/// agent session, a human reached through the session mesh, an external API. Free text: the
/// platform models nothing about the mesh's own addressing scheme here.
/// </param>
/// <param name="Summary">What happened: what was said or asked, and what the session did about it.</param>
/// <param name="HumanDirected">
/// True when a human, not this session's own judgment, directed the interaction or its outcome.
/// The one fact this whole event exists to keep honest: a later reader must never find an outcome
/// reported as the agent's own when a human actually called it.
/// </param>
/// <param name="Reason">
/// The human's own instruction or reason, required (by the CLI command, not this record) whenever
/// <see cref="HumanDirected"/> is true. Optional otherwise — an interaction the agent initiated on
/// its own can still be worth logging with no human reason attached to it.
/// </param>
/// <param name="LoggedByOwnerId">The node's own owner at the moment this was recorded, for authorship parity with every other agent-facing write.</param>
public sealed record ExternalInteractionLogged(
    Guid RunId,
    DateTimeOffset LoggedAt,
    string Party,
    string Summary,
    bool HumanDirected,
    string? Reason,
    Guid LoggedByOwnerId);
