using System.Text.RegularExpressions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Names a decision the way <see cref="IdeaIdResolver"/> names an idea: the full id, or an
/// unambiguous fragment matched against either end of it. The id IS the citation key idea
/// d805fd8b replaced PLAN.md §16's sequential numbers with, so a fragment of it is what a human
/// or an agent actually types.
/// <para>
/// One thing an idea has no equivalent of is accepted here too: the citation an imported decision
/// kept from before this store existed (idea d805fd8b, piece 3). Without it a citation is only
/// findable in the rendered <c>decisions.md</c>, and a decision left out of that file because it
/// no longer binds was findable nowhere at all — <c>h9k decide list</c> prints no citation column,
/// so following "Decisions Log #162" out of a source comment dead-ended at <c>show</c> asking for
/// an id the reader does not have (independent pre-PR review, cycle 1, adversarial lens).
/// </para>
/// </summary>
internal static partial class DecisionIdResolver
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
                $"'{idOrFragment}' has no characters to match a decision by — pass a full id, a "
                + "non-empty fragment of one, or the citation an imported decision kept.");
        }

        IReadOnlyList<DecisionDetails> all = await session.Query<DecisionDetails>().ToListAsync(cancellationToken);

        // Tried before the id fragment, and returning on any hit: a citation is a whole phrase or
        // a #-prefixed number, neither of which can be a fragment of a hex id, so a text that
        // matches one is never also a candidate for the other.
        Guid[] cited = CitationMatches(all, idOrFragment);
        if (cited.Length > 0)
        {
            return cited switch
            {
                [Guid single] => single,
                _ => throw new DomainConflictException(
                    $"'{idOrFragment}' names {cited.Length} legacy citations — type the whole citation "
                    + "(\"Decisions Log #162\", \"AGENTS.md Git rules #1\") rather than the number alone."),
            };
        }

        Guid[] matches = [.. all
            .Where(decision => decision.Id.ToString("N").StartsWith(fragment, StringComparison.OrdinalIgnoreCase)
                            || decision.Id.ToString("N").EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .Select(decision => decision.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException(
                $"No decision matches '{idOrFragment}'. A citation from before this store works here too, "
                + "whole (\"Decisions Log #162\") or as the number under whatever section cited it "
                + "(\"§16 #162\"). See what has been recorded, superseded ones included: "
                + "h9k decide list --all"),
            _ => throw new DomainConflictException(
                $"'{idOrFragment}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }

    /// <summary>Loads the aggregate behind a reference, for the commands that decide on state.</summary>
    public static async Task<DecisionAggregate> LoadAsync(
        IDocumentSession session, string idOrFragment, CancellationToken cancellationToken)
    {
        Guid decisionId = await ResolveAsync(session, idOrFragment, cancellationToken);
        return await session.Events.AggregateStreamAsync<DecisionAggregate>(decisionId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No decision {decisionId}.");
    }

    /// <summary>
    /// Every decision the text names as a legacy citation, in the order the rows arrived. Two
    /// forms are accepted and nothing else: the whole citation as it was recorded
    /// (<c>Decisions Log #162</c>, <c>AGENTS.md Git rules #1</c>), matched whole so no citation is
    /// resolved by a prefix of another; and failing that, the trailing <c>#&lt;number&gt;</c>,
    /// whatever section name precedes it. That second form is what makes the spellings already
    /// written across this repository work: one log entry is cited as <c>§16 #162</c>, another as
    /// <c>PLAN.md §16 #162</c> and another as <c>log #66</c>, the set of spellings is not closed,
    /// and rewriting a hundred-odd source comments into one of them was never the plan. So the
    /// section name is read as context and the number is what resolves.
    /// <para>
    /// The <c>#</c> is required for that second form, which is what keeps this out of the id
    /// fragment's way: a bare <c>162</c> stays what it always was, the front of an id. A number
    /// several sections each have an entry for (<c>#1</c> belongs to three) comes back as several
    /// matches and is reported as ambiguous rather than picked between — but only where the number
    /// is all that was typed, which is why the whole citation is tried first and returned alone
    /// when it hits. <c>Decisions Log #1</c> names one entry, and falling through to the number
    /// would have made the reader's own precision read as ambiguity.
    /// </para>
    /// </summary>
    internal static Guid[] CitationMatches(IReadOnlyList<DecisionDetails> decisions, string text)
    {
        string citation = Whitespace().Replace(text.Trim(), " ");
        Guid[] whole =
        [
            .. decisions
                .Where(decision => string.Equals(decision.LegacyId, citation, StringComparison.OrdinalIgnoreCase))
                .Select(decision => decision.Id),
        ];
        if (whole.Length > 0)
        {
            return whole;
        }

        Match trailing = TrailingNumber().Match(citation);
        if (!trailing.Success)
        {
            return [];
        }

        string suffix = $"#{trailing.Groups[1].Value}";
        return
        [
            .. decisions
                .Where(decision => decision.LegacyId.IsNotBlank()
                    && decision.LegacyId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(decision => decision.Id),
        ];
    }

    /// <summary>A citation typed across two lines, or with a double space in it, is the same citation.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The <c>#&lt;number&gt;</c> a citation ends with, whatever section name precedes it. Loose
    /// about the token itself rather than digits only, because an entry whose branch had not
    /// reached its renumbering step was cited as <c>#PLACEHOLDER-adbc4a1e</c> and the import kept
    /// every citation exactly as it found it.
    /// </summary>
    [GeneratedRegex(@"#([^\s#]+)$")]
    private static partial Regex TrailingNumber();
}
