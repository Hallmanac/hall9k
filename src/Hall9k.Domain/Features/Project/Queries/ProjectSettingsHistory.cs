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

    private ProjectSettingsHistory(IReadOnlyList<ProjectSettingsChanged> changes) => this.changes = changes;

    /// <summary>Nothing was ever recorded — what a project registered and never configured reads as.</summary>
    public static readonly ProjectSettingsHistory Empty = new([]);

    /// <summary>The in-order changes themselves, for a caller that already has them (a test, or a stream it just read).</summary>
    public static ProjectSettingsHistory FromChanges(IEnumerable<ProjectSettingsChanged> changes) => new([.. changes]);

    public static async Task<ProjectSettingsHistory> ReadAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<JasperFx.Events.IEvent> stream =
            await session.Events.FetchStreamAsync(projectId, token: cancellationToken);
        return new ProjectSettingsHistory([.. stream.Select(recorded => recorded.Data).OfType<ProjectSettingsChanged>()]);
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
}
