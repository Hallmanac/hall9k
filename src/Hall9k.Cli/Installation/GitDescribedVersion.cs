using Hall9k.Cli.DaemonControl;

namespace Hall9k.Cli.Installation;

/// <summary>
/// The version <c>h9k install --repo</c> stamps a locally built binary with: <c>git describe</c>
/// against the repository being published, so a checkout with tag history reports something like
/// <c>0.2.0-12-gabc1234</c> (12 commits past the <c>v0.2.0</c> tag, at commit <c>abc1234</c>) or
/// <c>0.2.0-12-gabc1234-dirty</c> with uncommitted changes — instead of the csproj's checked-in
/// placeholder (Origin: Brian noticed <c>h9k --version</c> printing 0.1.0 on a Sep 2 local
/// install, 2026-09-04). This never runs for <c>--from-release</c>: a release payload's version
/// comes from the tag that triggered <c>release.yml</c>, stamped there with <c>-p:Version</c>,
/// and is read back off the payload's own <c>VERSION</c> file instead.
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

        // release.yml derives its own VERSION the same way (${GITHUB_REF_NAME#v}): the tag
        // convention here is v-prefixed (v0.2.0), but the prefix is not part of the version.
        if (version.StartsWith('v'))
        {
            version = version[1..];
        }

        return new Result(version, null);
    }
}
