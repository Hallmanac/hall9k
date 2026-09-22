using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.ProjectHomes;

/// <summary>One project's two rendered documents, held together because nothing ever wants one without the other.</summary>
public sealed record RenderedKnowledgeDocuments(string Decisions, string Lessons);

/// <summary>
/// What one directory's write actually did. <see cref="SkippedForeignFiles"/> is never empty
/// silently: a file of the same name that is not this platform's own render is left exactly as it
/// is and named to the caller, because a repository that tracks its own <c>decisions.md</c> must
/// not have it replaced by a projection.
/// </summary>
public sealed record KnowledgeDocumentWriteResult(int Written, IReadOnlyList<string> SkippedForeignFiles);

/// <summary>
/// The decisions and lessons documents as files on disk (idea d805fd8b, piece 2): rendered from
/// the store, written into a project's home by the render sweep and into every dispatched run's
/// worktree by <c>RunLauncher</c>, and never committed anywhere.
/// <para>
/// Static rather than an injected service, for the same reason <see cref="HomeEntryWriter"/> is:
/// this is a pure function of the store's state plus a directory, with no state of its own to
/// hold between calls, and two callers on opposite sides of the daemon (a background sweep and
/// the launch path) reach it without either of them growing a constructor parameter.
/// </para>
/// </summary>
public static class KnowledgeDocuments
{
    /// <summary>
    /// Renders both documents for <paramref name="projectId"/>. Only project-scoped records are
    /// read: an owner-scoped decision or lesson is a cross-project habit that belongs to the
    /// owner rather than to any one project's home, and widening it into every project's file is
    /// backlog 55's prompt-injection work, not this render's.
    /// <para>
    /// The scope predicate goes through <c>MatchesSql</c> against the document's own jsonb field,
    /// the form <c>h9k decide list</c> and <c>DispatchEngine</c>'s queued-task query already use:
    /// <see cref="KnowledgeScope"/> is a value object behind a JsonConverter and Marten's Linq
    /// provider has no translation for the record type itself (TASK-MODEL.md §8). Status is
    /// deliberately NOT filtered here — which records still bind, and which lessons are still
    /// live, is the renderers' own rule and lives in one place.
    /// </para>
    /// </summary>
    public static async Task<RenderedKnowledgeDocuments> RenderAsync(
        IQuerySession query, Guid projectId, CancellationToken cancellationToken)
    {
        string projectScope = KnowledgeScope.Project;
        IReadOnlyList<DecisionDetails> decisions = await query.Query<DecisionDetails>()
            .Where(decision => decision.MatchesSql("d.data ->> 'scope' = ?", projectScope))
            .Where(decision => decision.ScopeId == projectId)
            .ToListAsync(cancellationToken);
        IReadOnlyList<LearningDetails> lessons = await query.Query<LearningDetails>()
            .Where(lesson => lesson.MatchesSql("d.data ->> 'scope' = ?", projectScope))
            .Where(lesson => lesson.ScopeId == projectId)
            .ToListAsync(cancellationToken);

        return new RenderedKnowledgeDocuments(
            DecisionsDocumentRenderer.Render(decisions), LessonsDocumentRenderer.Render(lessons));
    }

    /// <summary>
    /// Writes both documents into <paramref name="directory"/>, touching disk only where the bytes
    /// actually differ — the same "a render is a pure function, so write only what changed" rule
    /// <see cref="HomeEntryWriter"/> holds for a task or an idea.
    /// <para>
    /// A file already sitting at either name that does not carry that renderer's own generated
    /// marker is left alone and reported. A project home is the platform's own directory and will
    /// never have one, but a worktree is somebody's repository: a project that genuinely tracks a
    /// root <c>decisions.md</c> would otherwise have it overwritten on every dispatch and show up
    /// as a modified tracked file in the session's own diff. Declining to write and saying so is
    /// the same trade <c>ReviewLapGuardFile</c> already makes for a checkout's settings file.
    /// </para>
    /// </summary>
    public static KnowledgeDocumentWriteResult WriteInto(string directory, RenderedKnowledgeDocuments documents)
    {
        Directory.CreateDirectory(directory);
        List<string> skipped = [];
        int written = 0;
        written += WriteOne(
            KnowledgeDocumentPaths.DecisionsFileIn(directory), documents.Decisions,
            DecisionsDocumentRenderer.GeneratedMarker, skipped);
        written += WriteOne(
            KnowledgeDocumentPaths.LessonsFileIn(directory), documents.Lessons,
            LessonsDocumentRenderer.GeneratedMarker, skipped);
        return new KnowledgeDocumentWriteResult(written, skipped);
    }

    /// <summary>
    /// Tells <paramref name="checkoutPath"/>'s repository to ignore both documents, so a generated
    /// projection never reads as untracked work a session was supposed to commit (the platform's
    /// own left-behind check warns about exactly that) and can never land in authored history.
    /// False when the repository's exclude list could not be resolved at all, which the caller
    /// says out loud rather than writing the files and leaving them to show up in `git status`.
    /// </summary>
    public static async Task<bool> EnsureIgnoredAsync(string checkoutPath, CancellationToken cancellationToken)
    {
        if (WorktreeExcludeFile.Resolve(checkoutPath) is not { } excludeFile)
        {
            return false;
        }

        string? existing = File.Exists(excludeFile) ? await File.ReadAllTextAsync(excludeFile, cancellationToken) : null;
        if (WorktreeExcludeFile.Compose(
                existing,
                [KnowledgeDocumentPaths.DecisionsFileName, KnowledgeDocumentPaths.LessonsFileName])
            is not { } updated)
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
        await AtomicFileWrite.WriteAllTextAsync(excludeFile, updated, cancellationToken);
        return true;
    }

    private static int WriteOne(string path, string rendered, string generatedMarker, List<string> skipped)
    {
        string? current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (current == rendered)
        {
            return 0;
        }

        if (current is not null && !current.Contains(generatedMarker, StringComparison.Ordinal))
        {
            skipped.Add(path);
            return 0;
        }

        File.WriteAllText(path, rendered);
        return 1;
    }
}
