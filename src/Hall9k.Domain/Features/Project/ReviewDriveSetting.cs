using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Whether one review persona's session is allowed to stand this project's product up and drive
/// it, rather than reading the diff and the design files alone (idea b9b09779, pieces 2 and 3) —
/// and where that answer came from, the same <see cref="AutoPrReviewSetting"/> resolution every
/// default-on setting here goes through (Decisions Log #161), rather than reading the projection,
/// which carries the last value the stream recorded and cannot say whether anything recorded one.
/// <para>
/// One type for both drive settings rather than one each: the QA persona's review (piece 2) and
/// the designer's (piece 3) ask the identical question of the identical machinery — is driving on,
/// and is there a run skill to drive with — and differ only in their default. Splitting them into
/// two near-identical resolvers is how the two would drift.
/// </para>
/// <para>
/// The defaults differ on purpose and were ruled that way by Brian on 2026-09-17: the designer
/// drives by default, because a design review that never looks at the running product is judging
/// markup rather than an experience; QA does not, because a QA review's own centre of gravity is
/// the end-to-end suite, and standing the product up is the thing it reaches for when the suite
/// cannot answer. Both are per-project and reversible with one command.
/// </para>
/// </summary>
/// <param name="Persona">Which persona's driving this answers for.</param>
/// <param name="Enabled">Whether driving is allowed at all. Never on its own the decision to drive — see <see cref="Run.ReviewDriveDecision"/>, which also needs a run skill.</param>
/// <param name="Recorded">Whether this project ever recorded a choice, as opposed to inheriting the default.</param>
public sealed record ReviewDriveSetting(ReviewPersona Persona, bool Enabled, bool Recorded)
{
    /// <summary>What a project that never chose resolves to: on for the designer, off for QA.</summary>
    public static bool DefaultFor(ReviewPersona persona) => persona == ReviewPersona.Designer;

    /// <summary>The default resolution itself, for a caller with no stream to read (a fresh project, a test).</summary>
    public static ReviewDriveSetting UnrecordedFor(ReviewPersona persona) =>
        new(persona, DefaultFor(persona), Recorded: false);

    /// <summary>How the effective value is described wherever origin is printed: 'explicit' or 'default'.</summary>
    public string Origin => Recorded ? "explicit" : "default";

    /// <summary>The one word a settings row, a log line and a prompt all say.</summary>
    public string OnOff => Enabled ? "on" : "off";

    /// <summary>
    /// The <c>on</c>/<c>off</c> word a human types, refused by name rather than silently read as
    /// off. A blank value is refused for the same reason <see cref="ReviewPersona.Parse"/> refuses
    /// one: at a command line a blank is an empty shell variable, never a request.
    /// </summary>
    public static bool ParseOnOff(string? value, string optionName) => value?.Trim().ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new DomainValidationException(
            $"{optionName} takes 'on' or 'off' — whether that persona's review may stand this "
            + "project's product up and drive it, or reads the diff and the design files alone."),
    };

    /// <summary>
    /// What one project's stream actually recorded for <paramref name="persona"/>'s driving, or
    /// the default when it recorded nothing.
    /// </summary>
    public static ReviewDriveSetting From(ReviewPersona persona, ProjectSettingsHistory history) =>
        RecordedChoice(persona, history) is { HasValue: true } recorded
            ? new ReviewDriveSetting(persona, recorded.Value, Recorded: true)
            : UnrecordedFor(persona);

    public static async Task<ReviewDriveSetting> ResolveAsync(
        ReviewPersona persona, IQuerySession session, Guid projectId, CancellationToken cancellationToken) =>
        From(persona, await ProjectSettingsHistory.ReadAsync(session, projectId, cancellationToken));

    /// <summary>
    /// What this project's stream holds for one persona's driving, read across both halves of a
    /// settings change because a drive setting is a team field
    /// (<see cref="ProjectSettingsHistory.LastRecordedTeamField{T}"/>).
    /// <para>
    /// One field per persona rather than one keyed by persona, so a reader of the designer's
    /// answer never has to parse QA's out of the same value — and
    /// <see cref="Optional{T}.None"/> always for a persona with no field at all. That is
    /// <see cref="ReviewPersona.Engineer"/>, permanently: that review reads a diff and has never
    /// stood anything up.
    /// </para>
    /// </summary>
    private static Optional<bool> RecordedChoice(ReviewPersona persona, ProjectSettingsHistory history) =>
        persona == ReviewPersona.Designer
            ? history.LastRecordedTeamField(change => change.DesignReviewDrive, change => change.DesignReviewDrive)
        : persona == ReviewPersona.Qa
            ? history.LastRecordedTeamField(change => change.QaReviewDrive, change => change.QaReviewDrive)
            : Optional<bool>.None;
}
