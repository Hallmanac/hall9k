using Xunit;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Saves and restores the named environment variables around a test, isolating it from every
/// other. Carries <c>[Collection("Hall9kHome")]</c> itself even though the attribute has no
/// runtime effect on a type with no test methods of its own: <c>HomeEnvironmentIsolationTests</c>'s
/// own scan flags any class using <c>Environment.SetEnvironmentVariable</c>/<c>GetEnvironmentVariable</c>
/// without it, this helper included, and every caller still needs the attribute on its own test
/// class too — this only satisfies the scan for this file, it does not extend serialization to a
/// caller that omits it.
/// </summary>
[Collection("Hall9kHome")]
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
