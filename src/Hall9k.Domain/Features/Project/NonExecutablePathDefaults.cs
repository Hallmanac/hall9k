namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The compiled non-executable-path rule set every project starts with (task: a delivered diff
/// that touches no buildable or testable source skips the build and test gates — origin:
/// ef2fefe5, a two-file skill markdown fix paying roughly twelve minutes of build-and-test
/// ceremony on every pipeline entry while the actual work took four). A project may only add to
/// this list (<c>h9k project set --non-executable-path</c>), never remove or narrow one of these
/// five entries — there is no command that touches this list itself, so the constraint holds by
/// construction rather than by a runtime check. See <see cref="NonExecutablePathClassifier"/> for
/// how a rule here matches a changed path, including the leading-<c>!</c> exclusion syntax the
/// fifth entry uses.
/// </summary>
public static class NonExecutablePathDefaults
{
    /// <summary>
    /// The bare <c>*.md</c> rule matches a markdown file anywhere in the tree, but this
    /// repository's own <c>.claude/templates/</c> is prompt source the product loads at runtime
    /// (<c>PromptTemplates.ResolvePath</c>) and tests check byte-for-byte
    /// (<c>AgentPromptBuilderGoldenTests</c>, <c>PromptTemplateContractTests</c>) — content that
    /// looks like docs but is not (independent pre-PR review, cycle 1, adversarial lens, high). The
    /// leading <c>!</c> carves that directory back out so an edit there still runs the gates that
    /// would catch it, regardless of which other rule would otherwise have matched it.
    /// </summary>
    public static readonly IReadOnlyList<string> Rules =
        ["*.md", "docs/", ".claude/skills/", ".claude/commands/", "!.claude/templates/"];
}
