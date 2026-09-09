using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One project's effective auto-pr-review speed and where it came from — the resolution every
/// reader of this setting goes through (Decisions Log #161), rather than reading
/// <see cref="Projections.ProjectDetails.AutoPrReview"/>, which carries the last value the
/// stream recorded and cannot say whether anything recorded one at all.
/// <para>
/// <see cref="Default"/> is <see cref="AutoPrReviewSpeed.Normal"/>: a project that never chose
/// mints, publishes and assigns a pr-review task for every review GitHub requests of this
/// install's own login, at the ordinary queue speed. Origin incident (2026-09-08): the feature
/// sat installed and silent on both nodes for three days because it was a per-project opt-in
/// defaulting to off and nothing surfaced that state — four review requests, two of them from
/// August, had accumulated on Brian unnoticed. <c>h9k project set &lt;project&gt;
/// --auto-pr-review off</c> still records an explicit opt-out, and
/// <see cref="Recorded"/> is what tells that apart from a project that simply never chose.
/// </para>
/// </summary>
public sealed record AutoPrReviewSetting(AutoPrReviewSpeed Speed, bool Recorded)
{
    /// <summary>What a project with nothing recorded resolves to (Decisions Log #161).</summary>
    public static readonly AutoPrReviewSpeed Default = AutoPrReviewSpeed.Normal;

    /// <summary>The default resolution itself, for a caller with no stream to read (a fresh project, a test).</summary>
    public static readonly AutoPrReviewSetting Unrecorded = new(Default, Recorded: false);

    /// <summary>Whether a review request GitHub makes here mints and starts a task on its own.</summary>
    public bool IsOn => Speed != AutoPrReviewSpeed.Off;

    /// <summary>How the effective value is described wherever origin is printed: 'explicit' or 'default'.</summary>
    public string Origin => Recorded ? "explicit" : "default";

    /// <summary>The one-word state a log line, a status line and a settings row all say.</summary>
    public string OnOff => IsOn ? "on" : "off";

    public static AutoPrReviewSetting From(ProjectSettingsHistory history) =>
        history.LastRecorded(change => change.AutoPrReview) is { HasValue: true } recorded
            ? new AutoPrReviewSetting(recorded.Value ?? AutoPrReviewSpeed.Off, Recorded: true)
            : Unrecorded;

    public static async Task<AutoPrReviewSetting> ResolveAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken) =>
        From(await ProjectSettingsHistory.ReadAsync(session, projectId, cancellationToken));

    /// <summary>
    /// Every named project's own resolution, one stream read each — the shape the daemon's
    /// start-up announcement, its sweep, and <c>h9k status</c> all need, since each of the three
    /// speaks about every project rather than one.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, AutoPrReviewSetting>> ResolveAllAsync(
        IQuerySession session, IEnumerable<Guid> projectIds, CancellationToken cancellationToken)
    {
        Dictionary<Guid, AutoPrReviewSetting> resolved = [];
        foreach (Guid projectId in projectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved[projectId] = await ResolveAsync(session, projectId, cancellationToken);
        }

        return resolved;
    }
}
