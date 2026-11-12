namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Reads the environment variables the daemon sets on the CLI child it starts for a plan
/// (<c>HALL9K_PLAN_REFINEMENT_SESSION</c> on a refinement session's revise, <c>HALL9K_PLAN_PUBLISH</c> on a plan's publish).
/// <para>
/// A test-only, flow-scoped override sits ahead of the real variable, the same shape as <c>PlatformPaths.HomeOverrideForTests</c>.
/// The test project runs the command tree in process, and many classes run <c>task revise</c> or <c>task publish</c> in parallel,
/// so a test that set the real variable changed what every other one running at that moment read: <c>PlanCommandTests</c> setting
/// the refinement session once made <c>FeedbackStepCommandTests</c>' own revise refuse as a stale session's (CI on PR #877). An
/// override is an <see cref="AsyncLocal{T}"/>, so it reaches only the flow that opened it and everything that flow awaits. No
/// production code ever sets it.
/// </para>
/// </summary>
internal static class PlanHandoffEnvironment
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string?>?> Overrides = new();

    public static string? Read(string name) =>
        Overrides.Value is { } overrides && overrides.TryGetValue(name, out string? value)
            ? value
            : Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Overrides <paramref name="name"/> for the calling flow until the returned scope is disposed. Open it in the test method
    /// itself, never inside an <c>async Task InitializeAsync()</c>, whose own flow does not carry the write back out.
    /// </summary>
    internal static IDisposable OverrideForTests(string name, string? value)
    {
        IReadOnlyDictionary<string, string?>? previous = Overrides.Value;
        Overrides.Value = new Dictionary<string, string?>(previous ?? new Dictionary<string, string?>()) { [name] = value };
        return new Restore(previous);
    }

    private sealed class Restore(IReadOnlyDictionary<string, string?>? previous) : IDisposable
    {
        public void Dispose() => Overrides.Value = previous;
    }
}
