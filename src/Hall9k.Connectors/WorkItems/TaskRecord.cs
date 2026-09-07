using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The whole of a published task, written into the tracker item it is published to so a second
/// hall9k install can adopt the item and get the same task rather than three fields of it (task: a
/// published task's GitHub issue carries the whole task record). Everything a draft needs travels
/// here: the readiness contract, the agent context, the type and model, the caps, the dependency
/// edges, the epic, and where it came from.
/// <para>
/// Provider-neutral on purpose. This class knows the record's shape and its YAML; where that YAML
/// sits inside a particular tracker's item — a collapsed <c>&lt;details&gt;</c> section at the foot
/// of a GitHub issue body (<see cref="GitHubIssueBody"/>) — is the provider's business. Jira is the
/// next provider to carry the same record (docs/scope.md), and it will reuse this class rather than
/// grow a second shape.
/// </para>
/// <para>
/// Two conventions the record depends on, both of them because ids do not survive the crossing:
/// dependencies are written as ISSUE NUMBERS, never as the origin's task ids, since the issue
/// number is the only identifier that means the same thing on both installs; and the epic travels
/// as its TITLE beside the origin's id, since the adopting install maps it by title or creates one.
/// </para>
/// </summary>
public sealed record TaskRecord(
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
    /// <para>
    /// <see cref="PreApprovalMode.FromInput"/> is what reads it back, so the <c>true</c>/<c>false</c>
    /// a hand-written block would say — the shape the ten issues annotated before this feature
    /// existed use — still parses, as does the mode word the writer emits.
    /// </para>
    /// </summary>
    PreApprovalMode PreApproval,
    IReadOnlyList<int> BlockedByIssues,
    int DependenciesWithoutIssues,
    string? EpicTitle,
    Guid? EpicOriginId,
    TaskRecordCaps Caps,
    TaskOrigin Origin)
{
    /// <summary>
    /// The key that says a block is one of these, and the version that says which shape it is in.
    /// A reader that finds no such key is looking at an issue nobody published from hall9k, which
    /// adopts exactly as it always did (title to objective, body to context).
    /// </summary>
    public const string VersionKey = "hall9k-task-record";

    /// <summary>
    /// The only version this build writes. A reader accepts anything it can read rather than
    /// demanding an exact match: every field is optional on the way in, so a record written by a
    /// later build degrades to the fields this one understands instead of refusing the adoption.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The record as the YAML that goes inside the fence — plain and block scalars throughout, and
    /// never a quoted one, so nothing downstream has to unquote anything
    /// (<see cref="FrontmatterYaml"/>'s own summary carries the incident behind that rule).
    /// </summary>
    public string ToYaml()
    {
        StringBuilder yaml = new();
        yaml.Append(FrontmatterYaml.WriteValue(
            VersionKey, CurrentVersion.ToString(CultureInfo.InvariantCulture)));
        yaml.Append(FrontmatterYaml.WriteScalar("project", Project));
        // Lowercase is the record's canonical casing (the platform displays it capitalised); the
        // reader accepts either, so a hand-written block saying "Feature" still adopts.
        yaml.Append(FrontmatterYaml.WriteScalar("type", Type.ToLowerInvariant()));
        // The CLI's own spelling of the mode — off, on, after-human-review — rather than a boolean,
        // for the reason PreApproval's own doc gives: after-human-review is neither, and this field
        // exists to state which of the three the origin chose. The reader takes the old boolean
        // spellings too, so a hand-written `pre-approved: true` still adopts.
        yaml.Append(FrontmatterYaml.WriteValue("pre-approved", PreApproval.Word));
        if (Model.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("model", Model));
        }

        yaml.Append(FrontmatterYaml.WriteScalar("objective", Objective));
        yaml.Append("criteria:\n");
        foreach (string criterion in Criteria)
        {
            yaml.Append(FrontmatterYaml.WriteListItem(criterion, FrontmatterYaml.BlockIndent));
        }

        yaml.Append(FrontmatterYaml.WriteValue(
            "blocked-by-issues", $"[{string.Join(", ", BlockedByIssues)}]"));
        if (DependenciesWithoutIssues > 0)
        {
            // Stated rather than silently dropped: the origin has dependencies whose own tasks were
            // never published to an issue, so there is no identifier that would mean anything here.
            // A count is the honest whole of what can be said about them (AGENTS.md, never guess).
            yaml.Append(FrontmatterYaml.WriteValue(
                "blocked-by-without-issues",
                DependenciesWithoutIssues.ToString(CultureInfo.InvariantCulture)));
        }

        if (EpicTitle.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("epic-title", EpicTitle));
        }

        if (EpicOriginId is { } epicOriginId)
        {
            yaml.Append(FrontmatterYaml.WriteValue("epic-origin-id", epicOriginId.ToString()));
        }

        yaml.Append(Caps.ToYaml());
        yaml.Append(FrontmatterYaml.WriteValue("origin-node", Origin.NodeId.ToString()));
        yaml.Append(FrontmatterYaml.WriteScalar("origin-node-name", Origin.NodeName));
        yaml.Append(FrontmatterYaml.WriteValue("origin-task", Origin.TaskId.ToString()));
        if (Origin.BranchName.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("origin-branch", Origin.BranchName));
        }

        yaml.Append(FrontmatterYaml.WriteValue("published", Stamp(Origin.PublishedAt)));
        if (AgentContext.IsNotBlank())
        {
            yaml.Append(FrontmatterYaml.WriteScalar("context", AgentContext));
        }

        return yaml.ToString();
    }

    /// <summary>
    /// The record a block of YAML describes, or null when the block is not one — no version key, or
    /// no objective to build a draft around. Never throws: an item whose record cannot be read is
    /// adopted the way an item with no record at all is, and a refusal here would turn a
    /// best-effort improvement into a wall.
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

        return new TaskRecord(
            parsed.Scalar("project") ?? string.Empty,
            parsed.Scalar("type")?.ToLowerInvariant() ?? string.Empty,
            objective,
            [.. parsed.List("criteria").Where(criterion => criterion.IsNotBlank())],
            parsed.Scalar("context"),
            parsed.Scalar("model"),
            // FromInput, not Flag: it takes the mode words the writer emits and the boolean
            // spellings a hand-written block uses, and answers Unknown for anything else — which
            // the adoption reports as unrecognized rather than reading as off, the same degrade
            // every other unusable field in the record gets.
            PreApprovalMode.FromInput(parsed.Scalar("pre-approved")),
            [.. parsed.List("blocked-by-issues").Select(ParseIssueNumber).OfType<int>()],
            parsed.Number("blocked-by-without-issues") ?? 0,
            parsed.Scalar("epic-title"),
            Uuid(parsed.Scalar("epic-origin-id")),
            TaskRecordCaps.Read(parsed),
            new TaskOrigin(
                Uuid(parsed.Scalar("origin-node")) ?? Guid.Empty,
                parsed.Scalar("origin-node-name") ?? string.Empty,
                Uuid(parsed.Scalar("origin-task")) ?? Guid.Empty,
                parsed.Scalar("origin-branch"),
                Stamp(parsed.Scalar("published"))));
    }

    /// <summary>
    /// An issue number as the record writes it, tolerating the <c>#42</c> a human would type by
    /// hand. Anything else answers null and is dropped rather than guessed at — an unreadable edge
    /// is better lost loudly (the adoption output names what it resolved) than turned into a
    /// dependency on whichever issue the digits happened to look like.
    /// </summary>
    private static int? ParseIssueNumber(string value) =>
        int.TryParse(
            value.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
        && number > 0
            ? number
            : null;

    private static Guid? Uuid(string? value) => Guid.TryParse(value, out Guid parsed) ? parsed : null;

    /// <summary>
    /// The publish stamp written the one way every reader will see it, in UTC with the invariant
    /// culture — the same discipline <see cref="ImportedWorkItem.ObservedStamp"/> keeps, and for the
    /// same reason: this string crosses machines and outlives the one that wrote it.
    /// </summary>
    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// The stamp read back. An unparseable or absent one answers <see cref="DateTimeOffset.MinValue"/>
    /// rather than the moment of reading: the adopting install did not observe a publish time, and
    /// stamping the record with "now" would claim it did.
    /// </summary>
    private static DateTimeOffset Stamp(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}

/// <summary>
/// The four review-cycle caps and the session cap, each null when the origin task overrode nothing
/// and the level above it decides (task: the review cycle caps become settable at three levels;
/// Decisions Log #111 for the session cap). Written only when overridden, so an ordinary task's
/// record says nothing about caps at all rather than freezing this build's defaults into an issue.
/// <para>
/// These are the caps as of the last time the record was written — publish, or a revise. Unlike
/// every other field here, a cap is settable at any time, mid-run included
/// (<c>h9k task set-session-cap</c>, <c>h9k task set-review-caps</c>), and those commands
/// deliberately do not write to the tracker: they are local operating levers pulled while a run
/// grinds, not changes to the published work, and a gh round trip on each one would be noise. A cap
/// set that way reaches the record at the next revise, which rewrites the whole block.
/// </para>
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
