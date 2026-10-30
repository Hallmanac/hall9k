using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One item, one idea: a second idea captured from a tracker item that another idea in discovery on
/// this node already carries is refused, naming that idea. Only a captured idea holds its item, so
/// concluding or archiving an idea releases the item with no further act.
/// <para>
/// This is the idea-to-idea half of the rule. An item a task holds is refused by the guards task
/// adoption runs (<see cref="TaskAddCommand.RefuseHeldItemAsync"/>); the converse, a task-side refusal
/// of an item a live idea carries, is parked until an operator adopts around an idea and loses the
/// provenance.
/// </para>
/// </summary>
internal static class IdeaWorkItemGuard
{
    /// <summary>
    /// Refuses when a captured idea already carries <paramref name="item"/>, under the reference the
    /// read returned or under one the source says the item used to carry (a Linear issue keeps its
    /// previous identifiers when it moves team, and is still one item).
    /// </summary>
    internal static async Task RefuseSecondIdeaAsync(
        IQuerySession session, ImportedWorkItem item, CancellationToken cancellationToken)
    {
        foreach (ExternalReference reference in new[] { item.Reference }.Concat(item.FormerReferences))
        {
            string canonical = reference.ToString();
            IdeaDetails? holder = (await session.Query<IdeaDetails>()
                    .Where(idea => idea.WorkItemReference == canonical)
                    .Where(idea => idea.MatchesSql("d.data ->> 'state' = ?", IdeaState.Captured.Value))
                    .OrderBy(idea => idea.CapturedAt)
                    .Take(1)
                    .ToListAsync(cancellationToken))
                .FirstOrDefault();
            if (holder is null)
            {
                continue;
            }

            string shortId = TaskListCommand.ShortId(holder.Id);
            string aka = reference == item.Reference
                ? string.Empty
                : $"{item.Reference} is the item the tracker used to call {reference}, and ";
            throw new DomainConflictException(
                $"{aka}{reference} is already carried by idea {shortId} ({ExternalText.OneLine(holder.Text)}), which is still "
                + $"in discovery. One item is one idea: see it with h9k idea show {shortId}, and cut tasks from it "
                + $"(h9k task add --from-idea {shortId} --objective \"…\"). If that idea is finished with, "
                + $"h9k idea conclude {shortId} --reason \"…\" or h9k idea archive {shortId} --reason \"…\" "
                + "releases the item.");
        }
    }
}
