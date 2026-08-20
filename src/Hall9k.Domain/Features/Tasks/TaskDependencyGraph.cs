using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The BlockedBy edges reachable from one task, as a pure value the decider can reason over
/// without a database (loaded by TaskDependencyQuery). Cycle detection lives at publish
/// alone (Decisions Log #34): a graph under construction may transiently reference itself
/// in Draft, but a cycle can never become assignable.
/// </summary>
public sealed class TaskDependencyGraph
{
    private readonly Dictionary<Guid, TaskDependency> _nodes;

    public TaskDependencyGraph(IEnumerable<TaskDependency> nodes) =>
        _nodes = nodes.DistinctBy(node => node.Id).ToDictionary(node => node.Id);

    public static TaskDependencyGraph Empty => new([]);

    public TaskDependency? Node(Guid id) => _nodes.GetValueOrDefault(id);

    /// <summary>Ids among <paramref name="ids"/> that name no task the platform knows.</summary>
    public IReadOnlyList<Guid> Missing(IEnumerable<Guid> ids) => [.. ids.Where(id => !_nodes.ContainsKey(id))];

    /// <summary>
    /// The dependencies of <paramref name="ids"/> in declared order. Throws when one is
    /// unknown: callers check <see cref="Missing"/> first and report it in their own words.
    /// </summary>
    public IReadOnlyList<TaskDependency> Resolve(IEnumerable<Guid> ids) =>
    [
        .. ids.Select(id => _nodes.GetValueOrDefault(id)
            ?? throw new DomainNotFoundException($"No task {id} — it cannot be a dependency.")),
    ];

    /// <summary>
    /// A dependency cycle reachable from <paramref name="root"/>, as the path that closes it
    /// (first node repeated last), or null when the graph is acyclic from there. Depth-first
    /// with the current path coloured grey, which is the standard back-edge detection.
    /// </summary>
    public IReadOnlyList<Guid>? FindCycle(Guid root, IReadOnlyList<Guid> rootEdges)
    {
        HashSet<Guid> settled = [];
        List<Guid> path = [];
        HashSet<Guid> onPath = [];

        return Walk(root, rootEdges);

        IReadOnlyList<Guid>? Walk(Guid id, IReadOnlyList<Guid> edges)
        {
            path.Add(id);
            onPath.Add(id);

            foreach (Guid next in edges)
            {
                if (onPath.Contains(next))
                {
                    return [.. path[path.IndexOf(next)..], next];
                }

                if (settled.Contains(next))
                {
                    continue;
                }

                if (Walk(next, _nodes.GetValueOrDefault(next)?.BlockedBy ?? []) is { } cycle)
                {
                    return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            onPath.Remove(id);
            settled.Add(id);
            return null;
        }
    }

    /// <summary>The cycle spelled out for a human: every hop named, closing back on itself.</summary>
    public string DescribeCycle(IReadOnlyList<Guid> cycle, Guid root, string rootObjective) =>
        string.Join(" → ", cycle.Select(id =>
            id == root
                ? $"{root.ToString("N")[^8..]} \"{rootObjective}\" (this task)"
                : Node(id)?.Describe() ?? id.ToString()));
}
