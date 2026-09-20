namespace Hall9k.Connectors.RunSkills;

/// <summary>
/// One file the survey found, with the reason it is worth a composing session's attention.
/// <see cref="Path"/> is relative to the repository root, because that is what a run skill cites
/// and what a reader on another machine can actually follow.
/// </summary>
public sealed record RunSkillEvidence(string Path, string Why);

/// <summary>
/// What a mechanical scan of a repository found that bears on how to run it (idea b9b09779,
/// piece 4).
/// <para>
/// <see cref="LaunchCoverage"/> is the scan's own reading of which files already cover launching
/// the project — a hint the composing session is told to verify, never a verdict: the pointer /
/// full-text call is the agent's, made from the files themselves. <see cref="Documentation"/> and
/// <see cref="BuildFiles"/> are everything else worth reading. <see cref="HasAnything"/> is the
/// one thing the scan decides outright, because it needs no judgment at all: a repository with
/// none of the three has nothing for a session to read, and dispatching one to discover that
/// would spend a session to learn what the scan already knows.
/// </para>
/// </summary>
public sealed record RunSkillSurvey(
    IReadOnlyList<RunSkillEvidence> LaunchCoverage,
    IReadOnlyList<RunSkillEvidence> Documentation,
    IReadOnlyList<RunSkillEvidence> BuildFiles)
{
    public bool HasAnything => LaunchCoverage.Count > 0 || Documentation.Count > 0 || BuildFiles.Count > 0;

    /// <summary>Every file found, launch coverage first — the order a composing session should read them in.</summary>
    public IReadOnlyList<RunSkillEvidence> All => [.. LaunchCoverage, .. Documentation, .. BuildFiles];
}

/// <summary>
/// The mechanical half of run-skill discovery (idea b9b09779, piece 4): walks a repository
/// worktree for the files criterion 1 names — the README, docs, AGENTS.md, CLAUDE.md, skills, and
/// build files — and hands what it found to the prompt. Tools before tokens: a session is never
/// asked to go looking for files a directory listing answers, and is never dispatched at all when
/// there is nothing to find.
/// <para>
/// Read-only and bounded on purpose. It does not recurse the whole tree: a repository's launch
/// story lives at the root, under <c>docs/</c>, or in a skill, and walking every directory of a
/// large checkout to find a build file that the root already names would cost far more than it
/// tells anyone. <see cref="LookedFor"/> states exactly what was checked, which is what the
/// none-discoverable skill prints so a human can tell "there is nothing here" apart from "the
/// platform looked in the wrong place".
/// </para>
/// </summary>
public static class RunSkillRepositorySurvey
{
    /// <summary>
    /// Root files that are a briefing of some kind. Case-insensitively matched against the actual
    /// filenames on disk, since a repository may carry <c>Readme.md</c> or <c>readme.md</c>.
    /// </summary>
    private static readonly string[] RootDocumentationFiles =
        ["README.md", "README", "README.rst", "AGENTS.md", "CLAUDE.md", "CONTRIBUTING.md", "DEVELOPMENT.md"];

    /// <summary>
    /// Root files that say how the project is built or run by a tool rather than by prose. Listed
    /// rather than pattern-matched so the set is reviewable; the two globs below cover the
    /// language ecosystems whose manifest filename varies.
    /// </summary>
    private static readonly string[] RootBuildFiles =
    [
        "package.json", "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml",
        "Dockerfile", "Makefile", "justfile", "Taskfile.yml", "Procfile",
        "pyproject.toml", "requirements.txt", "Cargo.toml", "go.mod", "pom.xml", "build.gradle",
        "build.gradle.kts", "Gemfile", "mix.exs",
    ];

    /// <summary>Root build-file globs for ecosystems whose manifest carries the project's own name.</summary>
    private static readonly string[] RootBuildGlobs = ["*.slnx", "*.sln", "*.csproj"];

    /// <summary>
    /// The words that make a file's own name look like it covers launching the project. Matched
    /// against a path, case-insensitively — a hint for the session to verify, never a conclusion
    /// (a <c>docs/running-in-production.md</c> may describe a deployment nobody can reproduce
    /// locally, and only reading it settles that).
    /// </summary>
    private static readonly string[] LaunchWords =
        ["run", "launch", "start", "serve", "dev", "develop", "setup", "getting-started", "quickstart", "local"];

    /// <summary>Directories under the repository root whose markdown is worth reading, in order.</summary>
    private static readonly string[] DocumentationDirectories = ["docs", "doc"];

    /// <summary>
    /// A plain-English list of everywhere this survey looks, for the none-discoverable skill to
    /// print. Stated from the same constants the scan itself uses, so the two can never disagree
    /// about what was checked.
    /// </summary>
    public static IReadOnlyList<string> LookedFor =>
    [
        $"briefings at the repository root ({string.Join(", ", RootDocumentationFiles)})",
        $"build files at the repository root ({string.Join(", ", [.. RootBuildFiles, .. RootBuildGlobs])})",
        $"markdown under {string.Join("/ or ", DocumentationDirectories)}/",
        "skills under .claude/skills/*/SKILL.md",
    ];

    /// <summary>
    /// Surveys <paramref name="worktreePath"/>. A directory that does not exist surveys as empty
    /// rather than throwing: "the repository is not materialised here" and "the repository says
    /// nothing about running itself" are different facts, and the caller
    /// (<c>RunSkillSweepEngine</c>) is the one that knows which it is looking at — it checks the
    /// directory itself before ever calling this.
    /// </summary>
    public static RunSkillSurvey Survey(string worktreePath)
    {
        if (!Directory.Exists(worktreePath))
        {
            return new RunSkillSurvey([], [], []);
        }

        List<RunSkillEvidence> launchCoverage = [];
        List<RunSkillEvidence> documentation = [];
        List<RunSkillEvidence> buildFiles = [];

        // Enumerated once, up front, and matched case-insensitively below: a repository that
        // carries Readme.md rather than README.md still has its briefing found on Linux, and the
        // name that goes into the survey (and so into the run skill's own citation) is the one
        // actually on disk, which is the only spelling a reader on a case-sensitive filesystem
        // can follow.
        Dictionary<string, string> rootFiles = RootFilesByName(worktreePath);

        foreach (RunSkillEvidence skill in Skills(worktreePath))
        {
            launchCoverage.Add(skill);
        }

        foreach (string name in RootDocumentationFiles)
        {
            if (!rootFiles.TryGetValue(name, out string? actual))
            {
                continue;
            }

            RunSkillEvidence evidence = new(actual, "a briefing at the repository root");
            if (MentionsLaunching(Path.Combine(worktreePath, actual)))
            {
                launchCoverage.Add(evidence with { Why = "a root briefing whose own headings mention launching" });
            }
            else
            {
                documentation.Add(evidence);
            }
        }

        foreach (RunSkillEvidence document in DocumentationFiles(worktreePath))
        {
            if (LooksLikeLaunchPath(document.Path))
            {
                launchCoverage.Add(document with { Why = "a documentation file whose own name reads like a launch guide" });
            }
            else
            {
                documentation.Add(document);
            }
        }

        foreach (string name in RootBuildFiles)
        {
            if (rootFiles.TryGetValue(name, out string? actual))
            {
                buildFiles.Add(new RunSkillEvidence(actual, "a build or run manifest at the repository root"));
            }
        }

        foreach (string pattern in RootBuildGlobs)
        {
            foreach (string file in SafeEnumerateFiles(worktreePath, pattern, SearchOption.TopDirectoryOnly))
            {
                buildFiles.Add(new RunSkillEvidence(
                    Path.GetFileName(file), "a build or run manifest at the repository root"));
            }
        }

        return new RunSkillSurvey(
            launchCoverage,
            documentation,
            // Deduplicated by path: the globs and the named list can both reach the same file
            // (a repository whose solution file is literally named after one of the names above),
            // and one file listed twice in a prompt reads as two.
            [.. buildFiles.DistinctBy(found => found.Path, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Every file directly in the repository root, keyed by filename, case-insensitively, with
    /// the real on-disk name as the value. One enumeration rather than one per candidate, and
    /// case-insensitive rather than left to the filesystem's own rule, so a <c>Readme.md</c> is
    /// found on Linux as readily as on Windows. A duplicate key can only arise on a filesystem
    /// that genuinely holds two files differing by case alone, where the first wins rather than
    /// throwing: that is a pathological repository, and surveying one of the two briefings is a
    /// better answer than surveying nothing.
    /// </summary>
    private static Dictionary<string, string> RootFilesByName(string worktreePath)
    {
        Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in SafeEnumerateFiles(worktreePath, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            byName.TryAdd(name, name);
        }

        return byName;
    }

    /// <summary>
    /// Every <c>.claude/skills/&lt;name&gt;/SKILL.md</c>. All of them count as launch coverage
    /// candidates regardless of name: a repo-resident skill is the single most likely place a
    /// team already wrote down how to stand the project up, and the composing session reads each
    /// one to find out whether this particular skill actually does.
    /// </summary>
    private static IEnumerable<RunSkillEvidence> Skills(string worktreePath)
    {
        string skillsRoot = Path.Combine(worktreePath, ".claude", "skills");
        if (!Directory.Exists(skillsRoot))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(skillsRoot).Order(StringComparer.Ordinal))
        {
            string skillFile = Path.Combine(directory, "SKILL.md");
            if (File.Exists(skillFile))
            {
                yield return new RunSkillEvidence(
                    Relative(worktreePath, skillFile),
                    $"a repository skill ({Path.GetFileName(directory)}) that may already cover launching this project");
            }
        }
    }

    private static IEnumerable<RunSkillEvidence> DocumentationFiles(string worktreePath)
    {
        foreach (string name in DocumentationDirectories)
        {
            string directory = Path.Combine(worktreePath, name);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in SafeEnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal))
            {
                yield return new RunSkillEvidence(Relative(worktreePath, file), $"a {name}/ page");
            }
        }
    }

    private static bool LooksLikeLaunchPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return LaunchWords.Any(word => name.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a briefing's own markdown headings mention launching. Headings only, never the
    /// whole body: a README that says "run the tests" in passing has not documented how to launch
    /// the project, and treating every mention as coverage would make the hint meaningless. Read
    /// best-effort — a file this cannot open simply does not raise the hint.
    /// </summary>
    private static bool MentionsLaunching(string file)
    {
        try
        {
            foreach (string line in File.ReadLines(file))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith('#'))
                {
                    continue;
                }

                string heading = trimmed.TrimStart('#').Trim().ToLowerInvariant();
                if (LaunchWords.Any(word => heading.Contains(word, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Enumeration that answers empty rather than throwing for a directory that vanished, or one
    /// this process cannot read. A survey runs against a live worktree another process may be
    /// touching, and a partial survey is a usable one — an unhandled throw here would fail the
    /// whole discovery over a single unreadable directory.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern, SearchOption option)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, option).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A path relative to the repository root, always with forward slashes: what a run skill cites.</summary>
    private static string Relative(string worktreePath, string file) =>
        Path.GetRelativePath(worktreePath, file).Replace('\\', '/');
}
