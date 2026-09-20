namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The platform home every on-disk artifact hangs off: ~/.hall9k, or HALL9K_HOME when it is
/// set (which is how a child process, or a shell, points the whole layout at a temp directory).
/// Resolved per call rather than cached, for the same reason.
/// <para>
/// A third, higher-precedence source sits ahead of the environment variable:
/// <see cref="HomeOverrideForTests"/>, an <see cref="AsyncLocal{T}"/> the test project's
/// <c>ScopedTestHome</c> helper opens for its own async flow — and everything that flow starts —
/// so two tests running in parallel inside the same process never observe each other's redirected
/// home the way two tests racing the process-wide environment variable once could. No production
/// code ever sets it; the setter exists only for that one test-only caller (Decisions Log
/// PLACEHOLDER-98484f36).
/// </para>
/// </summary>
public static class PlatformPaths
{
    private static readonly AsyncLocal<string?> HomeOverride = new();

    public static string Home => HomeOverride.Value
        ?? Environment.GetEnvironmentVariable("HALL9K_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hall9k");

    /// <summary>
    /// Test-only flow-scoped override of <see cref="Home"/>, reachable only through
    /// <c>InternalsVisibleTo</c> for <c>Hall9k.Tests</c>. Set it from a constructor or field
    /// initializer, never inside an <c>async Task InitializeAsync()</c> — xUnit invokes that method
    /// as its own async flow, and an <see cref="AsyncLocal{T}"/> write made there does not survive
    /// back out to the test method xUnit runs after it returns.
    /// </summary>
    internal static string? HomeOverrideForTests
    {
        get => HomeOverride.Value;
        set => HomeOverride.Value = value;
    }
}
