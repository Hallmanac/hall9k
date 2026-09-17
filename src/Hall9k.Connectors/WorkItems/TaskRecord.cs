using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// A task's own projection in the ledger (idea 202383dc, A3a): the whole of a published task,
/// written to <c>records/&lt;task-id&gt;.yaml</c> on <c>refs/hall9k/ledger/records</c>
/// (<see cref="Ledger.LedgerRefRegistry.RecordPath"/>) so every node that shares the project can
/// tell what a task is, whichever one published it. Keyed by task id rather than by a tracker key
/// because ids are the same on every node (Brian, 2026-09-13) — a tracker reference differs per
/// provider and a task with no tracker item at all has none.
/// <para>
/// It is a PROJECTION, never the event log: every field but <see cref="Holder"/> is composed fresh
/// from the task's current state on every publish and every revise (<c>Hall9k.Cli.Commands.TaskRecordPublication</c>
/// is the one writer), so the record is rebuildable from the events at any time and carries nothing
/// an event does not already say. <see cref="Holder"/> is the one field this build never composes:
/// it stays whatever it already was — empty until A3b's holder lock writes it — because the record
/// writer does not own it and must never invent or clear a claim that is not its own to make.
/// </para>
/// <para>
/// Dependencies and the epic both travel as ids now, not as a tracker's own numbering: task ids are
/// shared across every node (adoption keeps the origin's id, Decisions Log #60), so the convention
/// this record used to need — writing a dependency as the ISSUE NUMBER it happened to carry,
/// because ids differed per install — retires with it. A dependency with no id at all cannot occur:
/// every edge here is another task in this same store, addressed the only way that means the same
/// thing everywhere.
/// </para>
/// </summary>
public sealed record TaskRecord(
    Guid TaskId,
    string Project,
    string Type,
    string Objective,
    IReadOnlyList<string> Criteria,
    string? AgentContext,
    string? Model,
    /// <summary>
    /// The pre-approval the ORIGIN install gave its own copy, as the three-valued mode
    /// (<see cref="PreApprovalMode"/>) rather than a boolean: a task pre-approved
    /// after-human-review is neither plainly on nor plainly off, and a record whose whole job is
    /// stating the origin's answer must be able to say which of the three it was. This field is
    /// read and reported, never applied — pre-approval deliberately does not carry across the
    /// crossing, and the adopting install gives its own answer with <c>h9k task add
    /// --pre-approved</c>.
    /// </summary>
    PreApprovalMode PreApproval,
    /// <summary>
    /// This task's tracker item, provider and key together, or null when it has none — a project
    /// with no tracker, or a task published --untracked, gets a record too (Brian's 2026-09-13
    /// ruling: "a task with no tracker item gets a record too").
    /// </summary>
    ExternalReference? ExternalReference,
    /// <summary>This task's blockers, by task id — see this type's own remarks on why an id rather than a tracker key.</summary>
    IReadOnlyList<Guid> Dependencies,
    string? EpicTitle,
    Guid? EpicId,
    TaskRecordCaps Caps,
    /// <summary>The root fingerprint of the owner who published this record (idea 202383dc, A2a).</summary>
    string OriginOwnerFingerprint,
    /// <summary>Which node published this record, when, and onto which branch — <see cref="TaskOrigin.TaskId"/> is this same record's own <see cref="TaskId"/>.</summary>
    TaskOrigin Origin,
    /// <summary>
    /// The holder lock (A3b, not yet built): empty on every record this build writes, and never
    /// composed by <c>TaskRecordPublication</c> — it is read back from whatever the record already
    /// held and carried through unchanged, so a publish or a revise can never invent or clear a
    /// claim that is not its own to make.
    /// </summary>
    TaskRecordHolder? Holder,
    /// <summary>
    /// A second tracker item, shown and linked alongside <see cref="ExternalReference"/> but never
    /// gating or written to (task: a task may link to both a GitHub issue and a Jira card): the
    /// primary — <see cref="ExternalReference"/> — alone keeps the claim gate, the branch key,
    /// publish, closeout close, and every tracker write. Null on every task with at most one
    /// reference, which is every record this build wrote before this field existed.
    /// </summary>
    ExternalReference? SecondaryExternalReference = null)
{
    /// <summary>
    /// The key that says a block is one of these, and the version that says which shape it is in.
    /// A reader that finds no such key is looking at content nobody published from hall9k.
    /// </summary>
    public const string VersionKey = "hall9k-task-record";

    /// <summary>
    /// The only version this build writes. A reader accepts anything it can read rather than
    /// demanding an exact match: every field is optional on the way in, so a record written by a
    /// later build degrades to the fields this one understands instead of refusing to read it.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The record as YAML — plain and block scalars throughout, and never a quoted one, so nothing
    /// downstream has to unquote anything (<see cref="FrontmatterYaml"/>'s own summary carries the
    /// incident behind that rule).
    /// </summary>
    public string ToYaml()
    {
        StringBuilder yaml = new();
        yaml.Append(FrontmatterYaml.WriteValue(
            VersionKey, CurrentVersion.ToString(CultureInfo.InvariantCulture)));
        yaml.Append(FrontmatterYaml.WriteValue("task-id", TaskId.ToString()));
        yaml.Append(FrontmatterYaml.WriteScalar("project", Project));
        // Lowercase is the record's canonical casing (the platform displays it capitalised); the
        // reader accepts either, so a hand-written block saying "Feature" still reads.
        yaml.Append(FrontmatterYaml.WriteScalar("type", Type.ToLowerInvariant()));
        yaml.Append(FrontmatterYaml.WriteValue("pre-approved", PreApproval.Word));
        if (Model.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("model", Model));
        }

        if (ExternalReference is { } reference)
        {
            yaml.Append(FrontmatterYaml.WriteScalar("external-reference", reference.ToString()));
        }

        if (SecondaryExternalReference is { } secondaryReference)
        {
            yaml.Append(FrontmatterYaml.WriteScalar("secondary-external-reference", secondaryReference.ToString()));
        }

        yaml.Append(FrontmatterYaml.WriteScalar("objective", Objective));
        yaml.Append("criteria:\n");
        foreach (string criterion in Criteria)
        {
            yaml.Append(FrontmatterYaml.WriteListItem(criterion, FrontmatterYaml.BlockIndent));
        }

        yaml.Append(FrontmatterYaml.WriteValue(
            "dependencies", $"[{string.Join(", ", Dependencies)}]"));

        if (EpicTitle.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("epic-title", EpicTitle));
        }

        if (EpicId is { } epicId)
        {
            yaml.Append(FrontmatterYaml.WriteValue("epic-id", epicId.ToString()));
        }

        yaml.Append(Caps.ToYaml());
        yaml.Append(FrontmatterYaml.WriteScalar("origin-owner-fingerprint", OriginOwnerFingerprint));
        yaml.Append(FrontmatterYaml.WriteValue("origin-node", Origin.NodeId.ToString()));
        yaml.Append(FrontmatterYaml.WriteScalar("origin-node-name", Origin.NodeName));
        if (Origin.BranchName.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("origin-branch", Origin.BranchName));
        }

        yaml.Append(FrontmatterYaml.WriteValue("published", Stamp(Origin.PublishedAt)));
        if (AgentContext.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("context", AgentContext));
        }

        if (Holder is { } holder)
        {
            yaml.Append(FrontmatterYaml.WriteScalar("holder-owner-fingerprint", holder.OwnerFingerprint));
            yaml.Append(FrontmatterYaml.WriteValue("holder-node", holder.NodeId.ToString()));
            yaml.Append(FrontmatterYaml.WriteScalar("holder-node-name", holder.NodeName));
            yaml.Append(FrontmatterYaml.WriteValue("holder-since", Stamp(holder.Since)));
        }

        return yaml.ToString();
    }

    /// <summary>
    /// The record a block of YAML describes, or null when the block is not one — no version key, or
    /// no objective to build a draft around. Never throws: content that cannot be read this way is
    /// treated the same as content that carries no record at all.
    /// </summary>
    public static TaskRecord? TryParse(string? yaml)
    {
        Frontmatter parsed = FrontmatterYaml.Parse(yaml);
        if (!parsed.Has(VersionKey))
        {
            return null;
        }

        string? objective = parsed.Scalar("objective");
        if (objective.IsBlank())
        {
            return null;
        }

        Guid taskId = Uuid(parsed.Scalar("task-id")) ?? Guid.Empty;
        return new TaskRecord(
            taskId,
            parsed.Scalar("project") ?? string.Empty,
            parsed.Scalar("type")?.ToLowerInvariant() ?? string.Empty,
            objective,
            [.. parsed.List("criteria").Where(criterion => criterion.IsNotBlank())],
            parsed.Scalar("context"),
            parsed.Scalar("model"),
            PreApprovalMode.FromInput(parsed.Scalar("pre-approved")),
            ParseExternalReference(parsed.Scalar("external-reference")),
            [.. parsed.List("dependencies").Select(Uuid).OfType<Guid>()],
            parsed.Scalar("epic-title"),
            Uuid(parsed.Scalar("epic-id")),
            TaskRecordCaps.Read(parsed),
            parsed.Scalar("origin-owner-fingerprint") ?? string.Empty,
            new TaskOrigin(
                Uuid(parsed.Scalar("origin-node")) ?? Guid.Empty,
                parsed.Scalar("origin-node-name") ?? string.Empty,
                taskId,
                parsed.Scalar("origin-branch"),
                Stamp(parsed.Scalar("published"))),
            ReadHolder(parsed),
            ParseExternalReference(parsed.Scalar("secondary-external-reference")));
    }

    private static TaskRecordHolder? ReadHolder(Frontmatter parsed)
    {
        string? ownerFingerprint = parsed.Scalar("holder-owner-fingerprint");
        Guid? nodeId = Uuid(parsed.Scalar("holder-node"));
        if (ownerFingerprint.IsBlank() || nodeId is null)
        {
            return null;
        }

        return new TaskRecordHolder(
            ownerFingerprint,
            nodeId.Value,
            parsed.Scalar("holder-node-name") ?? string.Empty,
            Stamp(parsed.Scalar("holder-since")));
    }

    private static Hall9k.Domain.Features.Tasks.ExternalReference? ParseExternalReference(string? value) =>
        value.IsBlank() ? null : Hall9k.Domain.Features.Tasks.ExternalReference.Parse(value);

    private static Guid? Uuid(string? value) => Guid.TryParse(value, out Guid parsed) ? parsed : null;

    /// <summary>
    /// The publish (or holder-claim) stamp written the one way every reader will see it, in UTC
    /// with the invariant culture — the same discipline <see cref="ImportedWorkItem.ObservedStamp"/>
    /// keeps, and for the same reason: this string crosses machines and outlives the one that wrote it.
    /// </summary>
    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// The stamp read back. An unparseable or absent one answers <see cref="DateTimeOffset.MinValue"/>
    /// rather than the moment of reading: a READER must not claim it observed a moment it never saw.
    /// </summary>
    private static DateTimeOffset Stamp(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}

/// <summary>
/// The claim lock A3b writes (idea 202383dc): who holds this task, and since when. Absent — the
/// whole of <see cref="TaskRecord.Holder"/> being null — until the first claim writes it; nothing
/// in A3a ever composes one, only carries an already-written one through unchanged.
/// </summary>
public sealed record TaskRecordHolder(string OwnerFingerprint, Guid NodeId, string NodeName, DateTimeOffset Since);

/// <summary>
/// The four review-cycle caps and the session cap, each null when the origin task overrode nothing
/// and the level above it decides (task: the review cycle caps become settable at three levels;
/// Decisions Log #111 for the session cap). Written only when overridden, so an ordinary task's
/// record says nothing about caps at all.
/// </summary>
public sealed record TaskRecordCaps(
    int? MaxComplianceReviewCycles,
    int? MaxAdversarialReviewCycles,
    int? MaxFinalFullPassRounds,
    int? LifetimeReviewCycleBudget,
    int? SessionCap)
{
    public static readonly TaskRecordCaps None = new(null, null, null, null, null);

    public bool Any => MaxComplianceReviewCycles is not null || MaxAdversarialReviewCycles is not null
        || MaxFinalFullPassRounds is not null || LifetimeReviewCycleBudget is not null
        || SessionCap is not null;

    public string ToYaml()
    {
        StringBuilder yaml = new();
        Write("max-compliance-review-cycles", MaxComplianceReviewCycles);
        Write("max-adversarial-review-cycles", MaxAdversarialReviewCycles);
        Write("max-final-full-pass-rounds", MaxFinalFullPassRounds);
        Write("lifetime-review-cycle-budget", LifetimeReviewCycleBudget);
        Write("session-cap", SessionCap);
        return yaml.ToString();

        void Write(string key, int? cap)
        {
            if (cap is { } value)
            {
                yaml.Append(FrontmatterYaml.WriteValue(key, value.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    public static TaskRecordCaps Read(Frontmatter parsed) => new(
        parsed.Number("max-compliance-review-cycles"),
        parsed.Number("max-adversarial-review-cycles"),
        parsed.Number("max-final-full-pass-rounds"),
        parsed.Number("lifetime-review-cycle-budget"),
        parsed.Number("session-cap"));
}
