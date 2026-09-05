using Hall9k.Cli.DaemonControl;

namespace Hall9k.Cli.Installation;

/// <summary>
/// The version <c>h9k install --repo</c> stamps a locally built binary with: <c>git describe</c>
/// against the repository being published, so a checkout with tag history reports something like
/// <c>0.2.0-12-gabc1234</c> (12 commits past the <c>v0.2.0</c> tag, at commit <c>abc1234</c>) or
/// <c>0.2.0-12-gabc1234-dirty</c> with uncommitted changes — an untracked file counts as dirty
/// here too, even though <c>git describe</c>'s own <c>--dirty</c> flag only looks at tracked
/// ones — instead of the csproj's checked-in placeholder (Origin: Brian noticed <c>h9k
/// --version</c> printing 0.1.0 on a Sep 2 local install, 2026-09-04). This never runs for
/// <c>--from-release</c>: a release payload's version comes from the tag that triggered
/// <c>release.yml</c>, stamped there with <c>-p:Version</c>, and is read back off the payload's
/// own <c>VERSION</c> file instead. A <c>repoRoot</c> that resolves into a git repository only
/// by walking up into an unrelated one above it (nowhere close to a top level of its own) is
/// treated the same as no repository at all, rather than trusting that unrelated repository's
/// tags.
/// <para>
/// Deliberately <c>--tags</c> without <c>--always</c>: a repository with no tags reachable from
/// HEAD must fall back to the checked-in csproj version (today's behavior), not to a bare commit
/// hash <c>--always</c> would supply instead. Git being missing or refusing to run the same
/// falls back the same way — <see cref="Exec.RunAsync"/> already degrades a failed process
/// launch into a nonzero <see cref="ExecResult"/> rather than throwing, so both cases are one
/// check here.
/// </para>
/// </summary>
internal static class GitDescribedVersion
{
    /// <summary>
    /// <paramref name="Version"/> is the version to stamp with, or null when none could be
    /// derived — in which case <paramref name="FallbackReason"/> names why, for the install
    /// output's own fallback note.
    /// </summary>
    internal readonly record struct Result(string? Version, string? FallbackReason);

    internal static async Task<Result> ResolveAsync(string repoRoot, CancellationToken cancellationToken)
    {
        ExecResult described = await Exec.RunAsync(
            "git", ["-C", repoRoot, "describe", "--tags", "--dirty"], cancellationToken);
        if (!described.Succeeded)
        {
            string reason = described.StandardError.Trim();
            return new Result(null, reason.Length == 0 ? $"git describe exited {described.ExitCode}" : reason);
        }

        string version = described.StandardOutput.Trim();
        if (version.Length == 0)
        {
            return new Result(null, "git describe produced no output");
        }

        // git discovers a repository by walking *up* from repoRoot, so a repoRoot that is not
        // itself a git working tree's own top level — nested under an unrelated repository
        // higher up, a dotfiles-managed home directory being the common shape — would
        // otherwise be silently stamped with that unrelated repository's own tags (cycle 1
        // adversarial review finding). --show-cdup is empty exactly when repoRoot is the
        // discovered repository's own top level; comparing repoRoot against --show-toplevel
        // directly would also catch this but has to fight git resolving symlinks in that
        // output that repoRoot (Path.GetFullPath) never does — a macOS temp directory being
        // exactly such a symlink.
        ExecResult cdup = await Exec.RunAsync("git", ["-C", repoRoot, "rev-parse", "--show-cdup"], cancellationToken);
        if (!cdup.Succeeded || cdup.StandardOutput.Trim().Length != 0)
        {
            return new Result(null, "repository root does not match the directory being published");
        }

        // release.yml derives its own VERSION the same way (${GITHUB_REF_NAME#v}): the tag
        // convention here is v-prefixed (v0.2.0), but the prefix is not part of the version.
        if (version.StartsWith('v'))
        {
            version = version[1..];
        }

        // --dirty only considers modifications to tracked files, so a source tree that
        // differs from HEAD purely by an untracked file would otherwise describe as clean
        // (cycle 1 conformance review finding).
        if (!version.EndsWith("-dirty", StringComparison.Ordinal))
        {
            ExecResult status = await Exec.RunAsync(
                "git", ["-C", repoRoot, "status", "--porcelain", "--untracked-files=normal"], cancellationToken);
            if (status.Succeeded
                && status.StandardOutput.Split('\n').Any(line => line.StartsWith("??", StringComparison.Ordinal)))
            {
                version += "-dirty";
            }
        }

        return new Result(version, null);
    }
}
