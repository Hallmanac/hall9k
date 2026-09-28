namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The rule set the Security persona's own docs-only skip (idea 6be68ee2, phase two) actually
/// classifies against — <see cref="Projections.ProjectDetails.EffectiveNonExecutablePaths"/> with
/// <see cref="AlwaysReviewed"/> layered on top as leading-<c>!</c> exclusions, so each one wins
/// over any positive rule at the same path regardless of which layer contributed it
/// (<see cref="NonExecutablePathClassifier"/>'s own exclusion precedence).
/// <para>
/// <see cref="NonExecutablePathDefaults"/> exists to decide whether a diff can break the build or
/// test gates (Decisions Log #252), and a project may add to it freely — both are the right
/// answer for that question. The Security persona hunts a different class of defect, and content
/// that is safely skippable for the gates is exactly the unsafe-process and supply-chain surface
/// this review exists to read: a CI or release workflow file a project chose to mark
/// non-executable for gate-skipping (<c>--security-review</c>'s own help text and
/// docs/concepts.md promise it is never skipped on that account), or a repository skill or
/// command this install publishes host-wide (AGENTS.md's own skill-tier doctrine) — reusing the
/// gate's own effective set for this persona let a project's own gate-skipping addition silently
/// skip the one review meant to catch it (independent pre-PR review, cycle 1, both lenses).
/// </para>
/// </summary>
public static class SecurityReviewNonExecutablePathRules
{
    /// <summary>
    /// Always reviewed, regardless of the compiled build/test defaults or any project addition:
    /// a CI or release workflow file, a repository skill or command (agent-executed content, not
    /// docs), and CLAUDE.md (agent-loaded instructions every future session in this repository
    /// reads, unlike an ordinary markdown file the bare <c>*.md</c> default skips safely for the
    /// gates).
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysReviewed =
    [
        ".github/workflows/",
        ".claude/skills/",
        ".claude/commands/",
        "CLAUDE.md",
    ];

    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> effectiveNonExecutablePaths) =>
        [.. effectiveNonExecutablePaths, .. AlwaysReviewed.Select(path => "!" + path)];
}
