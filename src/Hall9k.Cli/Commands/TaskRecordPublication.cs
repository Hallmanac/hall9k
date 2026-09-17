using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Writes a task's record into the ledger (idea 202383dc, A3a) — <c>records/&lt;task-id&gt;.yaml</c>
/// on <c>refs/hall9k/ledger/records</c> (<see cref="LedgerRefRegistry.RecordPath"/>) — on publish and
/// on every revise that changes anything the record carries. One writer for both moments, so
/// publish and revise cannot drift into writing two different records.
/// <para>
/// The record is a PROJECTION, composed fresh from the task's current state every time
/// (<see cref="ComposeAsync"/>): it is rebuildable from the events at any time and is never itself
/// the event log. The one field this never composes is <see cref="TaskRecordHolder"/> — the holder
/// lock A3b writes — which is read back from whatever the ledger already held for this task and
/// carried through unchanged, so a publish or a revise can never invent or clear a claim that is
/// not its own to make.
/// </para>
/// <para>
/// A task adopted from a GitHub issue or a Jira card once carried this whole record inside a
/// collapsed section of the issue's own body; that section is retired (idea 202383dc, A3a, ruled
/// 2026-09-12): the issue keeps only the human text it always had, and the record lives here
/// instead, readable by every node that shares the project rather than only by whichever install
/// happens to read that one tracker item.
/// </para>
/// </summary>
internal static class TaskRecordPublication
{
    /// <summary>How many times a conflicting ledger write retries against a fresh read before giving up.</summary>
    private const int MaxConflictRetries = 5;

    /// <summary>
    /// What writing the record came to, for a caller that has to decide what to print.
    /// <see cref="Mirror"/> covers every task whose record belongs to the install that published
    /// it, never to a copy adopted here (task.Origin is not null) — this install's own copy would
    /// otherwise overwrite the origin's record with stale local facts. <see cref="NotYetPublished"/>
    /// covers a Draft this install has never published: see this method's own remarks on why it
    /// still needs the ledger's own answer, not just the task's current state, to tell that apart
    /// from a once-published task a human returned to Draft to keep revising.
    /// </summary>
    internal enum WriteOutcome
    {
        Mirror,
        Written,
        NotYetPublished,
    }

    /// <summary>
    /// Compose the record for <paramref name="task"/> and write it to the ledger, preserving
    /// whatever holder block the record already carried. Answers <see cref="WriteOutcome.Mirror"/> —
    /// rather than throwing — for a task this install never published, so every caller can call
    /// this unconditionally and none of them owns a copy of the mirror rule.
    /// <para>
    /// Every project gets a record for every task it publishes, regardless of whether or how it
    /// tracks its backlog (Brian, 2026-09-13: "a task with no tracker item gets a record too") —
    /// unlike the retired issue-block writer, there is no backlog-policy gate here at all, only the
    /// mirror check above.
    /// </para>
    /// <para>
    /// A Draft never gets a first record written here: <c>h9k task revise</c> only touches a
    /// record-carrying field while a task is Draft (<c>TaskDecider.Revise</c>'s own Draft-only
    /// gate), so a naive "task.State == Draft means skip" reads as though EVERY record-touching
    /// revise should skip — including a once-published task <c>h9k task draft</c> returned to
    /// Draft to keep revising, whose record already exists and must keep tracking it (independent
    /// pre-PR review, cycle 1, both lenses). The ledger's own answer — whether a record already
    /// exists at this task's path — is what actually tells the two apart, since state alone
    /// cannot: a fresh Draft has no record to invent a false <c>published</c> stamp for, while a
    /// returned-to-Draft task's existing record still needs the write to carry the revision.
    /// </para>
    /// </summary>
    public static async Task<WriteOutcome> WriteAsync(
        IQuerySession session,
        TaskAggregate task,
        ProjectDetails project,
        Guid nodeId,
        string nodeName,
        string ownerFingerprint,
        DateTimeOffset now,
        ILedger ledger,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        CancellationToken cancellationToken = default)
    {
        if (task.Origin is not null)
        {
            return WriteOutcome.Mirror;
        }

        string refName = LedgerRefRegistry.Records.RefspecSource;
        string path = LedgerRefRegistry.RecordPath(task.Id);

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(project.RepositoryPath, refName, path, cancellationToken);
            if (task.State == TaskState.Draft && !current.Exists)
            {
                return WriteOutcome.NotYetPublished;
            }

            TaskRecord? existing = TaskRecord.TryParse(current.Content);
            DateTimeOffset publishedAt = PublishStamp(existing, now);
            TaskRecord record = await ComposeAsync(
                session, task, project, nodeId, nodeName, ownerFingerprint, publishedAt, existing?.Holder,
                cancellationToken);
            string content = record.ToYaml();
            if (current.Content == content)
            {
                return WriteOutcome.Written;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    project.RepositoryPath, refName, path, content, current.BlobId,
                    current.Exists ? $"Revise task record {task.Id}" : $"Publish task record {task.Id}",
                    committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return WriteOutcome.Written;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this write after {MaxConflictRetries} attempts — "
            + "something else is writing this task's record at the same time. Re-run once that settles.");
    }

    /// <summary>
    /// The publish stamp the record about to be written should carry: the one the existing ledger
    /// record already says, or <paramref name="now"/> when there is none to keep.
    /// <para>
    /// The stamp is the origin's, not this write's: a revision that rewrites the record has not
    /// republished the task, and moving the stamp would tell another node this copy is newer work
    /// than it is. <see cref="DateTimeOffset.MinValue"/> is what a reader answers for a stamp it
    /// never actually observed (<c>TaskRecord</c>'s own <c>Stamp(string?)</c> doc), so it is never
    /// carried forward as though it had been.
    /// </para>
    /// </summary>
    internal static DateTimeOffset PublishStamp(TaskRecord? existing, DateTimeOffset now) =>
        existing?.Origin.PublishedAt is { } stamped && stamped != DateTimeOffset.MinValue ? stamped : now;

    /// <summary>
    /// The record as it stands for this task right now: the readiness contract, the agent context,
    /// the caps this task overrode, the epic by id and title, the dependency edges by task id, and
    /// where it came from. <paramref name="holder"/> is carried through verbatim — see this type's
    /// own remarks on why the writer never composes one itself.
    /// </summary>
    public static async Task<TaskRecord> ComposeAsync(
        IQuerySession session,
        TaskAggregate task,
        ProjectDetails project,
        Guid nodeId,
        string nodeName,
        string ownerFingerprint,
        DateTimeOffset publishedAt,
        TaskRecordHolder? holder,
        CancellationToken cancellationToken)
    {
        EpicDetails? epic = task.EpicId is { } epicId
            ? await session.LoadAsync<EpicDetails>(epicId, cancellationToken)
            : null;

        return new TaskRecord(
            task.Id,
            project.Name,
            task.Type.Value,
            task.Objective,
            [.. task.AcceptanceCriteria],
            task.AgentContext,
            task.Model == AgentModel.Unknown ? null : task.Model.Value,
            task.PreApproval,
            task.ExternalReference,
            [.. task.BlockedBy],
            epic?.Title,
            epic?.Id,
            new TaskRecordCaps(
                task.MaxComplianceReviewCycles,
                task.MaxAdversarialReviewCycles,
                task.MaxFinalFullPassRounds,
                task.LifetimeReviewCycleBudget,
                task.SessionCap),
            ownerFingerprint,
            new TaskOrigin(nodeId, nodeName, task.Id, Branch(task, project), publishedAt),
            holder);
    }

    /// <summary>
    /// This node's own identity for a ledger commit — its committer line, its signing key, and the
    /// owner fingerprint a record's <c>origin-owner-fingerprint</c> names — gathered the one way
    /// <c>h9k project join</c> already does it (<c>ProjectJoinCommand.RunAsync</c>'s own committer
    /// and <c>claimedFingerprint</c>): the owner's own name and email when known, the node's key
    /// falling back to standing in for the fingerprint when this owner has not claimed a root yet.
    /// Both <see cref="TaskPublishCommand"/> and <see cref="TaskReviseCommand"/> need exactly this
    /// before they can call <see cref="WriteAsync"/>, so it lives here rather than being copied
    /// twice.
    /// </summary>
    public static async Task<(LedgerCommitter Committer, LedgerSigningKey SigningKey, string OwnerFingerprint)>
        ResolveIdentityAsync(IQuerySession session, BootstrapContext context, CancellationToken cancellationToken)
    {
        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(context.OwnerId, cancellationToken);
        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);
        LedgerCommitter committer = new(
            owner?.Name.IsNotBlank() == true ? owner.Name : Environment.UserName,
            owner?.Email.IsNotBlank() == true ? owner.Email : $"{context.NodeId}@hall9k.local");
        return (committer, new LedgerSigningKey(key.PrivateKeyPath), owner?.RootFingerprint ?? key.Fingerprint);
    }

    /// <summary>
    /// The branch this task's work is cut under, rendered from the project's own template. Null when
    /// the template refuses to render — a name too long for the objective it slugs, say — rather
    /// than a guess at what git would have accepted: an adopting install reading a branch name that
    /// does not exist is worse off than one told there is none.
    /// </summary>
    private static string? Branch(TaskAggregate task, ProjectDetails project)
    {
        try
        {
            return project.BranchNameTemplate.Render(
                task.Id, task.Objective, task.ExternalReference?.Key);
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
