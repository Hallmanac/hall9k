namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The compiled non-executable-path rule set every project starts with (task: a delivered diff
/// that touches no buildable or testable source skips the build and test gates — origin:
/// ef2fefe5, a two-file skill markdown fix paying roughly twelve minutes of build-and-test
/// ceremony on every pipeline entry while the actual work took four). A project may only add to
/// this list (<c>h9k project set --non-executable-path</c>), never remove one of these four —
/// there is no command that touches this list itself, so the constraint holds by construction
/// rather than by a runtime check. See <see cref="NonExecutablePathClassifier"/> for how a rule
/// here matches a changed path.
/// </summary>
public static class NonExecutablePathDefaults
{
    public static readonly IReadOnlyList<string> Rules = ["*.md", "docs/", ".claude/skills/", ".claude/commands/"];
}
