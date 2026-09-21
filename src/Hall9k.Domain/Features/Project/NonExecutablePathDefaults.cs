namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The compiled non-executable-path rule set every project starts with (task: a delivered diff
/// that touches no buildable or testable source skips the build and test gates — origin:
/// ef2fefe5, a two-file skill markdown fix paying roughly twelve minutes of build-and-test
/// ceremony on every pipeline entry while the actual work took four). A project may only add to
/// this list (<c>h9k project set --non-executable-path</c>), never remove or narrow one of these
/// entries — there is no command that touches this list itself, and <c>ProjectDecider</c> refuses
/// a project addition that starts with <c>!</c> outright (exclusion syntax stays reserved for this
/// compiled set), so the constraint holds by construction and by a runtime check together. See
/// <see cref="NonExecutablePathClassifier"/> for how a rule here matches a changed path, including
/// the leading-<c>!</c> exclusion syntax three of these entries use.
/// </summary>
public static class NonExecutablePathDefaults
{
    /// <summary>
    /// The bare <c>*.md</c> rule matches a markdown file anywhere in the tree, which is what makes
    /// the three exclusions below necessary: content that looks like docs but is not, or that is
    /// docs this platform's own doctrine relies on tooling to keep honest.
    /// <list type="bullet">
    /// <item>This repository's own <c>.claude/templates/</c> is prompt source the product loads at
    /// runtime (<c>PromptTemplates.ResolvePath</c>) and tests check byte-for-byte
    /// (<c>AgentPromptBuilderGoldenTests</c>, <c>PromptTemplateContractTests</c>) — content that
    /// looks like docs but is not (independent pre-PR review, cycle 1, adversarial lens, high).</item>
    /// <item><c>AGENTS.md</c> and <c>PLAN.md</c> are the two guidance files this platform's own
    /// doctrine treats as load-bearing rather than descriptive — this repository's own
    /// <c>AgentsMarkdownLineCountTests</c> checks <c>AGENTS.md</c> against a line ceiling and
    /// <c>DecisionsLogNumberingGuardTests</c> checks <c>PLAN.md</c>'s own Decisions Log numbering,
    /// and a diff that only edits one of them is exactly the shape those tests exist to catch
    /// (independent pre-PR review, cycle 1, adversarial lens, high). Excluded by bare file name
    /// rather than a root-anchored path, matching every other project's own copy of these files as
    /// conservatively as this repository's, since the same compiled set applies to all of them and
    /// a project cannot add this exclusion for itself once the leading-<c>!</c> syntax is refused
    /// in a project's own additions below.</item>
    /// </list>
    /// Each leading <c>!</c> carves its target back out so an edit there still runs the gates that
    /// would catch it, regardless of which other rule would otherwise have matched it.
    /// </summary>
    public static readonly IReadOnlyList<string> Rules =
    [
        "*.md",
        "docs/",
        ".claude/skills/",
        ".claude/commands/",
        "!.claude/templates/",
        "!AGENTS.md",
        "!PLAN.md",
    ];
}
