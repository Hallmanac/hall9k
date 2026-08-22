using System.ComponentModel;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.Credentials;

/// <summary>
/// Turns a <see cref="CredentialReference"/> back into the secret it points at, and — for the
/// one kind Hall9k stores itself — puts a secret where such a reference can find it.
/// <para>
/// The reference discipline (PLAN.md §10) is that a connection records <em>where</em> a
/// credential lives and never the credential. That only works if something can walk back the
/// other way, and this is it: the single place a secret is read, so there is exactly one file
/// to look at to answer "what does this platform do with tokens". It is deliberately not a
/// cache — every resolve reads the source again, because a rotated token should take effect the
/// next time it is used rather than the next time the process restarts.
/// </para>
/// <para>
/// Nothing here ever puts a secret in an exception, a log line, or a return value other than the
/// one the caller asked for. The refusals name the kind, the identifier, and the command that
/// fixes it, which is everything a human or an agent needs and none of what they must not see.
/// </para>
/// </summary>
public sealed class CredentialVault(ProcessRunner? runner = null)
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>The vault every command reaches for; a test constructs its own with a stub runner.</summary>
    public static CredentialVault Default { get; } = new();

    /// <summary>Where file-kind secrets live: ~/.hall9k/credentials, the owner's directory alone.</summary>
    public static string Directory => Path.Combine(PlatformPaths.Home, "credentials");

    /// <summary>
    /// The file a file-kind reference names, refused when the name is anything other than a
    /// single file sitting directly in <see cref="Directory"/>.
    /// <para>
    /// Every path in and out of the file store is built here, so this is the one place that has
    /// to hold the line. A name carrying a separator or a parent segment would compose into a
    /// path outside the owner's credentials directory, which on the write side means writing a
    /// token somewhere nobody agreed to put one and on the read side means resolving a reference
    /// to a file it does not name. The names this platform derives are already slugs and cannot
    /// do either; the guard is here because the identifier on the read side arrives off the event
    /// stream and a future call site would not know the rule it was keeping.
    /// </para>
    /// </summary>
    public static string FileFor(string name) =>
        IsStoredFileName(name)
            ? Path.Combine(Directory, name)
            : throw new DomainValidationException(
                $"'{RelayedText.OneLine(name)}' is not a credential name this vault can use. A stored "
                + "credential is one file in the owner's credentials directory, so the name is a file "
                + "name alone: no directory separators, and no parent segments.");

    /// <summary>Whether a name composes into a file inside <see cref="Directory"/> and nowhere else.</summary>
    private static bool IsStoredFileName(string name) =>
        name.IsNotBlank() && name is not ("." or "..") && name == Path.GetFileName(name);

    /// <summary>
    /// The secret <paramref name="reference"/> points at. <paramref name="purpose"/> is what the
    /// caller wanted it for ("read PROJ-123 at https://…"), quoted into the refusal so somebody
    /// reading a failure knows which connection stopped working rather than only that one did.
    /// </summary>
    public async ValueTask<string> ResolveAsync(
        CredentialReference reference, string purpose, CancellationToken cancellationToken)
    {
        string identifier = reference.Identifier ?? string.Empty;
        return reference.Kind switch
        {
            var kind when kind == CredentialKind.EnvironmentVariable => FromEnvironment(identifier, purpose),
            var kind when kind == CredentialKind.File => await FromFileAsync(identifier, purpose, cancellationToken),
            var kind when kind == CredentialKind.Keychain => await FromKeychainAsync(identifier, purpose, cancellationToken),
            var kind when kind == CredentialKind.GhCli => throw new DomainValidationException(
                $"The connection used to {purpose} is registered against the gh CLI's own login, which "
                + "holds no token Hall9k can read. That reference is for GitHub, where Hall9k shells out "
                + "to gh rather than authenticating itself (PLAN.md §10). Register the connection again "
                + "with a token: h9k connection add jira --help"),
            _ => throw new DomainValidationException(
                $"The connection used to {purpose} has no usable credential reference "
                + $"('{RelayedText.OneLine(reference.ToString())}'). Register it again: "
                + "h9k connection add jira --help"),
        };
    }

    /// <summary>
    /// Write a secret Hall9k was handed directly and return the reference that finds it again.
    /// <para>
    /// The file is created before the secret is written to it and its permissions are narrowed
    /// while it is still empty, so there is no moment where a readable file holds a token. On
    /// Windows there is no mode to set; the file inherits the user profile's ACL, which is the
    /// same protection ~/.hall9k already relies on, and the caller says so out loud rather than
    /// letting the difference pass silently.
    /// </para>
    /// </summary>
    public static async Task<CredentialReference> StoreAsync(
        string name, string secret, CancellationToken cancellationToken)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("A stored credential needs a name to be found by.");
        }

        if (secret.IsBlank())
        {
            throw new DomainValidationException("There is no secret here to store.");
        }

        // The path is composed first, because composing it is what vets the name and a refusal
        // should not have created a directory on its way out.
        string path = FileFor(name);

        System.IO.Directory.CreateDirectory(Directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await using (FileStream created = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(created.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await using StreamWriter writer = new(created);
            await writer.WriteAsync(secret.AsMemory(), cancellationToken);
        }

        return CredentialReference.File(name);
    }

    /// <summary>Whether Hall9k stores a secret under this name; asked before a re-registration discards one.</summary>
    public static bool Holds(CredentialReference reference) =>
        reference.Kind == CredentialKind.File
        && reference.Identifier is { } name
        && IsStoredFileName(name)
        && File.Exists(FileFor(name));

    /// <summary>
    /// Remove a secret Hall9k stored and nothing points at any more, returning the file it
    /// deleted or null when there was none to delete.
    /// <para>
    /// The counterpart to <see cref="StoreAsync"/>, and it exists because the overwrite that
    /// keeps re-registration clean only covers the case where the new credential lands on the
    /// same file name. Rotating a stored token into an environment variable, or re-registering
    /// the same site as a different account, both write a new reference and leave the old file
    /// holding a working token that nothing mentions again — a secret nobody meant to keep, which
    /// is exactly what the derived file name was chosen to prevent. Origin incident (2026-08-21):
    /// the pre-PR review of the Jira branch traced both paths.
    /// </para>
    /// <para>
    /// Only the file kind is touched, and only a file this vault would itself have written. The
    /// other kinds point at a secret somebody else put in a store of their own, and deleting one
    /// of those would be this platform reaching outside what it created.
    /// </para>
    /// </summary>
    public static string? Discard(CredentialReference reference)
    {
        if (!Holds(reference) || reference.Identifier is not { } name)
        {
            return null;
        }

        string path = FileFor(name);
        File.Delete(path);
        return path;
    }

    private static string FromEnvironment(string variable, string purpose) =>
        Environment.GetEnvironmentVariable(variable) is { } value && value.IsNotBlank()
            ? value.Trim()
            : throw new DomainValidationException(
                $"The environment variable {RelayedText.OneLine(variable)} is empty or unset, and it is "
                + $"where the credential to {purpose} was registered. Export it in the environment this "
                + "ran in and try again — note that h9kd inherits the environment it was started from, "
                + "so a variable exported after 'h9k daemon start' is not there until the daemon restarts.");

    private static async ValueTask<string> FromFileAsync(
        string name, string purpose, CancellationToken cancellationToken)
    {
        string path = FileFor(name);
        if (!File.Exists(path))
        {
            throw new DomainNotFoundException(
                $"The credential to {purpose} is stored at {path}, and that file is not there. "
                + "It is written when the connection is registered, so either it was removed or this is "
                + "a different machine: register the connection again with 'h9k connection add jira'.");
        }

        try
        {
            string secret = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            return secret.IsNotBlank()
                ? secret
                : throw new DomainValidationException(
                    $"The credential file at {path} is empty, so there is nothing to {purpose} with. "
                    + "Register the connection again with 'h9k connection add jira'.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DomainValidationException(
                $"Could not read the credential at {path}: {exception.Message}. It is written readable by "
                + "you alone, so this is usually the file having been moved or its ownership changed.");
        }
    }

    /// <summary>
    /// The macOS keychain, read through the 'security' tool rather than a native binding: it is
    /// what the platform already does for every other external credential (shell out to the tool
    /// that holds it), and it means the keychain prompt a locked item raises is the operating
    /// system's own, which is the only thing that can honestly ask for a password.
    /// <para>
    /// Refused elsewhere rather than approximated. A keychain reference on Linux or Windows names
    /// a store this code cannot open, and inventing a fallback — reading the file store, checking
    /// an environment variable — would resolve a reference to something other than what it says.
    /// </para>
    /// </summary>
    private async ValueTask<string> FromKeychainAsync(
        string service, string purpose, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new DomainValidationException(
                $"The credential to {purpose} is registered in the macOS keychain "
                + $"('{RelayedText.OneLine(service)}'), and this is not macOS. Register the connection on "
                + "this machine with --token-env <VARIABLE>, or with the token itself so Hall9k stores it "
                + "under ~/.hall9k/credentials.");
        }

        ProcessResult result;
        try
        {
            result = await runner(
                "security",
                ["find-generic-password", "-s", service, "-w"],
                Environment.CurrentDirectory,
                cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException)
        {
            throw new DomainValidationException(
                $"Could not read the keychain item '{RelayedText.OneLine(service)}' to {purpose}: "
                + $"{exception.Message}. Check it by hand with: security find-generic-password -s "
                + $"{RelayedText.OneLine(service)} -w");
        }

        if (result.ExitCode != 0 || result.StandardOutput.Trim().IsBlank())
        {
            throw new DomainNotFoundException(
                $"The keychain has no readable item named '{RelayedText.OneLine(service)}', which is where "
                + $"the credential to {purpose} was registered. security reported: "
                + $"{RelayedText.OneLine(result.StandardError).Trim()}. Add it with: security "
                + $"add-generic-password -s {RelayedText.OneLine(service)} -a <account> -w");
        }

        return result.StandardOutput.Trim();
    }
}
