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
    /// <summary>Matches <see cref="Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.DefaultConnectionString"/>.</summary>
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
    /// Named separately from <see cref="ComposeFileContents"/> so <c>h9k uninstall --purge-data</c>
    /// can name it in a <c>docker volume rm</c> without depending on the compose file still being
    /// on disk — uninstall's own removal of the home directory would otherwise race whichever of
    /// the two ran first.
    /// </summary>
    public const string VolumeName = "hall9k-pgdata";

    public static string ComposeDirectory => Path.Combine(PlatformPaths.Home, "postgres");

    public static string ComposeFile => Path.Combine(ComposeDirectory, "docker-compose.yml");

    /// <summary>
    /// Mirrors the repository's own <c>docker-compose.yml</c> exactly — that file is what a
    /// contributor reads and runs by hand from a checkout; this constant is what ships inside
    /// the binary so an installed user needs neither. Keep the two in sync by hand: both are
    /// small and change rarely.
    /// </summary>
    public static string ComposeFileContents => $"""
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
              POSTGRES_PASSWORD: hall9k
            ports:
              - "5432:5432"
            volumes:
              - {VolumeName}:/var/lib/postgresql

        volumes:
          {VolumeName}:
            name: {VolumeName}

        """;

    /// <summary>
    /// Writes (and, on a re-run, refreshes) the compose file. <c>h9k install</c> calls this
    /// on every publish-and-refresh, which is what makes a local edit here transient rather
    /// than a supported customization. The doctor's start-offer calls the same method as a
    /// belt-and-suspenders fallback for a CLI used without ever installing (e.g. straight off
    /// a dev build), so the offer can always find something to run.
    /// </summary>
    public static void WriteComposeFile()
    {
        Directory.CreateDirectory(ComposeDirectory);
        File.WriteAllText(ComposeFile, ComposeFileContents);
    }
}
