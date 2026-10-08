namespace Hall9k.Connectors.Prompts;

/// <summary>
/// One <c>gh &lt;group&gt; &lt;verb&gt;</c> a command can be tested for. Some verbs only speak to a
/// person when they carry a flag: <c>gh pr close</c> closes quietly and <c>gh pr close --comment
/// "…"</c> posts at the top level of the pull request, so such a route lists the flags (long names
/// as <c>--comment</c>, short ones as <c>-c</c>) of which the command must carry at least one.
/// </summary>
internal sealed record GhRoute(string Group, string Verb, string[] AnyOfFlags)
{
    public GhRoute(string group, string verb)
        : this(group, verb, [])
    {
    }

    /// <summary>
    /// Whether <paramref name="word"/> is one of the flags, in the spellings cobra accepts: the bare
    /// name, <c>--flag=value</c>, and a short flag with its value attached (<c>-c"text"</c>).
    /// </summary>
    public bool NamesAFlag(string word) =>
        AnyOfFlags.Any(flag => flag.StartsWith("--", StringComparison.Ordinal)
            ? word.Equals(flag, StringComparison.Ordinal)
                || word.StartsWith(flag + "=", StringComparison.Ordinal)
            : word.StartsWith(flag, StringComparison.Ordinal) && !word.StartsWith("--", StringComparison.Ordinal));
}
