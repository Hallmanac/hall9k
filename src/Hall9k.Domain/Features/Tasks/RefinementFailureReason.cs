using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Why a refinement session came to nothing: a closed list, so the event holds nothing a session or a stranger
/// wrote. <see cref="Unknown"/> is "no failure", which a session whose outcome was applied records. A session whose
/// report was readable but whose revision was not applied records the outcome it named and the reason here
/// (<see cref="DraftChanged"/> or <see cref="RevisionRefused"/>).
/// </summary>
[JsonConverter(typeof(RefinementFailureReasonJsonConverter))]
public sealed record RefinementFailureReason
{
    public static readonly RefinementFailureReason StartFailed = new("StartFailed", "the session could not be started");
    public static readonly RefinementFailureReason SessionError = new("SessionError", "the session ended in error");
    public static readonly RefinementFailureReason NoResult = new("NoResult", "the session ended without a result");
    public static readonly RefinementFailureReason TimedOut = new("TimedOut", "the session exceeded its time bound and was terminated");
    public static readonly RefinementFailureReason NoReport = new("NoReport", "the session wrote no report");
    public static readonly RefinementFailureReason UnreadableReport = new("UnreadableReport", "the report did not name exactly one outcome, or its revision file was missing");
    public static readonly RefinementFailureReason DraftChanged = new("DraftChanged", "the draft was changed after the session was dispatched, so its revision was not applied");
    public static readonly RefinementFailureReason RevisionRefused = new("RevisionRefused", "the revision the session wrote was refused");
    public static readonly RefinementFailureReason NoLongerDraft = new("NoLongerDraft", "the task was no longer a draft when the daemon came to dispatch the session, so none ran");
    public static readonly RefinementFailureReason DaemonRestarted = new("DaemonRestarted", "the daemon stopped or restarted while the session ran");
    public static readonly RefinementFailureReason DispatchExpired = new("DispatchExpired", "another node dispatched it and recorded no outcome past the foreign-session ceiling");
    public static readonly RefinementFailureReason Cancelled = new("Cancelled", "the plan was cancelled, so the session was stopped and nothing was applied");

    /// <summary>No failure. Serializes as an empty string.</summary>
    public static readonly RefinementFailureReason Unknown = new(string.Empty, "no failure");

    public string Value { get; }

    /// <summary>The sentence fragment <c>h9k task show</c> prints for this reason.</summary>
    public string Description { get; }

    private RefinementFailureReason(string value, string description)
    {
        Value = value;
        Description = description;
    }

    public bool IsFailure => Value.Length > 0;

    public static implicit operator string(RefinementFailureReason? value) => value?.Value ?? string.Empty;

    public static implicit operator RefinementFailureReason(string? value) => value switch
    {
        "StartFailed" => StartFailed,
        "SessionError" => SessionError,
        "NoResult" => NoResult,
        "TimedOut" => TimedOut,
        "NoReport" => NoReport,
        "UnreadableReport" => UnreadableReport,
        "DraftChanged" => DraftChanged,
        "RevisionRefused" => RevisionRefused,
        "NoLongerDraft" => NoLongerDraft,
        "DaemonRestarted" => DaemonRestarted,
        "DispatchExpired" => DispatchExpired,
        "Cancelled" => Cancelled,
        _ => Unknown,
    };

    public bool Equals(RefinementFailureReason? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RefinementFailureReasonJsonConverter : ClosedVocabularyJsonConverter<RefinementFailureReason>
    {
        protected override RefinementFailureReason FromStored(string? value) => value;

        protected override string ToStored(RefinementFailureReason value) => value.Value;
    }
}
