namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>
/// What the promotion step settled for a task's notes, before any lesson id exists: which note, if
/// any, becomes a lesson and with what claim, and the reason every other note was dropped. Pure, so
/// the ceiling and the claim bounds are provable without a store.
/// </summary>
/// <param name="KeptNoteId">The one note that becomes a lesson, or null.</param>
/// <param name="Claim">The kept note's claim exactly as the session wrote it, trimmed; null when nothing is kept.</param>
/// <param name="Dropped">Every other note, with the reason it was dropped, in the order offered and then the ones never offered.</param>
public sealed record NotePromotionVerdict(
    Guid? KeptNoteId, string? Claim, IReadOnlyList<(Guid NoteId, NoteDropReason Reason)> Dropped)
{
    /// <summary>
    /// Settles <paramref name="reading"/> against <paramref name="offer"/>. A note the answer did not
    /// name is dropped as not kept. A named note whose claim is blank, longer than
    /// <see cref="NotePromotionLimits.MaxClaimLength"/>, unprintable, or not made of the note's own words
    /// (<see cref="NotePromotionClaim"/>) is dropped for that, never cut or edited. Of the notes
    /// that stand, the first in the answer's order is kept and the rest are dropped as over the
    /// ceiling. Every note the trust rule let through is accounted for: the ones never shown are
    /// dropped as not offered.
    /// </summary>
    public static NotePromotionVerdict Decide(NotePromotionOffer offer, NotePromotionReading reading)
    {
        Dictionary<Guid, string> textById = offer.Offered.ToDictionary(note => note.NoteId, note => note.Text);
        Guid? keptId = null;
        string? claim = null;
        Dictionary<Guid, NoteDropReason> reasons = [];
        foreach (NoteKeepRequest keep in reading.Keeps)
        {
            NoteDropReason? reason = keep.Claim switch
            {
                { Length: 0 } => NoteDropReason.ClaimBlank,
                { Length: > NotePromotionLimits.MaxClaimLength } => NoteDropReason.ClaimTooLong,
                _ when !NotePromotionClaim.IsPrintable(keep.Claim) => NoteDropReason.ClaimUnprintable,
                _ when !NotePromotionClaim.IsGroundedIn(keep.Claim, textById.GetValueOrDefault(keep.NoteId, string.Empty)) =>
                    NoteDropReason.ClaimNotFromNote,
                // The ceiling is NotePromotionLimits.MaxKept; this type holds exactly that many kept notes.
                _ when keptId is not null => NoteDropReason.OverCeiling,
                _ => null,
            };
            if (reason is null)
            {
                keptId = keep.NoteId;
                claim = keep.Claim;
                continue;
            }

            reasons[keep.NoteId] = reason;
        }

        return new NotePromotionVerdict(
            keptId,
            claim,
            [
                .. offer.Offered
                    .Where(note => note.NoteId != keptId)
                    .Select(note => (note.NoteId, reasons.GetValueOrDefault(note.NoteId) ?? NoteDropReason.NotKept)),
                .. offer.NotOffered.Select(id => (id, NoteDropReason.NotOffered)),
            ]);
    }

    /// <summary>
    /// The verdict for a step that failed: nothing is kept and each offered note is dropped as step
    /// failed. A note past the ceiling was never shown to the session and stays not offered.
    /// </summary>
    public static NotePromotionVerdict Failed(NotePromotionOffer offer) =>
        new(
            null,
            null,
            [
                .. offer.Offered.Select(note => (note.NoteId, NoteDropReason.StepFailed)),
                .. offer.NotOffered.Select(id => (id, NoteDropReason.NotOffered)),
            ]);

    /// <summary>The decisions the record carries once the lesson, if one is kept, has its id.</summary>
    public IReadOnlyList<NotePromotionDecision> ToDecisions(Guid? lessonId)
    {
        NotePromotionDecision[] kept = KeptNoteId is { } noteId && lessonId is { } lesson
            ? [NotePromotionDecision.Kept(noteId, lesson)]
            : [];
        return [.. kept, .. Dropped.Select(drop => NotePromotionDecision.Drop(drop.NoteId, drop.Reason))];
    }
}
