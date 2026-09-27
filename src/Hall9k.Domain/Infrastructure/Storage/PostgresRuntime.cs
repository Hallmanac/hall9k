using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Hall9k's own Postgres definition for installed mode (Decisions Log #73), shipped into
/// <c>~/.hall9k</c> by <c>h9k install</c> so the doctor check and its start-offer never
/// depend on a repo checkout — an installed user has no dev worktree to run compose from.
/// The dev loop's Aspire AppHost manages its own Postgres container independently
/// (§15 row 28: the two provisioning paths stay deliberately separate) and never reads this.
/// </summary>
public static class PostgresRuntime
{
    /// <summary>Matches the host every connection string this class stands up names via
    /// <see cref="Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.ConnectionStringWithPassword"/>.</summary>
    public const string ContainerName = "hall9k-postgres";

    /// <summary>
    /// The volume the compose definition below gives the container's data directory — and,
    /// because the compose file pins it with an explicit <c>name:</c> below, the literal name
    /// Docker gives the volume it creates. Without that pin, Compose prefixes an unnamed volume
    /// with its own notion of the project name (the invoking working directory's basename by
    /// default), so the same logical volume comes out as <c>postgres_hall9k-pgdata</c> from one
    /// invocation and something else from another — and <c>h9k uninstall --purge-data</c>, which
    /// has to name the volume in a plain <c>docker volume rm</c> without Compose's help, would be
    /// guessing at a name nothing on disk actually carries (origin incident, this uninstall
    /// feature's own pre-PR review: purge silently failed to remove the real volume, but *did*
    /// remove a same-named volume the Aspire dev-loop had created independently under this exact
    /// literal string, since the dev loop's own naming carries no project prefix either). Pinning
    /// the name here means every consumer — compose, this constant, and the dev loop's own
    /// volume, kept deliberately distinct in <c>Hall9k.AppHost/AppHost.cs</c> — agrees on the
    /// same literal string, or a deliberately different one, never an accidental collision.
    /// Named separately from <see cref="ComposeFileContentsFor"/> so <c>h9k uninstall --purge-data</c>
    /// can name it in a <c>docker volume rm</c> without depending on the compose file still being
    /// on disk — uninstall's own removal of the home directory would otherwise race whichever of
    /// the two ran first.
    /// </summary>
    public const string VolumeName = "hall9k-pgdata";

    /// <summary>
    /// The literal volume name a pre-pin installed-mode compose file leaves behind: Compose's
    /// own project-name derivation for <see cref="ComposeDirectory"/>'s "postgres" leaf, before
    /// this branch's <c>name:</c> pin above existed to override it (docs/operations.md's
    /// Provisioning section has the full migration story). No code path compares against this
    /// literal directly: <c>ContainerRuntimeProbe.ComposeUpAsync</c>'s bring-up-fresh offer (and
    /// <c>UninstallCommand.HandleDataTierAsync</c>'s own absent-container purge path) instead
    /// search by the unanchored <c>hall9k-pgdata</c> substring, which this literal contains, so
    /// they also catch a checkout-dirname-prefixed volume that neither this literal nor
    /// <see cref="VolumeName"/> names. Kept as a named constant purely for the migration story
    /// above to point at, and for anything reading this file to have the literal in one place.
    /// </summary>
    public const string LegacyVolumeName = "postgres_hall9k-pgdata";

    public static string ComposeDirectory => Path.Combine(PlatformPaths.Home, "postgres");

    public static string ComposeFile => Path.Combine(ComposeDirectory, "docker-compose.yml");

    /// <summary>
    /// Mirrors the repository's own <c>docker-compose.yml</c> exactly, one <paramref name="password"/>
    /// aside — that file keeps the documented dev password <c>hall9k</c> for the contributor's own
    /// manual path (loopback-only after task 359e0d7a, so that constant never reaches the network),
    /// while every installed machine gets its own generated one instead (security review idea
    /// 6be68ee2, secrets-files-network findings 1 and 9: a shared, public constant password guarding
    /// a superuser role is a defect for anything more exposed than a contributor's own loopback dev
    /// container). Double-quoted so the value's own shape — 64 lowercase hex characters, never
    /// anything YAML, the connection string, or <c>psql</c> could misparse — is asserted rather than
    /// merely hoped for; the same rendering never has to special-case an unquoted literal like the
    /// dev file's <c>hall9k</c> still does. Keep the two files' structure in sync by hand: both are
    /// small and change rarely.
    /// </summary>
    public static string ComposeFileContentsFor(string password) => $"""
        # Hall9k-owned Postgres for installed mode (h9kd under launchd or started by
        # h9k daemon start). Written here by h9k install; never edited in place — a local
        # change is lost the next time install republishes it.
        services:
          postgres:
            image: postgres:18
            container_name: {ContainerName}
            restart: unless-stopped
            environment:
              POSTGRES_DB: hall9k
              POSTGRES_USER: postgres
              POSTGRES_PASSWORD: "{password}"
            ports:
              - "127.0.0.1:5432:5432"
            volumes:
              - {VolumeName}:/var/lib/postgresql

        volumes:
          {VolumeName}:
            name: {VolumeName}

        """;

    /// <summary>Matches the <c>POSTGRES_PASSWORD</c> line either quoted (this branch's own shape)
    /// or bare (the pre-migration shipped constant, <c>hall9k</c>, on a compose file no run has
    /// rewritten yet) — <see cref="ReadPasswordFromComposeFile"/> has to recognise both so a
    /// pre-existing install's real, already-in-effect password is read back rather than silently
    /// replaced by a freshly generated one that the running container never actually adopted
    /// (<c>POSTGRES_PASSWORD</c> only applies at <c>initdb</c>).</summary>
    private static readonly Regex PasswordLine = new(
        """^\s*POSTGRES_PASSWORD:\s*"?(?<password>[^"\r\n]*?)"?\s*$""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// 32 CSPRNG bytes rendered as 64 lowercase hex characters — a shape that can never need
    /// quoting or escaping anywhere it lands (this compose file's YAML, the Postgres connection
    /// string, a <c>psql</c> literal, a shell argument), which is the whole reason the shape was
    /// chosen over a mixed-character generator (security review idea 6be68ee2, secrets-files-network
    /// finding 1).
    /// </summary>
    public static string GeneratePassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The password currently recorded in <see cref="ComposeFile"/>, or <see langword="null"/> when
    /// the file does not exist yet or its <c>POSTGRES_PASSWORD</c> line could not be found —
    /// read-only, so a caller that just wants to know what is already in effect (to build a
    /// connection string, say) never risks generating or writing anything as a side effect of
    /// asking.
    /// </summary>
    public static string? ReadPasswordFromComposeFile()
    {
        if (!File.Exists(ComposeFile))
        {
            return null;
        }

        Match match = PasswordLine.Match(File.ReadAllText(ComposeFile));
        return match.Success && match.Groups["password"].Value.Length > 0 ? match.Groups["password"].Value : null;
    }

    /// <summary>
    /// Writes (and, on a re-run, refreshes) the compose file, keeping whatever password is already
    /// in effect rather than resetting it: the file is this password's own durable record, and a
    /// generated one is produced only when <see cref="ReadPasswordFromComposeFile"/> finds nothing
    /// to keep (a genuinely fresh machine). <c>h9k install</c> calls this on every publish-and-refresh
    /// and the doctor's start-offer calls it too — both would otherwise regenerate a fresh password on
    /// every single rewrite, silently orphaning whatever the running container's <c>initdb</c> already
    /// baked in (security review idea 6be68ee2, secrets-files-network finding 1). Written through
    /// <see cref="AtomicFileWrite"/> so the file lands at a private, owner-only mode on Unix rather
    /// than whatever the process umask would otherwise leave a password-bearing file at. Returns the
    /// password now in effect, for a caller that needs to build a connection string from it.
    /// </summary>
    public static Task<string> WriteComposeFileAsync(CancellationToken cancellationToken) =>
        WriteComposeFileAsync(passwordOverride: null, cancellationToken);

    /// <summary>
    /// Same as <see cref="WriteComposeFileAsync(CancellationToken)"/>, but forces the password to
    /// <paramref name="passwordOverride"/> rather than reading (or generating) one — the migration's
    /// own final step (<see cref="Hall9k.Cli.Diagnostics.DatabaseDoctor"/>), run only once the new
    /// password is already committed at the database via <c>ALTER ROLE</c> and recorded in
    /// <c>config.json</c>, so this file becomes the new password's durable record last, never before
    /// the two writes it has to stay consistent with.
    /// </summary>
    public static async Task<string> WriteComposeFileAsync(string? passwordOverride, CancellationToken cancellationToken)
    {
        string password = passwordOverride ?? ReadPasswordFromComposeFile() ?? GeneratePassword();
        Directory.CreateDirectory(ComposeDirectory);
        await AtomicFileWrite.WriteAllTextAsync(ComposeFile, ComposeFileContentsFor(password), cancellationToken);
        return password;
    }
}
