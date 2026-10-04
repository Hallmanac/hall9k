namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Which of hall9k's GitHub releases a node's release lookup considers (idea 93c1d24d, Decisions
/// Log 0ecc3701 and 8c039759). Every tag publishes as a pre-release, so <see cref="Cleared"/>
/// (GitHub's own latest release, which excludes pre-releases) sees only releases Brian has
/// cleared with <c>gh release edit &lt;tag&gt; --prerelease=false --latest</c>, and
/// <see cref="All"/> sees every published release, cleared or not. Unknown means "not
/// recognized", the same idiom every other value object here uses for an unusable value read
/// back from a config file.
/// </summary>
public sealed record ReleaseChannel
{
    public static readonly ReleaseChannel Cleared = new("cleared");
    public static readonly ReleaseChannel All = new("all");
    public static readonly ReleaseChannel Unknown = new("");

    public string Value { get; }

    private ReleaseChannel(string value) => Value = value;

    public static ReleaseChannel FromInput(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "cleared" => Cleared,
        "all" => All,
        _ => Unknown,
    };

    public bool IsWellFormed => this == Cleared || this == All;

    public override string ToString() => Value;
}
