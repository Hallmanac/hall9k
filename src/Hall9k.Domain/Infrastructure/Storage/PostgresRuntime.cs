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

    public static string ComposeDirectory => Path.Combine(PlatformPaths.Home, "postgres");

    public static string ComposeFile => Path.Combine(ComposeDirectory, "docker-compose.yml");

    /// <summary>
    /// Mirrors the repository's own <c>docker-compose.yml</c> exactly — that file is what a
    /// contributor reads and runs by hand from a checkout; this constant is what ships inside
    /// the binary so an installed user needs neither. Keep the two in sync by hand: both are
    /// small and change rarely.
    /// </summary>
    public const string ComposeFileContents = """
        # Hall9k-owned Postgres for installed mode (h9kd under launchd or started by
        # h9k daemon start). Written here by h9k install; never edited in place — a local
        # change is lost the next time install republishes it.
        services:
          postgres:
            image: postgres:18
            container_name: hall9k-postgres
            restart: unless-stopped
            environment:
              POSTGRES_DB: hall9k
              POSTGRES_USER: postgres
              POSTGRES_PASSWORD: hall9k
            ports:
              - "5432:5432"
            volumes:
              - hall9k-pgdata:/var/lib/postgresql

        volumes:
          hall9k-pgdata:

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
