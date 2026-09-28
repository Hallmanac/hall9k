using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One project's effective security-review setting and where it came from (idea 6be68ee2, phase
/// two) — the resolution every reader of this setting goes through, the same
/// <see cref="AutoPrReviewSetting"/> shape (Decisions Log #161), rather than reading
/// <see cref="Projections.ProjectDetails.SecurityReview"/> directly, which carries the last value
/// the stream recorded and cannot say whether anything recorded one at all.
/// <para>
/// Default on: a project that never chose has the Security persona (idea 6be68ee2, phase two)
/// appended to every pr-review's persona plan, whatever the assignee declared
/// (<see cref="Hall9k.Daemon.Review.ReviewPersonaRegistry.Plan"/>). <c>h9k project set
/// --security-review off</c> turns it off; the lever is the project's, never a member's own
/// declaration — <see cref="Hall9k.Domain.Shared.ValueObjects.ReviewPersona.Parse"/> refuses
/// Security as something a member can declare at all.
/// </para>
/// <para>
/// This is not a <see cref="ReviewDriveSetting"/>: that type answers "may this persona drive the
/// product", a question Security never asks (it never drives — <c>ReviewPersonaEntry.CanDriveTheProduct</c>
/// is false on its entry). This one answers a different question — does this persona run at all —
/// so it gets its own type rather than a third field squeezed into that one's shape.
/// </para>
/// </summary>
public sealed record SecurityReviewSetting(bool IsOn, bool Recorded)
{
    /// <summary>What a project with nothing recorded resolves to.</summary>
    public const bool Default = true;

    /// <summary>The default resolution itself, for a caller with no stream to read (a fresh project, a test).</summary>
    public static readonly SecurityReviewSetting Unrecorded = new(Default, Recorded: false);

    /// <summary>How the effective value is described wherever origin is printed: 'explicit' or 'default'.</summary>
    public string Origin => Recorded ? "explicit" : "default";

    /// <summary>The one-word state a settings row and a log line both say.</summary>
    public string OnOff => IsOn ? "on" : "off";

    /// <summary>
    /// The <c>on</c>/<c>off</c> word a human types, refused by name rather than silently read as
    /// off. A blank value is refused for the same reason <see cref="Hall9k.Domain.Shared.ValueObjects.ReviewPersona.Parse"/>
    /// refuses one: at a command line a blank is an empty shell variable, never a request.
    /// </summary>
    public static bool ParseOnOff(string? value, string optionName) => value?.Trim().ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new DomainValidationException(
            $"{optionName} takes 'on' or 'off' — whether the Security persona's own review is "
            + "appended to every pr-review's persona plan for this project."),
    };

    public static SecurityReviewSetting From(ProjectSettingsHistory history) =>
        history.LastRecordedTeamField(change => change.SecurityReview, change => change.SecurityReview) is
            { HasValue: true } recorded
            ? new SecurityReviewSetting(recorded.Value, Recorded: true)
            : Unrecorded;

    public static async Task<SecurityReviewSetting> ResolveAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken) =>
        From(await ProjectSettingsHistory.ReadAsync(session, projectId, cancellationToken));
}
