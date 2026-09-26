using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Domain.Features.Project.Queries;

/// <summary>
/// Every <see cref="ProjectSettingsChanged"/> one project's stream carries, in stream order —
/// the one place that can tell "this project explicitly recorded a setting" apart from "nothing
/// was ever recorded and the initialised default is what a reader sees".
/// <para>
/// The projection cannot answer that question and never could (Decisions Log #161): a
/// <see cref="Projections.ProjectDetails"/> document is serialised whole on every write, so a
/// field's own initialised default is stored under its key from the project's very first event
/// onward, indistinguishable afterward from the same value chosen on purpose. That was harmless
/// while every default-driven setting defaulted to the value that changes nothing — and stopped
/// being harmless the moment <c>AutoPrReview</c>'s default flipped to <c>Normal</c>, where
/// reading a stored "Off" as an explicit opt-out would have kept every existing project silent
/// forever, which is the exact failure the flip exists to end. The stream is the only honest
/// record of what an operator actually chose (AGENTS.md: never guess at unobserved facts).
/// </para>
/// <para>
/// A project's own stream is <see cref="ProjectRegistered"/> plus one event per
/// <c>h9k project set</c>, so this is a handful of events on any real install — cheap enough to
/// read per project on a poll or a pane render, with no cache to go stale.
/// </para>
/// </summary>
public sealed class ProjectSettingsHistory
{
    private readonly IReadOnlyList<ProjectSettingsChanged> changes;
    private readonly IReadOnlyList<object> everyChange;

    private ProjectSettingsHistory(IReadOnlyList<object> everyChange)
    {
        this.everyChange = everyChange;
        changes = [.. everyChange.OfType<ProjectSettingsChanged>()];
    }

    /// <summary>Nothing was ever recorded — what a project registered and never configured reads as.</summary>
    public static readonly ProjectSettingsHistory Empty = new([]);

    /// <summary>The in-order changes themselves, for a caller that already has them (a test, or a stream it just read).</summary>
    public static ProjectSettingsHistory FromChanges(IEnumerable<ProjectSettingsChanged> changes) => new([.. changes]);

    /// <summary>
    /// Both halves of a settings change in one stream-ordered list, for a caller that needs a
    /// team field's own origin (<see cref="LastRecordedTeamField{T}"/>) rather than only what
    /// this node recorded locally.
    /// <para>
    /// Its own name rather than a second <see cref="FromChanges(IEnumerable{ProjectSettingsChanged})"/>
    /// overload, deliberately: the two filter differently, and as overloads the ELEMENT TYPE of
    /// whatever a caller happened to pass would pick between them silently. A typed list would
    /// bind to the node-half-only one and resolve a team field to its default with nothing to
    /// see at the call site, which is the exact failure <see cref="LastRecordedTeamField{T}"/>
    /// exists to prevent.
    /// </para>
    /// </summary>
    public static ProjectSettingsHistory FromEveryChange(IEnumerable<object> everyChange) =>
        new([.. everyChange.Where(change => change is ProjectSettingsChanged or ProjectTeamSettingsChanged)]);

    public static async Task<ProjectSettingsHistory> ReadAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<JasperFx.Events.IEvent> stream =
            await session.Events.FetchStreamAsync(projectId, token: cancellationToken);
        return new ProjectSettingsHistory(
            [.. stream.Select(recorded => recorded.Data)
                .Where(data => data is ProjectSettingsChanged or ProjectTeamSettingsChanged)]);
    }

    /// <summary>
    /// The last value this project actually recorded for one setting, or
    /// <see cref="Optional{T}.None"/> when it never recorded one at all. Present-with-null is a
    /// recorded choice like any other — it is how every clearable setting here says "back to the
    /// default" — so a caller distinguishing origin must read <see cref="Optional{T}.HasValue"/>
    /// rather than the value.
    /// </summary>
    public Optional<T> LastRecorded<T>(Func<ProjectSettingsChanged, Optional<T>> setting)
    {
        for (int index = changes.Count - 1; index >= 0; index--)
        {
            Optional<T> candidate = setting(changes[index]);
            if (candidate.HasValue)
            {
                return candidate;
            }
        }

        return Optional<T>.None;
    }

    /// <summary>Whether this project ever recorded a choice for one setting at all.</summary>
    public bool WasRecorded<T>(Func<ProjectSettingsChanged, Optional<T>> setting) => LastRecorded(setting).HasValue;

    /// <summary>
    /// The newest-stamped value recorded for a setting that lives in BOTH halves of a settings
    /// change — <see cref="ProjectSettingsChanged"/>, which stays node-scoped, and
    /// <see cref="ProjectTeamSettingsChanged"/>, which is the half that actually replicates (idea
    /// 202383dc, M2a). <see cref="LastRecorded"/> reads only the first, which is right for a
    /// node-scoped setting and wrong for a team one: on a teammate's own node the local half was
    /// never written at all, so a team setting read through <see cref="LastRecorded"/> there
    /// resolves to its default however deliberately somebody set it. Both halves are appended in
    /// the same commit on the originating node with the same <c>ChangedAt</c>, so either half
    /// finds the same value.
    /// <para>
    /// Newest by the event's own <c>ChangedAt</c>, not by stream position: a team change can be
    /// appended behind a newer one already held, when a catch-up answer delivers a pre-switch-on
    /// head after the post-switch-on tail, and a reader that took the last one appended would read
    /// the older value there. A tie goes to the later one in the stream, which is the order this
    /// read always had. This is the same rule <c>ProjectDetailsProjection</c> applies per field.
    /// </para>
    /// </summary>
    public Optional<T> LastRecordedTeamField<T>(
        Func<ProjectSettingsChanged, Optional<T>> nodeHalf, Func<ProjectTeamSettingsChanged, Optional<T>> teamHalf)
    {
        Optional<T> newest = Optional<T>.None;
        DateTimeOffset newestStamp = default;
        foreach (object recorded in everyChange)
        {
            (Optional<T> candidate, DateTimeOffset stamp) = recorded switch
            {
                ProjectSettingsChanged change => (nodeHalf(change), change.ChangedAt),
                ProjectTeamSettingsChanged change => (teamHalf(change), change.ChangedAt),
                _ => (Optional<T>.None, default),
            };
            if (candidate.HasValue && (!newest.HasValue || stamp >= newestStamp))
            {
                newest = candidate;
                newestStamp = stamp;
            }
        }

        return newest;
    }
}
