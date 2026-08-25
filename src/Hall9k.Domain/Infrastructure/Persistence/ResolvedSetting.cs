namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// One operating setting's effective value, where it came from, and — for
/// <see cref="SettingOrigin.EnvironmentVariable"/> or <see cref="SettingOrigin.PlatformConfigFile"/>
/// — the name or path that supplied it (backlog 59).
/// </summary>
public sealed record ResolvedSetting<T>(T Value, SettingOrigin Origin, string? Source)
{
    /// <summary>The short "(env: NAME)" / "(config: PATH)" / "(default)" suffix every rendering uses.</summary>
    public string DescribeOrigin() => Origin switch
    {
        SettingOrigin.EnvironmentVariable => $"env: {Source}",
        SettingOrigin.PlatformConfigFile => $"config: {Source}",
        _ => "default",
    };
}
