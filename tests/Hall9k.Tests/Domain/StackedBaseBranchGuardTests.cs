using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A source-level guard for the one defect class this feature keeps re-opening (task: a stacked
/// pull-request edge exists as an explicit opt-in dependency). A stacked child's branch is a delta
/// against its PARENT's branch, so every site that asks git "what does this branch hold beyond its
/// base" or "what should this branch be cut from" has to name the run's own recorded base, never
/// the project's — and the two are the same value on every unstacked run, which is exactly why
/// getting it wrong is invisible until a stack exists.
/// <para>
/// Origin: this branch's own blast-radius sweep. The daemon's dispatch arm was fixed first and five
/// sibling sites were still reading <c>project.BaseBranch</c> — the CLI's two interactive
/// branch-creating claims (<c>h9k task work</c>, <c>h9k task start</c>, the two-arm shape AGENTS.md
/// warns about by name) plus three commit-counting guards (<c>VerificationRunner</c>'s no-commit
/// check, <c>h9k task deliver</c>, <c>h9k task release</c>, <c>h9k task delegate</c>). Each would
/// have counted the parent's commits as the child's own, reading a genuinely empty branch as
/// productive and waving through the very thing it exists to catch.
/// </para>
/// <para>
/// Deliberately a source scan rather than a behavioural test: the behaviour needs a real
/// repository, a live claim and a Delivered parent per site, while what actually regresses is a
/// sixth site being written the obvious way. A scan is what catches that on the commit that adds
/// it. It is narrow on purpose — only the two git verbs below, in the same statement as a project
/// base-branch read — so a file that legitimately renders or configures the project's base branch
/// (<c>h9k project show</c>, the project home's own documents, the <c>dev/</c> reading checkout)
/// never trips it.
/// </para>
/// </summary>
public sealed class StackedBaseBranchGuardTests
{
    /// <summary>
    /// The git-facing calls whose base argument must be the run's. Both take a base branch and
    /// answer a question about one branch relative to it, which is the shape that goes wrong.
    /// </summary>
    private static readonly string[] BaseRelativeCalls =
    [
        "CountBranchCommitsAsync(",
        "new WorktreeRequest(",
    ];

    /// <summary>
    /// The reads that are honestly the project's own base and must not be flagged: the long-lived
    /// <c>dev/</c> reading checkout really is on the project's base branch, and the resolver plus
    /// the two dispatch sites deliberately compare a resolved base against the project's to decide
    /// whether to record one at all.
    /// </summary>
    private static readonly string[] ExemptFiles =
    [
        "StackedBaseResolver.cs",
        "RunLauncher.cs",
        "TaskWorkCommand.cs",
        "TaskStartCommand.cs",
    ];

    [Fact]
    public void No_base_relative_git_call_reads_the_projects_base_branch_directly()
    {
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(
            TestSourceTree.SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceTree.IsBuildOutput(TestSourceTree.SourceDirectory(), file)
                || ExemptFiles.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || !BaseRelativeCalls.Any(call => line.Contains(call, StringComparison.Ordinal)))
                {
                    continue;
                }

                // The call's own argument list can wrap, so the base argument is looked for on the
                // call line and the two after it — far enough for this repo's wrapping habits,
                // short enough that an unrelated project.BaseBranch further down is never caught.
                // BaseBranchOr's own fallback argument IS project.BaseBranch and is the correct
                // reading — the whole point of the seam — so it is removed before the check,
                // leaving only a BARE project read to flag.
                string window = string.Join(' ', lines.Skip(i).Take(3))
                    .Replace("BaseBranchOr(project.BaseBranch)", string.Empty, StringComparison.Ordinal)
                    .Replace("BaseBranchOr(Project.BaseBranch)", string.Empty, StringComparison.Ordinal);
                if (window.Contains("project.BaseBranch", StringComparison.Ordinal)
                    || window.Contains("Project.BaseBranch", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a base-relative git call must take the RUN's own base branch (run.BaseBranchOr(project.BaseBranch), "
            + "or a base resolved through StackedBaseResolver for a fresh cut), never the project's directly — "
            + "on a stacked child the two differ, and the project's base counts the parent's commits as this "
            + "branch's own. If a new site genuinely wants the project's base (a long-lived reading checkout, "
            + "say), add its file to ExemptFiles here with a recorded why.");
    }
}
