namespace Hall9k.Tests.Fakes;

/// <summary>
/// Saves and restores the named environment variables around a test, isolating it from every
/// other — the shared helper for whichever process-wide variable has no flow-scoped alternative
/// (the claude path, a <c>Hall9k__*</c> setting, the MSBuild node-reuse flag, and so on;
/// <c>HALL9K_HOME</c>/<c>HALL9K_CONNECTION_STRING</c> themselves go through <c>ScopedTestHome</c>/
/// <c>ScopedConnectionString</c> instead, Decisions Log PLACEHOLDER-98484f36). Every caller still
/// needs <c>[Collection("Environment")]</c> on its own test class — this generic helper's own
/// parameterized <c>name</c> argument is not something a source scan can trace back to a specific
/// variable, so it carries no attribute of its own to satisfy.
/// </summary>
public sealed class EnvironmentVariableScope : IDisposable
{
    private readonly (string Name, string? Previous)[] _saved;

    private EnvironmentVariableScope((string Name, string? Previous)[] saved) => _saved = saved;

    public static EnvironmentVariableScope Clear(params string[] names) =>
        Set([.. names.Select(name => (name, (string?)null))]);

    public static EnvironmentVariableScope Set(params (string Name, string? Value)[] values)
    {
        (string Name, string? Previous)[] saved =
            [.. values.Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))];
        foreach ((string name, string? value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return new EnvironmentVariableScope(saved);
    }

    public void Dispose()
    {
        foreach ((string name, string? previous) in _saved)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
