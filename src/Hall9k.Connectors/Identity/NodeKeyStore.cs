using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
public sealed partial class NodeKeyStore(ProcessRunner? runner = null)
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>Where every node's keys live: ~/.hall9k/keys, one subdirectory per node id.</summary>
    public static string Root => Path.Combine(PlatformPaths.Home, "keys");

    public static string DirectoryFor(Guid nodeId) => Path.Combine(Root, nodeId.ToString());

    /// <summary>The private key file itself, inside <see cref="DirectoryFor"/>.</summary>
    public static string PrivateKeyPathFor(Guid nodeId) => Path.Combine(DirectoryFor(nodeId), "id_ed25519");

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
        string privateKeyPath = PrivateKeyPathFor(nodeId);
        string publicKeyPath = $"{privateKeyPath}.pub";

        // Two h9k processes on the same node (h9k project add's own join, run alongside a second,
        // manually invoked h9k project join) can both reach this method for the same node id at
        // once. Held for the whole check-and-generate/derive sequence below — the same
        // FileShare.None advisory-lock idiom SingleInstanceGuard and GitWorktreeManager's own
        // cross-process lock already use — this serializes them, so the second process never runs
        // ssh-keygen against a private key file the first is still creating, which can prompt to
        // overwrite and hang (this runner never redirects stdin).
        await using FileStream keyLock = await AcquireLockAsync(directory, cancellationToken);

        if (!File.Exists(privateKeyPath))
        {
            ProcessResult result = await RunSshKeygenAsync(
                ["-t", "ed25519", "-f", privateKeyPath, "-N", string.Empty, "-C", $"hall9k-node-{nodeId}"],
                directory, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new DomainValidationException(
                    "Could not generate this node's signing key with ssh-keygen "
                    + $"({result.StandardError.Trim()}). Signing is mandatory — every ledger commit is "
                    + "signed with this node's own key — so a node without one is refused: install "
                    + "ssh-keygen (on Windows, Git for Windows puts it on PATH) and re-run h9k project join.");
            }
        }
        else
        {
            // The private key already exists, from an earlier join. Its public half is always
            // derived fresh from the private key here rather than trusted from whatever .pub
            // already sits on disk — a stale or tampered .pub file would otherwise get registered
            // and written to root.yaml/node.yaml while git actually signs commits with the
            // different key underneath it, leaving the ledger unverifiable under its advertised
            // identity. This also sidesteps ssh-keygen's own overwrite prompt a fresh -f
            // generation would trigger against an existing private key — a prompt this runner's
            // caller never sees (stdin is not redirected), so the command would hang until it
            // times out, or silently regenerate the key if something did answer y (independent
            // pre-PR review, cycle 1, conformance and adversarial lenses, medium).
            ProcessResult result = await RunSshKeygenAsync(["-y", "-f", privateKeyPath], directory, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new DomainValidationException(
                    "This node's private key exists, but its public half could not be derived from "
                    + $"it with ssh-keygen ({result.StandardError.Trim()}). Restore the original "
                    + $"{publicKeyPath} or remove {privateKeyPath} entirely and re-run h9k project "
                    + "join to generate a fresh keypair.");
            }

            // ssh-keygen -y already carries the comment recorded in the private key's own metadata
            // at generation time (-C hall9k-node-<id>), so nothing further is appended here.
            string derivedPublicKeyLine = $"{result.StandardOutput.Trim()}\n";
            string? onDisk = File.Exists(publicKeyPath) ? await File.ReadAllTextAsync(publicKeyPath, cancellationToken) : null;
            if (onDisk != derivedPublicKeyLine)
            {
                await File.WriteAllTextAsync(publicKeyPath, derivedPublicKeyLine, cancellationToken);
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
    /// Runs ssh-keygen through <see cref="runner"/>, translating the tool being missing from
    /// <c>PATH</c> entirely into the same kind of refusal a non-zero exit already gets, rather
    /// than letting it escape as a raw, unhandled exception. The real <see cref="ProcessRunner"/>
    /// (<c>ExternalProcess.RunAsync</c>) starts the child process outside any try block, so a
    /// missing executable throws <see cref="Win32Exception"/> before ever producing a
    /// <see cref="ProcessResult"/> to check an exit code against — a node without ssh-keygen on
    /// PATH, or a minimal container image without openssh-client, would otherwise crash with a
    /// stack trace instead of the "install ssh-keygen and re-run" guidance every other refusal
    /// here gives (adversarial review, cycle 1, medium).
    /// </summary>
    private async Task<ProcessResult> RunSshKeygenAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await runner("ssh-keygen", arguments, workingDirectory, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw new DomainValidationException(
                "Could not run ssh-keygen — it is not on PATH "
                + $"({exception.Message}). Signing is mandatory — every ledger commit is signed with "
                + "this node's own key — so a node without one is refused: install ssh-keygen (on "
                + "Windows, Git for Windows puts it on PATH) and re-run h9k project join.");
        }
    }

    /// <summary>
    /// FileShare.None maps to an exclusive advisory lock on Unix and a real exclusive lock on
    /// Windows: whichever process's FileStream opens it first holds it until disposed, so a second
    /// process racing the same node id's key waits here instead of losing ssh-keygen to a file the
    /// first process is still writing. Unbounded on purpose, same as GitWorktreeManager's own
    /// cross-process lock: a holder finishes key generation in well under a second, and there is
    /// no safe value to time this out to.
    /// </summary>
    private static async Task<FileStream> AcquireLockAsync(string directory, CancellationToken cancellationToken)
    {
        string lockFilePath = Path.Combine(directory, ".h9k-node-key.lock");
        while (true)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Hall9k's own fingerprint for a public key line (<c>"ssh-ed25519 &lt;base64&gt; comment"</c>):
    /// the lowercase hex SHA-256 of the decoded key blob. See <see cref="NodeSigningKey"/> for why
    /// this is not OpenSSH's own <c>SHA256:</c> fingerprint format. Every way a caller-supplied
    /// line can fail to parse — too few fields, or a field that is not valid base64 — surfaces as
    /// the identical <see cref="DomainValidationException"/> rather than letting a raw
    /// <see cref="FormatException"/> escape: every caller of this method already catches the
    /// former as "this key is malformed, refuse it", and a second, undocumented exception type
    /// would keep slipping past that catch for a ledger-supplied key nobody signing this line ever
    /// validated on the way in.
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

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(fields[1]);
        }
        catch (FormatException exception)
        {
            throw new DomainValidationException(
                $"'{publicKeyLine}' is not a public key line Hall9k can fingerprint (its key data "
                + $"is not valid base64: {exception.Message}).");
        }

        byte[] hash = SHA256.HashData(blob);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="value"/> has the shape <see cref="Fingerprint"/> produces: exactly
    /// 64 lowercase hex characters. The one place this repo needs to tell a fingerprint apart from
    /// a name, an email fragment, or an owner's internal Guid — <c>h9k owner show</c>,
    /// <c>h9k task assign</c>, and every other place an owner is named by a human now that the
    /// fingerprint is the owner id everywhere a human or another node reads one (Decisions Log #190).
    /// </summary>
    public static bool IsFingerprint(string value) => FingerprintPattern().IsMatch(value);

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex FingerprintPattern();
}
