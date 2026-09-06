using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// An operator holding a task interactively (<c>h9k task work</c>) dispatched a contractor build
/// agent to do this one phase while staying the arbiter (<c>h9k task delegate</c>, task
/// 15f889e3-h9k, design ruling R6, idea fcaded0b's design rulings, Take the Wheel epic 9272e514's
/// slice 10) — distinct from <c>h9k task handback</c>, which ends interactive mode and hands the
/// task back to the machine, this reuses the same run and worktree the interactive claim already
/// holds and never touches the task's claim, its assignment, or its interactive-mode flag.
/// </summary>
/// <param name="Id">The run this contractor session was dispatched against — the interactive claim's own run.</param>
/// <param name="Note">
/// The operator's own handoff, in the blocker-handoff mold: what was attempted, what is
/// deliberate versus abandoned, what latitude is granted. Carried verbatim into the contractor's
/// starting prompt and recorded here so the decision to delegate, and why, survives this session.
/// </param>
/// <param name="SessionName">
/// The contractor's mesh-visible name (<c>--name</c>, <c>claude agents --json</c>) — the
/// documented slice-1 <c>&lt;task-shortid&gt;-build</c> shape, identical across every delegation
/// on this run.
/// </param>
/// <param name="SessionFileKey">
/// Unique per delegation (independent pre-PR review, cycle 1, both lenses): this run's prompt,
/// stream, settings and stderr files are named from this, never <paramref name="SessionName"/>,
/// because a second delegation sharing <paramref name="SessionName"/>'s own value would truncate
/// the first contractor's transcript, handoff and recorded token usage the moment
/// <c>HeadlessLaunch.SpawnDetached</c> opened its own files for writing.
/// </param>
/// <param name="Model">
/// The contractor's own resolved build-role model (<c>TaskStartCommand.ResolveBuildModelAsync</c>),
/// recorded the same way <see cref="RunDispatched.Model"/> and <see cref="TokensRecorded.Model"/>
/// are — an observed fact, never inferred from the interactive claim's own <c>run.Model</c>, which
/// is hard-wired to the human-interactive tier and says nothing about what this contractor ran on
/// (independent pre-PR review, cycle 1, conformance lens: the contractor's spend was otherwise
/// recorded under the wrong model bucket, with no field anywhere naming the one it actually used).
/// </param>
public sealed record RunPhaseDelegated(
    Guid Id, string Note, DateTimeOffset DelegatedAt, Guid DelegatedByOwnerId, string SessionName,
    string SessionFileKey, AgentModel Model);
