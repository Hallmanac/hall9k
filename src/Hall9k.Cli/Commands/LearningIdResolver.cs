using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names a lesson the way <see cref="DecisionIdResolver"/> names a decision: the full id, or an
/// unambiguous fragment matched against either end of it. The short id is what a prompt-injected
/// lesson will carry once backlog 55's injection lands, so a fragment of it is what an agent
/// citing or retiring one types.
/// </summary>
internal static class LearningIdResolver
{
    public static async Task<Guid> ResolveAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrFragment, out Guid id))
        {
            return id;
        }

        string fragment = idOrFragment.Replace("-", "");
        if (fragment.Length == 0)
        {
            throw new DomainValidationException(
                $"'{idOrFragment}' has no characters to match a lesson by — pass a full id or a "
                + "non-empty fragment of one.");
        }

        IReadOnlyList<LearningDetails> all = await session.Query<LearningDetails>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(learning => learning.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                            || learning.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(learning => learning.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException(
                $"No lesson matches '{idOrFragment}'. See what has been recorded: h9k learn list"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>
    /// Resolves a reference that is about to be RECORDED as a citation, so it has to name a lesson
    /// that exists. <see cref="ResolveAsync"/>'s full-id fast path deliberately does not query,
    /// and every other caller is fine with that because it goes on to load the lesson and gets
    /// "No lesson &lt;id&gt;" from the load. A citation is never loaded: it is written onto an
    /// event and read back by a person much later, so a well-formed id that names nothing would
    /// ship as a merge pointing at nothing and there is no second chance to catch it
    /// (adversarial pre-PR review, cycle 1).
    /// <para>
    /// Checked against <see cref="LearningDetails"/> rather than by replaying the stream: the row
    /// is what <c>h9k learn show</c> and <c>h9k learn list</c> will read the citation back
    /// through, so it is the existence that actually matters to whoever checks the merge.
    /// </para>
    /// </summary>
    public static async Task<Guid> ResolveRecordedAsync(
        IQuerySession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid learningId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.LoadAsync<LearningDetails>(learningId, cancellationToken) is not null
            ? learningId
            : throw new DomainNotFoundException(
                $"No lesson {learningId}, so there is nothing to cite it as a source. See what has "
                + "been recorded: h9k learn list");
    }

    /// <summary>Loads the aggregate behind a reference, for the commands that decide on state.</summary>
    public static async Task<LearningAggregate> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid learningId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.Events.AggregateStreamAsync<LearningAggregate>(learningId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No lesson {learningId}.");
    }
}
