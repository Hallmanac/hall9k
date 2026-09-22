using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// What one import call found: the events to append, and how many entries a previous run had
/// already recorded.
/// <para>
/// <see cref="Retirements"/> is one <see cref="DecisionSuperseded"/> per entry in
/// <see cref="ToRecord"/> whose rule this very change retired, keyed by the decision's own id and
/// belonging on that same new stream. Empty on every run after the first, for the same reason
/// <see cref="ToRecord"/> is.
/// </para>
/// </summary>
public sealed record LegacyImportPlan(
    IReadOnlyList<DecisionRecorded> ToRecord,
    IReadOnlyList<DecisionSuperseded> Retirements,
    int AlreadyImported);

/// <summary>
/// The rules the one-time import of PLAN.md §16 and AGENTS.md's standing rules has of its own
/// (idea d805fd8b, piece 3): replication has to be on, the same citation is never imported twice,
/// and no source may claim one citation for two entries. Everything else it does is
/// <see cref="DecisionDecider.Record"/>'s job, called once per entry, so an imported decision is
/// validated exactly like a typed one.
/// <para>
/// <b>Replication has to be switched on first.</b> An event written before this node's own
/// switch-on point never rides an outbox and is never backfilled from elsewhere (Brian's ruling of
/// 2026-09-13, idea 202383dc's M2a), so importing on a node that has not switched on would bury
/// two hundred and eighty decisions in one install's local store forever, with no way to move them
/// but a second import somewhere else minting a second set of ids for the same rules. Refusing is
/// the only outcome that leaves the fleet repairable.
/// </para>
/// <para>
/// <b>It is idempotent through the legacy id.</b> A run records every entry whose citation is not
/// already on a decision in this scope, so a second run records nothing, and a run interrupted
/// part way through finishes on the next call. The legacy id is the right key for this precisely
/// because it is the one thing about an imported decision that did not come from this store: two
/// runs of the same import cannot disagree about what <c>Decisions Log #62</c> is called.
/// </para>
/// <para>
/// <b>One entry is recorded and superseded in the same act.</b> AGENTS.md's own bullet instructing
/// a branch to append its entry to PLAN.md §16 under a <c>PLACEHOLDER-&lt;shortid&gt;</c> token is
/// simply absent from the frozen snapshot, because this change is what retired that convention and
/// importing it would record a rule that stopped holding the moment it landed. §16 #162 is the same
/// rule, stated by the decision the AGENTS.md bullet was a restatement of, and it cannot be dropped
/// the same way: thirteen places in this repository cite <c>Decisions Log #162</c> today, and a
/// citation whose entry was never imported resolves to nothing. So it is imported, keeping its
/// citation, and ended on its own stream in the same transaction (<see cref="RetiredOnImport"/>) —
/// which keeps the rule itself out of the rulebook agents read, while leaving the citation
/// answerable: <c>DecisionsDocumentRenderer</c> names it at the foot of <c>decisions.md</c> as
/// ended, without restating it, and <c>DecisionIdResolver</c> accepts the citation itself, so
/// <c>h9k decide show "Decisions Log #162"</c> reads it in full with the reason it ended
/// (independent pre-PR review, cycle 1, adversarial and conformance lenses).
/// </para>
/// </summary>
public static class LegacyKnowledgeImportDecider
{
    /// <summary>
    /// Every citation the import records and then immediately supersedes, with the reason recorded
    /// on its own stream. The bar for being on this list is narrow and is the same one that kept
    /// the matching AGENTS.md bullet out of the snapshot: the rule stopped holding the moment this
    /// change landed, and a session that followed it out of <c>decisions.md</c> would fail a gate
    /// doing so. A rule that merely reads as dated, or one whose machinery this change happens to
    /// leave standing, is imported binding and left for a human to supersede deliberately.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RetiredOnImport =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{LegacyDecisionsLogParser.CitationPrefix}#162"] =
                "Retired by the change that imported it (idea d805fd8b, piece 3). PLAN.md §16 carries no "
                + "numbered entries now, so there is no tail for a branch to append its own entry to and no "
                + "number for a placeholder to stand in for; DecisionsLogNumberingGuardTests fails the build "
                + "of a branch that appends one anyway. This entry is imported rather than skipped so the "
                + "citations already written across this repository as \"Decisions Log #162\" still resolve, "
                + "and superseded in the same act so the rendered decisions.md never tells a session to do "
                + "the one thing the gate now refuses. Nothing replaced it: a decision is recorded with "
                + "h9k decide and cited by the id in its own heading. The renumbering machinery this entry "
                + "describes is still shipped and still cited from the source, and retiring that is a later "
                + "piece of the same idea.",
        };

    /// <summary>
    /// Refuses unless this node has a replication switch-on point (idea 202383dc, M2a). The
    /// argument is <c>NodeAggregate.ReplicationSwitchOnSequence</c>, which is null until
    /// the first outbound replication sweep ever runs here.
    /// </summary>
    public static void RequireReplicationSwitchedOn(long? replicationSwitchOnSequence)
    {
        if (replicationSwitchOnSequence is not null)
        {
            return;
        }

        throw new DomainValidationException(
            "This node has not switched event replication on yet (idea 202383dc, M2a), so nothing "
            + "imported here would ever reach another node: an event written before a node's own "
            + "switch-on point never rides its outbox and is never backfilled from elsewhere "
            + "(Brian's ruling of 2026-09-13). Switch-on is recorded by the first outbound "
            + "replication sweep this node runs, and that sweep needs two things: the daemon "
            + "running (h9k daemon start), and a project whose own ledger carries a key, which "
            + "h9k project join writes, or h9k project assign-key backfills for a project whose "
            + "genesis predates keys. Give it a sweep and run this again. Importing on a node that "
            + "is never going to replicate is not the fallback; the decisions would be stranded in "
            + "one install's store with no id anybody else could cite.");
    }

    /// <summary>
    /// The events this call should append: one <see cref="DecisionRecorded"/> per entry whose
    /// legacy id is not already recorded in this scope, in the order the entries were parsed.
    /// <para>
    /// Ids are minted in that same order on purpose. <see cref="DomainId.New"/> is a sequential
    /// UUIDv7, so ids minted in the log's own order sort in the log's own order, and the rendered
    /// document (which orders on recorded-at, then the id) reads down the log the way the markdown
    /// did rather than shuffling two hundred and eighty rules into an arbitrary sequence. Nothing
    /// depends on that for correctness; the ordering is a courtesy to whoever reads the file.
    /// </para>
    /// <para>
    /// One <see cref="DateTimeOffset"/> is shared by every event in the call, because one import
    /// is one act. The dates the decisions were originally made are not knowable per entry from
    /// the markdown and are not invented here; where an entry states its own date, it says so in
    /// its own prose, which travels intact.
    /// </para>
    /// <para>
    /// Alongside those recordings, one <see cref="DecisionSuperseded"/> per entry on
    /// <see cref="RetiredOnImport"/> that this call is actually recording — the rules this very
    /// change retired, imported for their citations and ended behind the recording on the same
    /// stream. Never for an entry a previous run already recorded, which is what keeps a second
    /// run from superseding a stream that is already ended.
    /// </para>
    /// </summary>
    public static LegacyImportPlan Plan(
        IReadOnlyList<LegacyDecisionEntry> entries,
        IReadOnlyCollection<string> alreadyImported,
        KnowledgeScope scope,
        Guid scopeId,
        RecordedProvenance provenance,
        DateTimeOffset recordedAt)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (LegacyDecisionEntry entry in entries)
        {
            if (!seen.Add(entry.LegacyId))
            {
                throw new DomainValidationException(
                    $"Two entries in the import's own source claim the same citation, '{entry.LegacyId}'. "
                    + "The import refuses rather than recording one rule twice under one name, since the "
                    + "citation is what every later run reads to tell what it has already imported.");
            }
        }

        HashSet<string> recorded = new(alreadyImported, StringComparer.Ordinal);
        List<DecisionRecorded> toRecord = [];
        List<DecisionSuperseded> retirements = [];
        foreach (LegacyDecisionEntry entry in entries.Where(entry => !recorded.Contains(entry.LegacyId)))
        {
            DecisionRecorded decision = DecisionDecider.Record(
                DomainId.New(), scope, scopeId, entry.Statement, originIncident: null, supersedes: [],
                provenance, recordedAt, entry.LegacyId);
            toRecord.Add(decision);

            if (RetiredOnImport.TryGetValue(entry.LegacyId, out string? reason))
            {
                // Superseded by nothing rather than by the nearest plausible successor, which is
                // DecisionSuperseded's own documented null case: this rule was overruled by a
                // change, not replaced by another ruling. The owner is whoever ran the import,
                // read off the provenance the recording already carries rather than taken as a
                // second argument saying the same thing.
                retirements.Add(DecisionDecider.SupersedeAsRecorded(
                    decision, supersededBy: null, reason, provenance.RecordedByOwnerId, recordedAt));
            }
        }

        return new LegacyImportPlan(toRecord, retirements, entries.Count - toRecord.Count);
    }
}
