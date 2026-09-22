namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Where the rendered decisions and lessons land, in one place (idea d805fd8b, piece 2). Two
/// names, used in two kinds of directory: a project's home, where they sit beside the generated
/// <c>AGENTS.md</c> as part of the home's always-there shape, and a dispatched run's worktree,
/// where the daemon writes them at dispatch and git is told to ignore them
/// (<c>Hall9k.Connectors.Worktrees.WorktreeExcludeFile</c>) so a generated projection can never
/// be committed into authored history.
/// <para>
/// Not folded into <see cref="ProjectHomePaths"/>, which names them for a home: a worktree is not
/// a project home, and the two directories have to agree on the file names or an agent told to
/// read <c>decisions.md</c> in its worktree would find a different file from the one the home
/// renders.
/// </para>
/// </summary>
public static class KnowledgeDocumentPaths
{
    /// <summary>The rendered binding decisions. Cited by the ids inside it, never by its own line numbers.</summary>
    public const string DecisionsFileName = "decisions.md";

    /// <summary>The rendered live lessons, the decisions file's sibling.</summary>
    public const string LessonsFileName = "lessons.md";

    public static string DecisionsFileIn(string directory) => Path.Combine(directory, DecisionsFileName);

    public static string LessonsFileIn(string directory) => Path.Combine(directory, LessonsFileName);
}
