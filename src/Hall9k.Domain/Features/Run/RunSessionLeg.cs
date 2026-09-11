using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which of a run's sessions an error-result retry (task: a session that reports an error
/// result is retried once in place) applies to — the same primary-session-versus-review-loop
/// split <c>TokenBudgetRetryEngine</c> already draws for token-budget recovery
/// (<see cref="Events.RunBudgetExhausted"/>), named here so <see cref="Events.RunSessionErrorRetried"/>
/// can say which leg without a reader rederiving it from <see cref="ReviewPhase"/>.
/// </summary>
[JsonConverter(typeof(RunSessionLegJsonConverter))]
public sealed record RunSessionLeg
{
    /// <summary>The primary agent session that writes the feature.</summary>
    public static readonly RunSessionLeg Build = new("Build");

    /// <summary>One review pass of the pre-PR loop (Decisions Log #59) — <see cref="Events.RunSessionErrorRetried.Lens"/> says which.</summary>
    public static readonly RunSessionLeg ReviewPass = new("ReviewPass");

    /// <summary>The session that applies review findings in the run's worktree.</summary>
    public static readonly RunSessionLeg Fix = new("Fix");

    /// <summary>
    /// The fix session dispatched over a human's own <c>h9k review resolve --needs-fixes</c>
    /// reason rather than the review loop's own automated findings document — its own leg, not
    /// <see cref="Fix"/> (independent pre-PR review, conformance lens): the automatic
    /// uncommitted-work recovery bound is scoped per leg specifically so an earlier leg spending
    /// its own attempt never leaves a later, different leg on the same run with none of its own,
    /// and a review-fix round and a human-resolved round are different legs by that same
    /// reasoning — collapsing them let a run's ordinary review-fix leg spend the one automatic
    /// recovery a later human-resolved-fix leg on that same run still owed itself.
    /// </summary>
    public static readonly RunSessionLeg HumanResolvedFix = new("HumanResolvedFix");

    /// <summary>
    /// The narrow recovery session dispatched when a plain rebase onto the base branch conflicts
    /// immediately before the mandatory final full pass (task: a run rebases its branch onto the
    /// current base branch). Its own leg, not <see cref="Fix"/>: an error-retry on this leg must
    /// clear the rebase-recovery session's own active-session fields and redispatch through
    /// <see cref="ReviewPhase.RebaseRecoveryNeeded"/>, never the ordinary fix session's.
    /// </summary>
    public static readonly RunSessionLeg RebaseRecovery = new("RebaseRecovery");

    /// <summary>
    /// The narrow repair session dispatched when the Settling phase's own mandatory gate fails on
    /// a tree whose most recent recorded rebase was real (task: a pre-final-pass rebase that
    /// applies cleanly but breaks the mandatory gate gets a repair lap inside the same run instead
    /// of failing it). Its own leg for the one-retry-per-leg/cycle/lens error-result bookkeeping
    /// (<see cref="RunAggregate.HasRetriedSessionError"/>), even though
    /// <see cref="Events.SettlingGateRepairDispatched"/>'s own doc says this session shares
    /// <see cref="RebaseRecovery"/>'s dispatch mechanics and worktree-recovery eligibility: reusing
    /// that same leg for the error-retry key too let a rebase-recovery session's own error retry in
    /// a review cycle spend the identical (leg, cycle) key a later Settling-gate repair session in
    /// that same cycle needed for its own first retry (independent pre-PR review, cycle 1, both
    /// lenses).
    /// </summary>
    public static readonly RunSessionLeg SettlingGateRepair = new("SettlingGateRepair");

    /// <summary>Not recognized. Serializes as an empty string.</summary>
    public static readonly RunSessionLeg Unknown = new("");

    public string Value { get; }

    private RunSessionLeg(string value) => Value = value;

    public static implicit operator string(RunSessionLeg? leg) => leg?.Value ?? string.Empty;

    public static implicit operator RunSessionLeg(string? value) => value.IsBlank() ? Unknown : new RunSessionLeg(value);

    public bool Equals(RunSessionLeg? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunSessionLegJsonConverter : JsonConverter<RunSessionLeg>
    {
        public override RunSessionLeg Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, RunSessionLeg value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
