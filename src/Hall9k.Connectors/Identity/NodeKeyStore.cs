using System.Security.Cryptography;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Identity;

/// <summary>
/// This node's own ed25519 signing key, generated once under the install's own
/// <c>~/.hall9k/keys/&lt;node-id&gt;</c> and reused for every ledger commit after that
/// (idea 202383dc, A2a). <see cref="Fingerprint"/> is Hall9k's own identifier for the key — the
/// lowercase hex SHA-256 of the key's decoded wire-format blob, chosen instead of OpenSSH's own
/// base64 <c>SHA256:</c> fingerprint precisely because that format's <c>/</c> and <c>+</c>
/// characters (and the colon before them) are not legal in a git ref name, and this fingerprint
/// is used as one (<c>refs/hall9k/ledger/owners/&lt;fingerprint&gt;</c>).
/// </summary>
public sealed record NodeSigningKey(string PrivateKeyPath, string PublicKeyLine, string Fingerprint);

/// <summary>
/// Generates and reads back this node's own ed25519 keypair by shelling out to
/// <c>ssh-keygen</c> — the same tool <c>GitLedger</c> already depends on to sign and verify
/// commits, so a node able to sign a ledger commit at all is already able to generate this key.
/// On Windows that has to be the <c>ssh-keygen.exe</c> Git for Windows ships with its own
/// installation, since there is no guaranteed system OpenSSH otherwise.
/// </summary>
public sealed class NodeKeyStore(ProcessRunner? runner = null)
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>Where every node's keys live: ~/.hall9k/keys, one subdirectory per node id.</summary>
    public static string Root => Path.Combine(PlatformPaths.Home, "keys");

    public static string DirectoryFor(Guid nodeId) => Path.Combine(Root, nodeId.ToString());

    /// <summary>
    /// The private key file mode 0600 requires: readable and writable by this node's own account
    /// alone, never the ledger, a project home, or an event (the acceptance criterion this whole
    /// type exists to satisfy). Generates the keypair once — a private key already on disk is
    /// never regenerated or overwritten, so a node's key is stable for its whole lifetime.
    /// </summary>
    public async Task<NodeSigningKey> EnsureAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        string directory = DirectoryFor(nodeId);
        Directory.CreateDirectory(directory);
        string privateKeyPath = Path.Combine(directory, "id_ed25519");
        string publicKeyPath = $"{privateKeyPath}.pub";

        if (!File.Exists(privateKeyPath) || !File.Exists(publicKeyPath))
        {
            ProcessResult result = await runner(
                "ssh-keygen",
                ["-t", "ed25519", "-f", privateKeyPath, "-N", string.Empty, "-C", $"hall9k-node-{nodeId}"],
                directory,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new DomainValidationException(
                    "Could not generate this node's signing key with ssh-keygen "
                    + $"({result.StandardError.Trim()}). Signing is mandatory — every ledger commit is "
                    + "signed with this node's own key — so a node without one is refused: install "
                    + "ssh-keygen (on Windows, Git for Windows puts it on PATH) and re-run h9k project "
                    + "join named.");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(privateKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        string publicKeyLine = (await File.ReadAllTextAsync(publicKeyPath, cancellationToken)).Trim();
        return new NodeSigningKey(privateKeyPath, publicKeyLine, Fingerprint(publicKeyLine));
    }

    /// <summary>
    /// Hall9k's own fingerprint for a public key line (<c>"ssh-ed25519 &lt;base64&gt; comment"</c>):
    /// the lowercase hex SHA-256 of the decoded key blob. See <see cref="NodeSigningKey"/> for why
    /// this is not OpenSSH's own <c>SHA256:</c> fingerprint format.
    /// </summary>
    public static string Fingerprint(string publicKeyLine)
    {
        string[] fields = publicKeyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            throw new DomainValidationException(
                $"'{publicKeyLine}' is not a public key line Hall9k can fingerprint (expected "
                + "'<type> <base64> [comment]').");
        }

        byte[] blob = Convert.FromBase64String(fields[1]);
        byte[] hash = SHA256.HashData(blob);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
