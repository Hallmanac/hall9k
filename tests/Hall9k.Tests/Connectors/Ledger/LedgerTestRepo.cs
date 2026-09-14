using System.Diagnostics;

namespace Hall9k.Tests.Connectors.Ledger;

/// <summary>
/// Throwaway bare repositories in a temp directory for <see cref="GitLedgerTests"/> — the one
/// place git is allowed in tests outside the message-transport chain reader (Brian, 2026-09-13).
/// A "hub" bare repo stands in for the project's real GitHub remote; a "node" is a bare clone of
/// it with the fetch refspec narrowed to heads only, exactly as <c>RepoMaterialiser</c> configures
/// a real project's own <c>repo/&lt;name&gt;.git</c> — so a test proving an ordinary fetch does
/// not bring a ledger ref down is proving it against the same refspec a real install runs.
/// </summary>
internal sealed class LedgerTestRepo : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-ledger-test-{Guid.NewGuid():N}");

    public LedgerTestRepo()
    {
        Directory.CreateDirectory(_root);
    }

    /// <summary>A bare repository standing in for the project's real GitHub remote.</summary>
    public string CreateHub()
    {
        string hub = Path.Combine(_root, $"hub-{Guid.NewGuid():N}.git");
        Git(_root, "init", "-q", "--bare", "-b", "main", hub);
        return hub;
    }

    /// <summary>
    /// A bare clone of <paramref name="hub"/> with the fetch refspec narrowed to heads only, the
    /// same correction <c>RepoMaterialiser</c> applies to every real project clone — so a plain
    /// <c>git fetch origin</c> here behaves exactly as it does against a real project's own
    /// <c>repo/&lt;name&gt;.git</c>.
    /// </summary>
    public string CloneNode(string hub)
    {
        string node = Path.Combine(_root, $"node-{Guid.NewGuid():N}.git");
        Git(_root, "clone", "-q", "--bare", hub, node);
        Git(node, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        return node;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            // git leaves loose object files read-only, which Directory.Delete refuses on Windows.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    /// <summary>Runs a plain, ordinary `git fetch origin` — no explicit refspec — the operation
    /// GitLedger itself never performs, so a test can prove it does not bring a ledger ref down.</summary>
    public static (int ExitCode, string StandardOutput, string StandardError) OrdinaryFetch(string repositoryPath) =>
        Git(repositoryPath, "fetch", "origin");

    public static (int ExitCode, string StandardOutput, string StandardError) RevParseQuiet(
        string repositoryPath, string reference) =>
        Git(repositoryPath, "rev-parse", "--verify", "--quiet", reference);

    public static (int ExitCode, string StandardOutput, string StandardError) VerifyCommit(
        string repositoryPath, string commitId, string allowedSignersFile) =>
        Git(
            repositoryPath,
            "-c", "gpg.format=ssh",
            "-c", $"gpg.ssh.allowedSignersFile={allowedSignersFile}",
            "verify-commit", commitId);

    private static (int ExitCode, string StandardOutput, string StandardError) Git(
        string workingDirectory, params string[] arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(workingDirectory);
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        // Both pipes drained concurrently: reading stdout to exhaustion first would block this
        // thread until exit, and a git invocation that fills the stderr pipe buffer in the
        // meantime would block on a write nobody is reading.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        string output = standardOutput.GetAwaiter().GetResult();
        string error = standardError.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0 && arguments is ["init", ..] or ["clone", ..] or ["config", ..])
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}{error}");
        }

        return (process.ExitCode, output, error);
    }
}
