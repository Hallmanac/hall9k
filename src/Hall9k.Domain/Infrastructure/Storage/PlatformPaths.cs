namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The platform home every on-disk artifact hangs off: ~/.hall9k, or HALL9K_HOME when it is
/// set (which is how tests point the whole layout at a temp directory). Resolved per call
/// rather than cached, for the same reason.
/// </summary>
public static class PlatformPaths
{
    public static string Home => Environment.GetEnvironmentVariable("HALL9K_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hall9k");
}
