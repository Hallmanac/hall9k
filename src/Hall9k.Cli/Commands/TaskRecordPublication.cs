using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Writes the machine-readable task record into the GitHub issue a published task is tracked by,
/// and rewrites it when the task changes (task: a published task's GitHub issue carries the whole
/// task record). One writer for both moments, so publish and revise cannot drift into writing two
/// different records.
/// <para>
/// It always reads the issue's current body before writing, and replaces only the record section
/// inside it. That is the whole promise the feature makes to a human editing the issue on GitHub:
/// the prose above the record is theirs, and nothing here composes a body from scratch. The
/// checklist is regenerated only when the criteria actually changed, for the same reason.
/// </para>
/// <para>
/// The record is written after the issue is created and linked rather than as part of the create
/// body, and that ordering is load-bearing: the branch name in the record renders through the
/// project's own branch template, whose <c>{key}</c> token is the linked item's key — unknown until
/// the issue exists. Composed into the create body, a project templating <c>{key}</c> would publish
/// a record naming a branch nobody will ever cut.
/// </para>
/// </summary>
internal static class TaskRecordPublication
{
    /// <summary>
    /// What writing the record came to, for a caller that has to decide what to print.
    /// <see cref="NotTracked"/> covers every task with no GitHub issue to write into, which is the
    /// ordinary case for a project on backlog policy none.
    /// </summary>
    internal enum WriteOutcome
    {
        NotTracked,
        Written,
    }

    /// <summary>
    /// Compose the record for <paramref name="task"/> and rewrite it into its linked issue, leaving
    /// everything above the record section alone. Answers <see cref="WriteOutcome.NotTracked"/> —
    /// rather than throwing — whenever this task is not one whose record this install maintains, so
    /// every caller can call it unconditionally and none of them owns a copy of the rule.
    /// <para>
    /// Three conditions, and the last two are the ones worth stating. The project's backlog policy
    /// has to be <c>github-issues</c>: an adopted issue is routinely somebody else's, and a project
    /// that tracks nothing has said nothing about wanting hall9k to write into it. And the task must
    /// carry no <see cref="TaskOrigin"/> — a MIRROR never writes the record. Its issue belongs to
    /// the install that published it, and a mirror rewriting that block would overwrite the origin's
    /// own record with its local copy, wiping the origin fields every other install adopts by. One
    /// record, one writer, and the writer is whoever published the work.
    /// </para>
    /// <para>
    /// <paramref name="criteriaChanged"/> is the one thing this cannot work out for itself: the
    /// record always carries the current criteria, but the human-readable checklist above it is
    /// regenerated only when a revision actually replaced the set.
    /// </para>
    /// </summary>
    public static async Task<WriteOutcome> WriteAsync(
        IQuerySession session,
        TaskAggregate task,
        ProjectDetails project,
        Guid nodeId,
        string nodeName,
        DateTimeOffset now,
        bool criteriaChanged,
        GitHubWorkItemProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        if (task.ExternalReference is not { } reference || reference.Provider != WorkItemProvider.GitHub
            || project.BacklogPolicy != BacklogPolicy.GitHubIssues
            || task.Origin is not null)
        {
            return WriteOutcome.NotTracked;
        }

        GitHubWorkItemProvider github = provider ?? new GitHubWorkItemProvider();
        ImportedWorkItem issue = await github.ImportAsync(
            new WorkItemImportRequest(WorkItemProvider.GitHub, reference.Reference, project.RepositoryPath),
            cancellationToken);

        DateTimeOffset publishedAt = PublishStamp(issue.Body, now);
        TaskRecord record = await ComposeAsync(
            session, task, project, nodeId, nodeName, publishedAt, cancellationToken);
        string body = criteriaChanged
            ? GitHubIssueBody.WithCriteriaChecklist(issue.Body, task.AcceptanceCriteria)
            : issue.Body ?? string.Empty;

        await github.UpdateBodyAsync(
            reference, GitHubIssueBody.WithRecord(body, record), project.RepositoryPath, cancellationToken);
        return WriteOutcome.Written;
    }

    /// <summary>
    /// The publish stamp the record about to be written should carry: the one the issue already
    /// says, or <paramref name="now"/> when there is none to keep.
    /// <para>
    /// The stamp is the origin's, not this write's: a revision that rewrites the record has not
    /// republished the task, and moving the stamp would tell the next install to read this copy as
    /// newer work than it is. "None to keep" covers two shapes, though — an issue with no record
    /// section at all, and a record whose <c>published</c> line is missing or unreadable, which is
    /// what a hand-written block has. <see cref="TaskRecord.TryParse"/> answers
    /// <see cref="DateTimeOffset.MinValue"/> for the second shape because a READER must not claim
    /// it observed a publish time it never saw; carrying that value into a write would stamp the
    /// issue <c>published: 0001-01-01 00:00:00Z</c>, which is a false observation rather than an
    /// absent one (AGENTS.md, never guess at unobserved facts — external review on PR #276 caught
    /// the write). A writer is in the opposite position from a reader: this install is the one
    /// publishing, so the moment it writes is something it actually observed.
    /// </para>
    /// </summary>
    internal static DateTimeOffset PublishStamp(string? body, DateTimeOffset now) =>
        TaskRecord.TryParse(GitHubIssueBody.TryReadRecordYaml(body))?.Origin.PublishedAt is { } stamped
        && stamped != DateTimeOffset.MinValue
            ? stamped
            : now;

    /// <summary>
    /// The record as it stands for this task right now: the readiness contract, the agent context,
    /// the caps this task overrode, the epic by title, the dependency edges as issue numbers, and
    /// where it came from.
    /// </summary>
    public static async Task<TaskRecord> ComposeAsync(
        IQuerySession session,
        TaskAggregate task,
        ProjectDetails project,
        Guid nodeId,
        string nodeName,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        EpicDetails? epic = task.EpicId is { } epicId
            ? await session.LoadAsync<EpicDetails>(epicId, cancellationToken)
            : null;

        (IReadOnlyList<int> issues, int withoutIssues) =
            await DependencyIssuesAsync(session, task, cancellationToken);

        return new TaskRecord(
            project.Name,
            task.Type.Value,
            task.Objective,
            [.. task.AcceptanceCriteria],
            task.AgentContext,
            task.Model == AgentModel.Unknown ? null : task.Model.Value,
            task.PreApproval,
            issues,
            withoutIssues,
            epic?.Title,
            // The epic's own id travels beside the title as provenance only. It names nothing on the
            // adopting install — epics are local records with local ids — and adoption maps by title
            // or names the command that creates one (task 247 on 2026-09-07 is what asked for this:
            // the Windows window had to create the matching epic by hand).
            epic?.Id,
            new TaskRecordCaps(
                task.MaxComplianceReviewCycles,
                task.MaxAdversarialReviewCycles,
                task.MaxFinalFullPassRounds,
                task.LifetimeReviewCycleBudget,
                task.SessionCap),
            new TaskOrigin(nodeId, nodeName, task.Id, Branch(task, project), publishedAt));
    }

    /// <summary>
    /// This task's dependencies as issue numbers in the repository its own issue lives in, plus a
    /// count of the ones that could not be named that way at all.
    /// <para>
    /// A number alone only means something inside one repository, so a dependency tracked in a
    /// different one — or tracked nowhere, which is every unpublished or untracked blocker — is
    /// counted rather than written. Counting rather than dropping silently is the whole point: the
    /// adopting install can say "the origin had two more blockers it could not name here", which is
    /// a true statement, where a shorter list would be a false one.
    /// </para>
    /// </summary>
    private static async Task<(IReadOnlyList<int> Issues, int WithoutIssues)> DependencyIssuesAsync(
        IQuerySession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.BlockedBy.Count == 0)
        {
            return ([], 0);
        }

        string? repository = Repository(task.ExternalReference);
        Dictionary<Guid, TaskDetails> blockers =
            (await session.LoadManyAsync<TaskDetails>(cancellationToken, task.BlockedBy))
            .ToDictionary(blocker => blocker.Id);
        List<int> issues = [];
        int withoutIssues = 0;
        // One round trip for the whole set, then the edges are worked out in memory — but still in
        // BlockedBy's own order, so rewriting the record of a task nobody changed produces the same
        // list of numbers rather than a reshuffled one (external review on PR #276 asked for the
        // single load; the ordering is why it is a lookup rather than a projection of the results).
        // A blocker with no document loads to nothing and counts as one that cannot be named here,
        // which is the same answer the per-dependency load gave.
        foreach (Guid dependencyId in task.BlockedBy)
        {
            TaskDetails? dependency = blockers.GetValueOrDefault(dependencyId);
            ExternalReference? reference = dependency?.ExternalReference.IsNotBlank() == true
                ? ExternalReference.Parse(dependency.ExternalReference)
                : null;
            if (reference is not null && reference.Provider == WorkItemProvider.GitHub
                && Repository(reference) == repository
                && int.TryParse(reference.Key, out int number) && number > 0)
            {
                issues.Add(number);
                continue;
            }

            withoutIssues++;
        }

        return (issues, withoutIssues);
    }

    /// <summary>
    /// The repository half of a GitHub reference — the <c>owner/repo</c> in
    /// <c>owner/repo#42</c> — or null when the reference carries none.
    /// <see cref="ExternalReference.Key"/> is the other half; this is deliberately not a property
    /// on that type, because "the part before the #" is only a repository for GitHub and is nothing
    /// at all for a Jira key.
    /// </summary>
    internal static string? Repository(ExternalReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        int hash = reference.Reference.LastIndexOf('#');
        return hash <= 0 ? null : reference.Reference[..hash];
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
        catch (Domain.Shared.Exceptions.DomainException)
        {
            return null;
        }
    }
}
