using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which pre-PR review stages a run gets (task: the review pipeline's stage composition becomes
/// configuration recorded per run). Resolved once, at dispatch (<see cref="Events.RunDispatched"/>,
/// via <see cref="ReviewStageCompositionResolver"/>), task &gt; project &gt; node &gt; compiled
/// default — the same strict hierarchy <c>Hall9k.Daemon.Review.ReviewCapResolver</c> already walks
/// for the four review-cycle caps — and then frozen for that run's whole lifetime. Unlike a cap,
/// which is a number a live <c>ReviewEngine.DriveAsync</c> loop can safely re-check every
/// iteration, a composition change reshapes which tracks exist and when the mandatory final pass
/// runs; re-resolving it mid-run could reopen or drop a track the aggregate's own bookkeeping
/// (<c>RunAggregate.ConcludedReviewTracks</c>, <c>RunAggregate.CurrentCycleMode</c>) has already
/// committed to. A mid-run setting change therefore reaches only the NEXT run this task
/// dispatches, never the one already in flight — this is the one place this setting's own
/// resolution discipline deliberately diverges from the caps' live-recheck one.
/// <para>
/// <see cref="FullPipeline"/> is byte-for-byte what shipped before this setting existed: both
/// lenses, every cycle, the mandatory <see cref="ReviewMode.FinalFullPass"/> immediately before
/// merge (Decisions Log #92). <see cref="AdversarialOnly"/> and <see cref="ConformanceOnly"/> open
/// one track instead of two — the other lens never dispatches, on any cycle including the final
/// one. <see cref="SkipFinalPass"/> keeps both tracks but waives the mandatory FinalFullPass read
/// immediately before merge; the build/test gate still runs full regardless — this setting only
/// ever touches the review pass, never the gate. <see cref="None"/> skips pre-PR review
/// altogether: no reviewer ever reads the diff, and the run goes straight from the gates to the
/// pull request.
/// </para>
/// <para>
/// <see cref="SkipFinalPass"/> and <see cref="None"/> remove Decisions Log #92's own guarantee
/// (nothing merges on scoped-review-or-no-review alone); dropping a lens outright
/// (<see cref="AdversarialOnly"/>, <see cref="ConformanceOnly"/>, <see cref="None"/> again) removes
/// whatever that lens alone catches that the other's own attention budget does not sample
/// (Decisions Log #59, #63 — the origin incident on PR #21, four Copilot passes surfacing an
/// injection risk conformance alone had missed across several cycles, is the adversarial lens's
/// own case in point). <see cref="ReviewStageCompositionValidation"/> is what refuses setting any
/// of these without an explicit acknowledgment at set time — the platform advises, the human
/// overrides, never a silent degrade.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewStageCompositionJsonConverter))]
public sealed record ReviewStageComposition
{
    /// <summary>Both lenses, every cycle, the mandatory final pass — unchanged from today's shipped pipeline.</summary>
    public static readonly ReviewStageComposition FullPipeline = new("FullPipeline");

    /// <summary>Only the adversarial track ever dispatches, on every cycle including the mandatory final one.</summary>
    public static readonly ReviewStageComposition AdversarialOnly = new("AdversarialOnly");

    /// <summary>Only the conformance track ever dispatches, on every cycle including the mandatory final one.</summary>
    public static readonly ReviewStageComposition ConformanceOnly = new("ConformanceOnly");

    /// <summary>Both tracks run, but the mandatory FinalFullPass immediately before merge never dispatches.</summary>
    public static readonly ReviewStageComposition SkipFinalPass = new("SkipFinalPass");

    /// <summary>No pre-PR review at all — the run settles straight off the gates VerificationRunner already ran.</summary>
    public static readonly ReviewStageComposition None = new("None");

    /// <summary>Not recognized, or no override at this level. Serializes as an empty string.</summary>
    public static readonly ReviewStageComposition Unknown = new("");

    public string Value { get; }

    private ReviewStageComposition(string value) => Value = value;

    public static implicit operator string(ReviewStageComposition? composition) => composition?.Value ?? string.Empty;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="Project.BacklogPolicy"/> convention: a value
    /// built this way can carry anything, which is what lets the deciders be the one place that
    /// actually enforces the closed set. Use <see cref="Parse"/> for a human's own input and
    /// <see cref="FromInput"/> for a value already vetted at another level.
    /// </summary>
    public static implicit operator ReviewStageComposition(string? value) =>
        value.IsBlank() ? Unknown : new ReviewStageComposition(value);

    /// <summary>
    /// Lenient mapping for a value already on the stream or read off another level; unrecognized
    /// reads as Unknown — including the word "default", which is not a canonical alias here but
    /// instead the clearing word a project or task level's own CLI command intercepts before ever
    /// reaching this method (the <see cref="Shared.ValueObjects.AgentModel"/> convention): the
    /// node level has no clearing word at all, the same as the four review-cycle caps.
    /// </summary>
    public static ReviewStageComposition FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fullpipeline" or "full-pipeline" or "full" => FullPipeline,
        "adversarialonly" or "adversarial-only" => AdversarialOnly,
        "conformanceonly" or "conformance-only" => ConformanceOnly,
        "skipfinalpass" or "skip-final-pass" => SkipFinalPass,
        "none" => None,
        _ => Unknown,
    };

    /// <summary>
    /// The strict form a human's own input goes through (the <see cref="Project.BacklogPolicy"/>
    /// convention): a typo here is refused at the command line, with the recognized values quoted,
    /// rather than silently deferring to whatever level sits below it.
    /// </summary>
    public static ReviewStageComposition Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return FromInput(trimmed) is { } parsed && parsed != Unknown
            ? parsed
            : throw new DomainValidationException(
                $"'{trimmed}' is not a review stage composition. Use \"full-pipeline\", "
                + "\"adversarial-only\", \"conformance-only\", \"skip-final-pass\", or \"none\".");
    }

    /// <summary>
    /// The tracks this composition opens a run with, in <see cref="ReviewLens.CycleLenses"/>'s own
    /// dispatch order — what <c>RunAggregate.ActiveReviewLenses</c> and the mandatory
    /// <see cref="ReviewMode.FinalFullPass"/> dispatch (<c>RunAggregate.CurrentCycleLenses</c>) both
    /// read instead of the static full list. <see cref="Unknown"/> reads as
    /// <see cref="FullPipeline"/> here — a stream written before this field existed ran the full
    /// pipeline, and this is what lets that stream's own lens computation stay unchanged.
    /// </summary>
    public IReadOnlyList<ReviewLens> OpeningLenses() => Value switch
    {
        "AdversarialOnly" => [ReviewLens.Adversarial],
        "ConformanceOnly" => [ReviewLens.Conformance],
        "None" => [],
        _ => ReviewLens.CycleLenses,
    };

    /// <summary>Whether this composition removes Decisions Log #92's mandatory pre-merge fresh-context read.</summary>
    public bool WaivesFinalFullPassGuarantee => Value is "SkipFinalPass" or "None";

    /// <summary>Whether this composition removes one of the two review lenses entirely, on every cycle.</summary>
    public bool DropsALens => Value is "AdversarialOnly" or "ConformanceOnly" or "None";

    /// <summary>
    /// Whether setting this composition needs an explicit acknowledgment
    /// (<see cref="ReviewStageCompositionValidation"/>) — either half of the guarantee loss counts.
    /// What a decider's own attestation clamp checks before recording an acknowledgment flag as
    /// true: a human who passes the flag alongside a composition that never needed it (FullPipeline,
    /// say, out of habit) must not have that read back later as "this task's guarantee was reduced
    /// and accepted", an unobserved fact never asserted (AGENTS.md).
    /// </summary>
    public bool RequiresAcknowledgment => WaivesFinalFullPassGuarantee || DropsALens;

    public bool Equals(ReviewStageComposition? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewStageCompositionJsonConverter : JsonConverter<ReviewStageComposition>
    {
        // Reading is deliberately not FromInput, the BacklogPolicy/JiraProjectKey convention: a
        // value already on an event stream is a record of what was set, and a rule tightened
        // later must not make an old document unreadable.
        public override ReviewStageComposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewStageComposition value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
