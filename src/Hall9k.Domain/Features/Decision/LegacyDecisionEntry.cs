namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// One hand-written rule read out of the markdown that used to hold it (idea d805fd8b, piece 3),
/// on its way to becoming a <see cref="DecisionRecorded"/>. Two fields, because two is everything
/// the old sources actually carried: the citation the entry already answered to, and the entry's
/// whole text.
/// <para>
/// There is deliberately no origin-incident field. A §16 entry states its own scar inside its
/// prose, in a dozen different wordings, and pulling one out by pattern would be a guess dressed
/// as an observation (AGENTS.md, never guess at unobserved facts). The prose travels intact
/// inside <see cref="Statement"/> instead, and the structured field stays null for everything
/// imported, honestly empty rather than plausibly filled in.
/// </para>
/// </summary>
/// <param name="LegacyId">
/// The citation this entry already answered to, in the exact house style the repository's own
/// prose uses for it: <c>Decisions Log #62</c> for a numbered §16 entry, <c>AGENTS.md Git rules
/// #4</c> for a standing rule that never had a number.
/// </param>
/// <param name="Statement">The entry's own text, line endings normalised, with nothing added and nothing summarised.</param>
public sealed record LegacyDecisionEntry(string LegacyId, string Statement);
