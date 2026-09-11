using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Features.Node;

/// <summary>
/// One standing launch hold, start to end (task: a session that exits at once with no work done
/// is treated as the node failing to launch sessions) — reconstructed from the node stream
/// rather than kept as a running total, so a hold that already cleared is still answerable after
/// the fact. <see cref="ClearedAt"/> is null for a hold still standing when the stream was read.
/// </summary>
public sealed record NodeLaunchHoldEpisode(
    DateTimeOffset RaisedAt,
    string CauseText,
    DateTimeOffset? ClearedAt,
    int RunsHeld,
    int Probes)
{
    public bool IsOngoing => ClearedAt is null;
}

/// <summary>
/// Replays a node's own stream into its launch-hold episodes (task: a session that exits at once
/// with no work done is treated as the node failing to launch sessions) — the "queryable from
/// the node stream afterwards" half of the feature, since <see cref="NodeDetails"/> only ever
/// carries the current or most recent episode's own counts, reset the moment the next one is
/// raised. A node's own stream is short (registration plus a handful of hold events per episode),
/// so replaying it whole per query costs nothing worth caching.
/// </summary>
public static class NodeLaunchHoldEpisodes
{
    public static async Task<IReadOnlyList<NodeLaunchHoldEpisode>> ReadAsync(
        IQuerySession session, Guid nodeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> stream = await session.Events.FetchStreamAsync(nodeId, token: cancellationToken);

        List<NodeLaunchHoldEpisode> episodes = [];
        DateTimeOffset raisedAt = default;
        string causeText = string.Empty;
        HashSet<Guid> runsHeld = [];
        int probes = 0;
        bool open = false;

        foreach (IEvent recorded in stream)
        {
            switch (recorded.Data)
            {
                case NodeLaunchHoldRaised raised:
                    raisedAt = raised.RaisedAt;
                    causeText = raised.CauseText;
                    runsHeld = [];
                    probes = 0;
                    open = true;
                    break;
                case NodeLaunchHoldRunHeld runHeld when open:
                    runsHeld.Add(runHeld.RunId);
                    break;
                case NodeLaunchHoldProbed when open:
                    probes++;
                    break;
                case NodeLaunchHoldCleared cleared when open:
                    episodes.Add(new NodeLaunchHoldEpisode(raisedAt, causeText, cleared.ClearedAt, runsHeld.Count, probes));
                    open = false;
                    break;
            }
        }

        if (open)
        {
            episodes.Add(new NodeLaunchHoldEpisode(raisedAt, causeText, null, runsHeld.Count, probes));
        }

        return episodes;
    }
}
