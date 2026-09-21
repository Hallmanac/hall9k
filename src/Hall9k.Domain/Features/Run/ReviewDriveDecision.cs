using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Whether one persona's review session on one pr-review run actually stands the project's
/// product up, and why it does not when it does not (idea b9b09779, piece 3; piece 2 records the
/// QA persona's own the same way). Resolved once at dispatch and recorded on the run stream
/// (<see cref="Events.PrReviewPersonasSelected"/>) rather than re-read when the report is
/// composed: the project setting and the ledger's run skill can both move while a review is in
/// flight, and a report that claimed the session drove because the setting says so today would be
/// asserting something nobody observed.
/// </summary>
/// <param name="Persona">Whose review this decides.</param>
/// <param name="SettingOn">What <see cref="Project.ReviewDriveSetting"/> resolved for this project at dispatch.</param>
/// <param name="ProjectHasRunSkill">
/// Whether the project had a run skill on its ledger at dispatch. Driving needs one: it is the
/// only thing that says how this particular project is stood up, and a session that guessed at
/// that would be inventing a command rather than following one.
/// </param>
public sealed record ReviewDriveDecision(ReviewPersona Persona, bool SettingOn, bool ProjectHasRunSkill)
{
    /// <summary>
    /// Whether the session drives. Both halves have to be true; neither implies the other.
    /// <para>
    /// Deliberately not serialized, along with the two derived members below, on the same terms
    /// <see cref="Tasks.Events.TaskAdded.EffectivePreApproval"/> states: this record rides on a
    /// run's own stream (<see cref="Events.PrReviewPersonasSelected"/>), and a stream carries the
    /// facts that were recorded rather than conclusions drawn from them — least of all
    /// <see cref="WhyNotDriven"/>, which is report prose that would otherwise be frozen into
    /// every event as it happened to read on the day it was written.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool Drives => SettingOn && ProjectHasRunSkill;

    /// <summary>
    /// Whether the report may end with the offer to run the branch locally for the reviewer. Keyed
    /// on the run skill alone, never on <see cref="Drives"/>: the offer is a question about what
    /// the reviewer might want next, and it is just as answerable after a static review as after a
    /// driven one. What makes it unanswerable is having nothing that says how to start the app.
    /// </summary>
    [JsonIgnore]
    public bool CanOfferToRunItLive => ProjectHasRunSkill;

    /// <summary>
    /// Why this review did not drive, in one clause a report can print, or null when it did.
    /// Both reasons are named separately because they are reversed by different people doing
    /// different things: one is a project setting, the other is a missing run skill.
    /// </summary>
    [JsonIgnore]
    public string? WhyNotDriven =>
        Drives ? null
        : !SettingOn && !ProjectHasRunSkill
            ? $"this project has {DrivingName} turned off and has no run skill on its ledger"
        : SettingOn
            ? "this project has no run skill on its ledger, so nothing says how to stand its product up"
            : $"this project has {DrivingName} turned off";

    /// <summary>
    /// What the setting this decision read is called in a sentence a person reads — one clause per
    /// persona, because each is a separate project setting somebody turns on or off by name
    /// (<c>--design-review-drive</c>, <c>--qa-review-drive</c>), and a report that named the wrong
    /// one would send a reader to change a setting that had nothing to do with their review.
    /// </summary>
    private string DrivingName =>
        Persona == ReviewPersona.Qa ? "QA-review driving"
        : Persona == ReviewPersona.Designer ? "design-review driving"
        : "review driving";

    /// <summary>The decision a caller with nothing to read makes: the persona's own default, and no run skill.</summary>
    public static ReviewDriveDecision NoneFor(ReviewPersona persona) =>
        new(persona, Project.ReviewDriveSetting.DefaultFor(persona), ProjectHasRunSkill: false);
}
