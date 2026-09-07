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
/// <para>
/// A fourth scan was added in cycle 4, for the same class in the review prompts: the branch's own
/// build, recompose, fold and replay mechanics were all keyed to the recorded fork point, and the
/// review lens, verify and fix prompts were still naming <c>origin/&lt;parent&gt;</c> — which is
/// what decides a reviewer's read range AND its in-scope/out-of-scope grading.
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
    /// No file is exempt, and the review round on this pull request is why. This scan once carried
    /// a whole-file exemption list — the resolver plus the three dispatch sites, on the grounds
    /// that they deliberately compare a resolved base against the project's to decide whether to
    /// record one at all. Those comparisons are real, but not one of them sits inside a
    /// base-relative call's window, so the exemptions caught nothing and cost the scan its cover
    /// on the three files where writing this wrong is most destructive (the daemon's dispatch arm
    /// and the CLI's two interactive claims, which is precisely the list this class's own origin
    /// paragraph above names). The per-line seam below — stripping the
    /// <c>BaseBranchOr</c>/<c>StackedForkPoint</c> fallback argument before looking for a bare
    /// project read — is the narrower answer to the same need, and it arrived after the exemptions
    /// did.
    /// </summary>
    [Fact]
    public void No_base_relative_git_call_reads_the_projects_base_branch_directly()
    {
        List<string> offenders = [];

        foreach ((string file, string[] lines, int i) in ProductionLines())
        {
            string line = lines[i];
            if (!BaseRelativeCalls.Any(call => line.Contains(call, StringComparison.Ordinal)))
            {
                continue;
            }

            // The call's own argument list can wrap, so the base argument is looked for on the
            // call line and the two after it — far enough for this repo's wrapping habits,
            // short enough that an unrelated project.BaseBranch further down is never caught.
            // BaseBranchOr's own fallback argument IS project.BaseBranch and is the correct
            // reading — the whole point of the seam — so it is removed before the check,
            // leaving only a BARE project read to flag. StackedForkPoint is the same seam and
            // reads its fallback the same way (conformance review, cycle 4: these counts now
            // pass the run's recorded fork point alongside its base, because the parent's own
            // ref stops naming the cut point once the parent is force-pushed).
            string window = string.Join(' ', lines.Skip(i).Take(3))
                .Replace("BaseBranchOr(project.BaseBranch)", string.Empty, StringComparison.Ordinal)
                .Replace("BaseBranchOr(Project.BaseBranch)", string.Empty, StringComparison.Ordinal)
                .Replace("StackedForkPoint(project.BaseBranch)", string.Empty, StringComparison.Ordinal)
                .Replace("StackedForkPoint(Project.BaseBranch)", string.Empty, StringComparison.Ordinal);
            if (window.Contains("project.BaseBranch", StringComparison.Ordinal)
                || window.Contains("Project.BaseBranch", StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "a base-relative git call must take the RUN's own base branch (run.BaseBranchOr(project.BaseBranch), "
            + "or a base resolved through StackedBaseResolver for a fresh cut), never the project's directly — "
            + "on a stacked child the two differ, and the project's base counts the parent's commits as this "
            + "branch's own. No file is exempt: the two seams above (BaseBranchOr's and StackedForkPoint's own "
            + "fallback arguments) are the only honest way one of these calls names the project's base, and a "
            + "site that wants it for some other reason wants a different git verb.");
    }

    /// <summary>
    /// The same class one layer up, and the one the scan above missed (independent pre-PR review,
    /// cycle 1, conformance lens): a build prompt that never receives a base at all falls back to
    /// the project's inside the builder, which is invisible at the call site — no
    /// <c>project.BaseBranch</c> to spot. The prompt is where the recompose's own mixed reset is
    /// authored, so an omission here is the most destructive shape this defect class has: it
    /// recomposes the parent's commits as the child's authored history, and the recompose's
    /// tree-identity check passes by construction because a mixed reset never moves the tree.
    /// <para>
    /// An interactive claim's prompt is exempt, and only because it names no base branch anywhere:
    /// the <c>isInteractive</c> arm of <c>WorkPromptBuilder.Build</c> appends neither the
    /// checkpoint/recompose protocol nor the self-review phase, which are the only rules that read
    /// one. If that ever changes, this exemption has to go with it.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_build_prompt_is_handed_the_runs_own_base_branch()
    {
        List<string> offenders = [];

        foreach ((string file, string[] lines, int i) in ProductionLines())
        {
            if (!lines[i].Contains("PromptBuilder.Build(", StringComparison.Ordinal))
            {
                continue;
            }

            // A reviewer's own review lap (ReviewLapPromptBuilder, Decisions Log #149) is exempt
            // for the identical reason the interactive arm below is, and one more besides: it
            // appends neither the checkpoint/recompose protocol nor the self-review phase — the
            // only two rules that read a base branch to reset or diff against — because a lap
            // has no diff of its own to recompose. The one range it does name is the REVIEWED
            // pull request's own base (`origin/<PullRequest.BaseRefName>`, read live from GitHub
            // and recorded as RunDispatched.PrReviewBaseRefName), which is that run's own base by
            // construction and can never be the project's base standing in for a parent's.
            if (lines[i].Contains("ReviewLapPromptBuilder.Build(", StringComparison.Ordinal))
            {
                continue;
            }

            // Long argument lists here, so a wider window than the git-call scan's three lines —
            // baseBranch sits last by convention (CancellationToken aside, prompts take none).
            string window = string.Join(' ', lines.Skip(i).Take(10));
            if (window.Contains("isInteractive: true", StringComparison.Ordinal)
                || window.Contains("baseBranch:", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        offenders.Should().BeEmpty(
            "a build prompt must be handed the base this run actually recorded (baseBranch:, and baseCommit: "
            + "with it wherever a fork point was observed), never left to default to the project's — the "
            + "prompt's self-review range and end-of-work recompose both read it, and on a stacked child the "
            + "recompose would reset to the PARENT's fork point and rewrite the parent's commits as this "
            + "branch's own history.");
    }

    /// <summary>
    /// The third face of the same class, and the other one the git-call scan above could not see
    /// (independent pre-PR review, cycle 1, conformance lens): a dispatch that records the base
    /// BRANCH but not the base COMMIT leaves the stacked replay permanently unobservable — the
    /// force-push arm has nothing but that record to read a boundary from, and it refuses to
    /// dispatch on a blank rather than guessing one, so the child silently never replays and never
    /// retargets. Every dispatch site records both or neither is any use.
    /// </summary>
    [Fact]
    public void Every_dispatch_records_both_the_base_branch_and_the_base_commit()
    {
        List<string> offenders = [];

        foreach ((string file, string[] lines, int i) in ProductionLines())
        {
            if (!lines[i].Contains("new RunDispatched(", StringComparison.Ordinal))
            {
                continue;
            }

            // RunDispatched's argument list is the longest in the codebase and its two base fields
            // sit at the end of it, so this window is generous on purpose.
            string window = string.Join(' ', lines.Skip(i).Take(30));
            if (window.Contains("BaseBranch:", StringComparison.Ordinal)
                && window.Contains("BaseCommit:", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        offenders.Should().BeEmpty(
            "every dispatch records BaseBranch AND BaseCommit — the branch this run's work sits on and that "
            + "branch resolved to a commit at the cut. A site that records only the branch leaves a stacked "
            + "child with no fork point, which StackedParentWatch reads as Unobservable forever: no replay on a "
            + "parent force-push, and no retarget when it later merges.");
    }

    /// <summary>
    /// The fourth face, and the one the three scans above could not see (conformance and
    /// adversarial review, cycle 4): a review prompt handed the parent's BRANCH but not the commit
    /// this branch was cut from. Its ranges are three-dot diffs, so naming the parent's ref is not
    /// merely stale — a parent force-pushed during the child's review window (an ordinary review
    /// lap folding its own fixes) collapses the merge base below this branch's real fork point, and
    /// the reviewer then reads AND SCOPES the parent's whole rewritten-away delta as the child's
    /// own work: parent-owned defects are graded in-scope, and the fix session edits the parent's
    /// code on the child's branch.
    /// <para>
    /// A foreign pull request is the one exemption, and an honest one: a pr-review checkout has no
    /// run of its own whose fork point could be recorded, so the pull request's own base ref is the
    /// only boundary there is.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_review_prompt_is_handed_the_runs_own_fork_point()
    {
        List<string> offenders = [];

        foreach ((string file, string[] lines, int i) in ProductionLines())
        {
            string line = lines[i];
            bool constructsOverride = line.Contains("new ", StringComparison.Ordinal)
                && line.Contains("ReviewMechanicsOverride(", StringComparison.Ordinal);
            bool buildsBaseAwarePrompt = line.Contains("BuildReviewVerify(", StringComparison.Ordinal)
                || line.Contains("BuildReviewFix(", StringComparison.Ordinal);
            if (!constructsOverride && !buildsBaseAwarePrompt)
            {
                continue;
            }

            // Same generous window as the build-prompt scan, and for the same reason: these
            // argument lists wrap, and the base pair sits at the end of them.
            string window = string.Join(' ', lines.Skip(i).Take(10));
            bool satisfied = constructsOverride
                ? window.Contains("ForkPointCommit:", StringComparison.Ordinal)
                  || window.Contains("DiffIsForeignPullRequest: true", StringComparison.Ordinal)
                // A caller that hands over no base at all is an unstacked-only surface and keeps
                // the project's own; it is a caller naming the base BRANCH without the commit that
                // is the defect.
                : !window.Contains("baseBranch:", StringComparison.Ordinal)
                  || window.Contains("baseCommit:", StringComparison.Ordinal);
            if (!satisfied)
            {
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "a review prompt told which branch this run is a delta against must be told the commit it was cut "
            + "from too (ForkPointCommit: / baseCommit:, from RunDetails.StackedForkPoint) — a three-dot range "
            + "against origin/<parent> collapses to the project's base the moment the parent is force-pushed, "
            + "and the reviewer then reads and scopes the parent's already-reviewed work as this branch's own.");
    }

    /// <summary>
    /// Every real source line under <c>src/</c>, with its file and index, skipping build output and
    /// whole-line comments — the shared walk all four scans above run over.
    /// </summary>
    private static IEnumerable<(string File, string[] Lines, int Index)> ProductionLines()
    {
        foreach (string file in Directory.EnumerateFiles(
            TestSourceTree.SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceTree.IsBuildOutput(TestSourceTree.SourceDirectory(), file))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    yield return (file, lines, i);
                }
            }
        }
    }
}
