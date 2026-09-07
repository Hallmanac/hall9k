using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The reading half of the task record: what <c>h9k task add --from-issue</c> does with an issue
/// that carries one (task: a published task's GitHub issue carries the whole task record). An issue
/// with no record is untouched by any of this and adopts exactly as it always did — title to
/// objective, body to context, criteria typed by the human — which is the contract every issue
/// filed by a person outside hall9k depends on.
/// <para>
/// Everything here happens once, at adoption. The record is not re-read afterwards and nothing
/// watches the issue: the adopting install owns its copy from that moment on (Decisions Log #60),
/// and the origin's later revisions reach it only by adopting again. The adoption output says so,
/// because a copy that silently ages is the one failure this whole feature could introduce.
/// </para>
/// </summary>
internal static class TaskRecordAdoption
{
    /// <summary>
    /// What the local store could and could not make of a record's cross-install references.
    /// Every "could not" is carried as a fact with the command that fixes it rather than dropped:
    /// an edge the adopting install cannot resolve is exactly the thing a human needs told.
    /// </summary>
    internal sealed record Resolution(
        IReadOnlyList<Guid> Dependencies,
        IReadOnlyList<int> UnresolvedIssues,
        Guid? EpicId,
        string? UnmatchedEpicTitle);

    /// <summary>
    /// What the command line already said. Each value here wins over the record: adopting another
    /// install's task is not surrendering the local call, and someone who typed <c>--objective</c>
    /// or <c>--criteria</c> meant it.
    /// </summary>
    internal sealed record Overrides(
        string? Objective,
        IReadOnlyList<string> Criteria,
        string? AgentContext,
        string? Type,
        string? Model,
        string? Epic,
        IReadOnlyList<string> BlockedBy);

    /// <summary>
    /// A cap line the record carried whose value this build's own floors reject, as the record
    /// wrote it: the key and the number, so the adoption output can say what it dropped and name
    /// the command that sets that one cap here.
    /// </summary>
    internal sealed record UnusableCap(string Key, int Value)
    {
        /// <summary>
        /// The command that sets this cap on the adopted task. The record's keys are the review-cap
        /// flags' own names, so the flag is the key; the session cap is the one that takes its value
        /// as an argument instead.
        /// </summary>
        public string Command(string shortId) =>
            Key == SessionCapKey
                ? $"h9k task set-session-cap {shortId} <cap>"
                : $"h9k task set-review-caps {shortId} --{Key} <n>";
    }

    /// <summary>The record's key for the session cap — the one cap with a command of its own.</summary>
    private const string SessionCapKey = "session-cap";

    /// <summary>
    /// The draft a record and this install's own resolution of it come to. <see cref="Type"/> and
    /// <see cref="Model"/> are null when the record said nothing the command line had not already
    /// said — the caller's own value stands rather than being restated here.
    /// <para>
    /// The three "unusable" fields are the record's degrade contract made reportable
    /// (<see cref="TaskRecord.CurrentVersion"/>): a field this build cannot use degrades to the
    /// caller's own value rather than failing the adoption, and the value it could not use is
    /// carried here so the adoption output names it. <see cref="UnrecognizedType"/> is a type this
    /// build has never heard of, <see cref="UnusableModel"/> a model name it will not spawn, and
    /// <see cref="UnusableCaps"/> the record's cap lines whose values sit outside this build's own
    /// floors — each one a case where a null field above is a degrade rather than "the command
    /// line already answered it".
    /// </para>
    /// </summary>
    internal sealed record Reconstruction(
        string Objective,
        IReadOnlyList<string> Criteria,
        string? AgentContext,
        string? Type,
        string? UnrecognizedType,
        string? Model,
        string? UnusableModel,
        Guid? EpicId,
        IReadOnlyList<Guid> Dependencies,
        TaskRecordCaps Caps,
        IReadOnlyList<UnusableCap> UnusableCaps);

    /// <summary>
    /// The record an imported item carries, or null when it carries none. Only GitHub issues carry
    /// one today; Jira is the next provider to (docs/scope.md), and it will arrive as another arm
    /// here rather than as a second reader.
    /// </summary>
    public static TaskRecord? Read(WorkItemProvider provider, ImportedWorkItem imported) =>
        provider == WorkItemProvider.GitHub
            ? TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(imported.Body))
            : null;

    /// <summary>
    /// The whole draft the record describes, once this install has resolved what it can of the
    /// record's cross-install references. Criteria become criteria rather than context — the one
    /// thing an ordinary adoption refuses to do, and the one thing a record makes honest, since
    /// somebody's owner wrote them as the readiness contract rather than as a description.
    /// <para>
    /// The agent context is the record's own, verbatim, with no imported-item framing wrapped
    /// around it: the text is the origin owner's agent context, not a stranger's issue description,
    /// and quoting it as source material would misdescribe who wrote it. A <c>--context</c> passed
    /// here follows it, the same ordering every other adoption uses for the operator's own words.
    /// </para>
    /// </summary>
    public static Reconstruction Reconstruct(
        TaskRecord record, Resolution resolution, Overrides given, string reference)
    {
        string? agentContext = given.AgentContext.IsNotBlank() && record.AgentContext.IsNotBlank()
            ? $"{record.AgentContext}\n\n{given.AgentContext}"
            : given.AgentContext.IsNotBlank() ? given.AgentContext : record.AgentContext;

        bool readsTheRecordsType = given.Type.IsBlank() && record.Type.IsNotBlank();
        string? recordType = readsTheRecordsType ? VetType(record.Type, reference) : null;
        bool readsTheRecordsModel = given.Model.IsBlank() && record.Model.IsNotBlank();
        (string? recordModel, string? unusableModel) =
            readsTheRecordsModel ? VetModel(record.Model) : (null, null);
        List<UnusableCap> unusableCaps = [];
        return new Reconstruction(
            given.Objective.IsNotBlank() ? given.Objective : record.Objective,
            given.Criteria.Count > 0 ? given.Criteria : record.Criteria,
            agentContext,
            recordType,
            readsTheRecordsType && recordType is null ? record.Type : null,
            recordModel,
            unusableModel,
            given.Epic.IsBlank() ? resolution.EpicId : null,
            given.BlockedBy.Count == 0 ? resolution.Dependencies : [],
            VetCaps(record.Caps, unusableCaps),
            unusableCaps);
    }

    /// <summary>
    /// The type a record names, whichever casing it was written in — the record's canonical casing
    /// is lowercase and either is accepted on read (the platform displays "Feature" while the record
    /// says "feature", and a block hand-written either way still adopts).
    /// <para>
    /// pr-review is the one type a record cannot hand over. A pr-review task reviews a pull request
    /// and carries that pull request as its reference; adopted off an issue it would be a review
    /// task pointed at something that is not a review, which the decider refuses downstream with a
    /// message about a flag this caller never passed. Refused here instead, naming the two routes
    /// that work.
    /// </para>
    /// <para>
    /// A type this build has never heard of answers null instead of refusing: a record written by a
    /// LATER build degrades to the fields this one understands rather than refusing the whole
    /// adoption (<see cref="TaskRecord.CurrentVersion"/>), so the draft takes this install's own
    /// type and <see cref="Reconstruction.UnrecognizedType"/> carries the word the record named for
    /// the adoption output to report. Never silently, which is the one way degrading would be worse
    /// than the refusal it replaces — a <c>TaskType.Parse</c> here used to fail the adoption
    /// outright, quoting a <c>--type</c> flag the operator never passed (independent pre-PR review,
    /// cycle 1, adversarial lens).
    /// </para>
    /// </summary>
    private static string? VetType(string recordType, string reference) =>
        TaskType.TryParse(recordType, out TaskType? parsed)
            ? parsed == TaskType.PrReview
                ? throw new DomainValidationException(
                    $"{ExternalText.OneLine(reference)}'s task record says type pr-review, "
                    + "and a pr-review task reviews an existing pull request rather than an issue. Adopt the "
                    + "pull request itself — h9k task add --project <name> --from-pr <url> — or adopt this "
                    + "issue as ordinary work by naming the type yourself: --type feature.")
                : recordType
            : null;

    /// <summary>
    /// The model a record names, or null when it names none this build can use — and the word it
    /// named, for the adoption output, in that second case. The same degrade
    /// <see cref="VetType"/> makes and for the same reason: a model name reaches the executor's
    /// shell command line, so <c>TaskDecider.VetModel</c> refuses an ill-formed one outright, and
    /// handing a record's value straight to it walled the whole adoption over a field the draft
    /// could perfectly well have taken from this install instead (independent pre-PR review,
    /// cycle 1 — the same defect the cap fields carried).
    /// <para>
    /// <see cref="AgentModel.Unknown"/> is not a degrade: it is what <c>default</c> — or any word
    /// this type reads as "no preference" — means, and a record saying that has stated nothing for
    /// the output to report.
    /// </para>
    /// </summary>
    private static (string? Model, string? Unusable) VetModel(string? recordModel)
    {
        AgentModel candidate = AgentModel.FromInput(recordModel);
        return candidate == AgentModel.Unknown
            ? (null, null)
            : candidate.IsWellFormed ? (candidate.Value, null) : (null, recordModel);
    }

    /// <summary>
    /// The record's caps with every value this build's own floors reject dropped to "no override",
    /// and each dropped line named in <paramref name="unusable"/> for the adoption output.
    /// <para>
    /// A record hall9k wrote can never carry such a value — the origin validated it at set time —
    /// so this is reachable only from a block somebody wrote or edited by hand, which is an
    /// explicitly supported shape (Decisions Log #151: ten issues were hand-annotated with a record
    /// block before this feature existed). Feeding those values straight into
    /// <c>TaskDecider.OverrideSessionCap</c>/<c>OverrideReviewCaps</c> threw, and the throw failed
    /// the entire adoption with a message quoting cap flags <c>h9k task add</c> does not even have
    /// — the exact failure shape the type field was fixed for, and the record's degrade contract
    /// says a field this build cannot use costs its own value and nothing more (independent pre-PR
    /// review, cycle 1, both lenses). The floors themselves stay in the domain, asked through the
    /// decider's own predicates, so this cannot drift from what the setters enforce.
    /// </para>
    /// </summary>
    private static TaskRecordCaps VetCaps(TaskRecordCaps caps, List<UnusableCap> unusable)
    {
        return new TaskRecordCaps(
            Vet(caps.MaxComplianceReviewCycles, "max-compliance-review-cycles", TaskDecider.IsUsablePerRunReviewCap),
            Vet(caps.MaxAdversarialReviewCycles, "max-adversarial-review-cycles", TaskDecider.IsUsablePerRunReviewCap),
            Vet(caps.MaxFinalFullPassRounds, "max-final-full-pass-rounds", TaskDecider.IsUsablePerRunReviewCap),
            Vet(
                caps.LifetimeReviewCycleBudget, "lifetime-review-cycle-budget",
                TaskDecider.IsUsableLifetimeReviewCycleBudget),
            Vet(caps.SessionCap, SessionCapKey, TaskDecider.IsUsableSessionCap));

        int? Vet(int? cap, string key, Func<int, bool> usable)
        {
            if (cap is not { } value)
            {
                return null;
            }

            if (usable(value))
            {
                return value;
            }

            unusable.Add(new UnusableCap(key, value));
            return null;
        }
    }

    /// <summary>
    /// Resolve the record's cross-install references against this install: each blocked-by issue
    /// number to the local task already linked to that issue, and the epic title to the local epic
    /// of that title.
    /// <para>
    /// Issue numbers, not task ids, are what the record carries, precisely because task ids differ
    /// per install by design and an issue number means the same thing on both. An issue nobody here
    /// has adopted yet resolves to nothing, and that is reported as an unresolved edge naming the
    /// command that adopts the parent first — never quietly dropped, and never invented as a
    /// dependency on whatever task happened to look similar.
    /// </para>
    /// </summary>
    public static async Task<Resolution> ResolveAsync(
        IQuerySession session,
        TaskRecord record,
        ExternalReference adoptedIssue,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        // The same reading of a reference the writing side makes, from the one place that owns it.
        string? repository = TaskRecordPublication.Repository(adoptedIssue);
        List<Guid> dependencies = [];
        List<int> unresolved = [];
        foreach (int issue in record.BlockedByIssues)
        {
            Guid? local = repository is null
                ? null
                : await FindTaskForIssueAsync(session, $"{repository}#{issue}", cancellationToken);
            if (local is { } dependencyId)
            {
                if (!dependencies.Contains(dependencyId))
                {
                    dependencies.Add(dependencyId);
                }

                continue;
            }

            unresolved.Add(issue);
        }

        Guid? epicId = null;
        string? unmatchedEpicTitle = null;
        if (record.EpicTitle.IsNotBlank())
        {
            // Matched by title within this project, and by exact title only: the record names the
            // epic the origin grouped the work under, and an epic here with a similar name is a
            // different record, not the same one. A title that matches nothing is reported with the
            // command that creates it (the Windows window did exactly that by hand for #247 on
            // 2026-09-07, which is what asked for this).
            IReadOnlyList<EpicDetails> epics = await session.Query<EpicDetails>()
                .Where(epic => epic.ProjectId == projectId)
                .ToListAsync(cancellationToken);
            EpicDetails? match = epics.FirstOrDefault(epic =>
                string.Equals(epic.Title, record.EpicTitle, StringComparison.OrdinalIgnoreCase)
                && epic.State == EpicState.Open);
            epicId = match?.Id;
            unmatchedEpicTitle = match is null ? record.EpicTitle : null;
        }

        return new Resolution(dependencies, unresolved, epicId, unmatchedEpicTitle);
    }

    /// <summary>
    /// The live task already linked to one issue, or null when none is. Abandoned holders do not
    /// count, the same reading <see cref="TaskAddCommand.RefuseSecondAdoptionAsync"/> makes of the
    /// same question: a task somebody walked away from will never close out, so an edge onto it
    /// would block forever.
    /// </summary>
    private static async Task<Guid?> FindTaskForIssueAsync(
        IQuerySession session, string reference, CancellationToken cancellationToken)
    {
        string canonical = new ExternalReference(WorkItemProvider.GitHub, reference).ToString();
        // TaskState is a value object and Marten cannot translate a comparison against one, so the
        // state filter is SQL against the stored string, the way every state filter in this repo is.
        TaskListItem? holder = await session.Query<TaskListItem>()
            .Where(task => task.ExternalReference == canonical)
            .Where(task => task.MatchesSql("d.data ->> 'state' <> ?", TaskState.Abandoned.Value))
            .OrderBy(task => task.AddedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return holder?.Id;
    }
}
