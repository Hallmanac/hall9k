using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The owner's standing pre-approval, three-valued (task: the people a pull request is waiting on
/// are named, and pre-approval gains a mode that waits for human review). <see cref="Off"/> is the
/// default and leaves the owner a synchronous gate at the pull request; <see cref="On"/> is the
/// original flag's behaviour — the daemon merges as soon as GitHub's own gates read satisfied;
/// <see cref="AfterHumanReview"/> is the same automatic merge with one extra gate in front of it,
/// so a task can start pre-approved and still wait for whichever reviewers the owner ends up
/// adding on GitHub.
/// <para>
/// Nothing here names a reviewer, and nothing here requests a review. Reviewers are assigned in
/// GitHub, by humans; this platform only reads who is outstanding, reports it, and waits. That is
/// why <see cref="AfterHumanReview"/> is a mode rather than a reviewer list: the owner's
/// intent ("do not merge this until a person has actually looked") is the only part of the
/// arrangement Hall9k stores.
/// </para>
/// <para>
/// The legacy boolean the events and projections also carry beside the mode is
/// <see cref="LegacyPreApproved"/> — <c>mode == On</c>, deliberately NOT
/// <see cref="MergesAutomatically"/>, so a reader that does not understand this vocabulary at all
/// fails closed rather than merging past a gate the owner asked for. Written that way after the
/// alternative was weighed and reversed (independent pre-PR review, cycle 1, adversarial lens): a
/// task pre-approved <see cref="AfterHumanReview"/> IS pre-approved, so <c>mode != Off</c> read as
/// the truer summary of it and kept every "will the daemon merge this itself" display answering as
/// it always had — but the reader that boolean exists for is a build which cannot honour this mode,
/// and telling that build "yes, merge it yourself" hands away exactly the gate the owner asked for.
/// An understated display on a build that predates the mode costs nothing by comparison.
/// <see cref="Resolve"/> is the only place the mapping runs in reverse, for a stream or a document
/// written before the mode existed.
/// </para>
/// </summary>
[JsonConverter(typeof(PreApprovalModeJsonConverter))]
public sealed record PreApprovalMode
{
    /// <summary>No pre-approval: the owner is a synchronous gate at the pull request, and the daemon merges nothing.</summary>
    public static readonly PreApprovalMode Off = new("Off");

    /// <summary>The original flag: the daemon merges on its own the moment GitHub's own gates read satisfied.</summary>
    public static readonly PreApprovalMode On = new("On");

    /// <summary>
    /// The daemon merges on its own, but only once a human review has actually happened: at least
    /// one human reviewer has been requested on the pull request at some point, and every
    /// requested reviewer has approved the current head. With no human reviewer ever requested the
    /// task waits — the owner is the one who adds a reviewer on GitHub, or flips this to
    /// <see cref="On"/> for an emergency.
    /// </summary>
    public static readonly PreApprovalMode AfterHumanReview = new("AfterHumanReview");

    /// <summary>
    /// Not recognized, or not recorded at all — a stream or projection document written before
    /// this vocabulary existed. Never a mode a human can set; <see cref="Resolve"/> maps it back
    /// onto the legacy boolean rather than letting it reach a merge gate. Serializes as an empty
    /// string.
    /// </summary>
    public static readonly PreApprovalMode Unknown = new("");

    public string Value { get; }

    private PreApprovalMode(string value) => Value = value;

    /// <summary>Whether the daemon merges this task's pull request itself at all — true in either automatic mode.</summary>
    public bool MergesAutomatically => this == On || this == AfterHumanReview;

    /// <summary>
    /// What the legacy <c>bool PreApproved</c> beside this mode is recorded as, on every event and
    /// every projection document: <c>this == On</c> — the one and only mode a build that predates
    /// this vocabulary can actually carry out, since it knows nothing of the human-review gate.
    /// <para>
    /// Deliberately not <see cref="MergesAutomatically"/>. That boolean is read by exactly one kind
    /// of reader — a build older than this one, sweeping a stream or a document this one wrote — and
    /// for <see cref="AfterHumanReview"/> it would tell that reader the pull request is plainly
    /// pre-approved, whereupon its own closeout would merge the moment GitHub's four gates read
    /// satisfied, before any human reviewer had been asked (independent pre-PR review, cycle 1,
    /// adversarial lens). False loses the pre-approval instead, which that build answers by leaving
    /// the merge to the owner — the same thing it does for every un-pre-approved task, and the safe
    /// direction for a fact this narrow: understating automation costs a display line, overstating
    /// it costs the gate.
    /// </para>
    /// <para>
    /// It round-trips through <see cref="Resolve"/> for <see cref="On"/> and <see cref="Off"/>, the
    /// two modes that boolean can express. <see cref="AfterHumanReview"/> degrades to
    /// <see cref="Off"/> there rather than round-tripping, and only where the recorded mode itself
    /// is missing — which is the same fail-closed direction, reached by the same reasoning.
    /// </para>
    /// </summary>
    public bool LegacyPreApproved => this == On;

    /// <summary>Whether the automatic merge additionally waits for a human review to have happened.</summary>
    public bool WaitsForHumanReview => this == AfterHumanReview;

    public static implicit operator string(PreApprovalMode? mode) => mode?.Value ?? string.Empty;

    public static implicit operator PreApprovalMode(string? value) =>
        value.IsBlank() ? Unknown : new PreApprovalMode(value);

    /// <summary>
    /// Maps human input to the closed set, case-insensitively — the CLI's own vocabulary
    /// (<c>on</c>/<c>off</c>/<c>after-human-review</c>) plus the boolean spellings the original
    /// flag accepted, so nothing a human already typed stops working. Anything else is
    /// <see cref="Unknown"/>, which the CLI refuses with the vocabulary quoted rather than
    /// silently reading as off.
    /// </summary>
    public static PreApprovalMode FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" => On,
        "off" or "false" or "no" => Off,
        "after-human-review" or "afterhumanreview" or "after_human_review" => AfterHumanReview,
        _ => Unknown,
    };

    /// <summary>
    /// The canonical spelling of this mode: the CLI's own word, the one a human types back, and
    /// the exact input <see cref="FromInput"/> round-trips. Lives on the value object rather than
    /// beside one surface because two projects need it — the CLI writes it to a terminal
    /// (<c>PreApprovalInput.Word</c>) and the connectors write it into a published tracker item's
    /// task record — and a three-way mapping kept in two places is a three-way mapping that
    /// eventually disagrees with itself. An unrecognized mode answers with whatever was recorded,
    /// so a surface names the unknown word rather than one of the three it might have been.
    /// </summary>
    public string Word => Value switch
    {
        "On" => "on",
        "Off" => "off",
        "AfterHumanReview" => "after-human-review",
        _ => Value,
    };

    /// <summary>
    /// The mode a stream or document actually means: what was recorded, or — when nothing was
    /// (<see cref="Unknown"/>, or a JSON key that was simply absent) — the legacy boolean the
    /// same event or document carries. This is the whole of the migration: every event written
    /// before this vocabulary existed recorded only the boolean, and the boolean says the truth
    /// for both of the two states that build could produce.
    /// <para>
    /// The inverse of <see cref="LegacyPreApproved"/> for those two, and deliberately not the
    /// inverse of <see cref="MergesAutomatically"/>: a record carrying <c>true</c> and no mode was
    /// written by a build that had only <see cref="On"/> to mean, so reading it as
    /// <see cref="On"/> claims nothing that build did not.
    /// </para>
    /// </summary>
    public static PreApprovalMode Resolve(PreApprovalMode? recorded, bool legacyPreApproved) =>
        recorded is null || recorded == Unknown
            ? legacyPreApproved ? On : Off
            : recorded;

    public bool Equals(PreApprovalMode? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class PreApprovalModeJsonConverter : JsonConverter<PreApprovalMode>
    {
        public override PreApprovalMode Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, PreApprovalMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
