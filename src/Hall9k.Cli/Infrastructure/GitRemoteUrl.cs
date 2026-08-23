using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Turns what somebody types after <c>--repo-url</c> into the <see cref="Uri"/> the platform
/// records. The reason this exists rather than a bare <c>new Uri(…)</c> is the scp-style remote,
/// <c>git@github.com:you/project.git</c>, which is the form GitHub's own Code button offers and
/// the ordinary way to reach a private repository — and which is not a URI at all, so
/// <c>new Uri</c> throws <see cref="UriFormatException"/> on it.
/// <para>
/// It is rewritten into its <c>ssh://</c> equivalent rather than refused: the two name the same
/// remote, git accepts either, and refusing the spelling every SSH user already has in their
/// clipboard would leave private repositories with no working registration path. The rewrite is
/// what gets recorded, so the render that reads <c>Uri.Host</c> to decide a project needs
/// <c>gh</c> keeps working.
/// </para>
/// <para>
/// Origin incident (2026-08-23): the pre-PR review of the project-home branch found the README's
/// own quickstart line, <c>h9k project add --name myproject --repo-url
/// git@github.com:you/myproject.git</c>, dying with an unhandled <c>UriFormatException</c> —
/// <c>Program.cs</c> maps the domain exceptions and nothing else, so the user got a stack trace
/// and no project.
/// </para>
/// </summary>
public static class GitRemoteUrl
{
    /// <summary>
    /// The remote as a <see cref="Uri"/>, or a refusal naming the forms that work. Never throws
    /// <see cref="UriFormatException"/>: every rejection is a <see cref="DomainValidationException"/>
    /// the CLI already maps to an exit code and a message an agent can self-correct from.
    /// </summary>
    public static Uri Parse(string value)
    {
        string trimmed = value.Trim();
        string candidate = IsScpStyle(trimmed) ? ToSshUrl(trimmed) : trimmed;

        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? remote)
            ? remote
            : throw new DomainValidationException(
                $"'{trimmed}' is not a git remote this can record. Pass an https URL "
                + "(https://github.com/you/project.git), an ssh URL "
                + "(ssh://git@github.com/you/project.git), or the scp-style form git offers "
                + "(git@github.com:you/project.git). To register against a repository that already "
                + "exists on this machine, use --repo <path> instead.");
    }

    /// <summary>
    /// git's own rule for telling an scp-style remote from a path: a colon before the first
    /// slash, and no <c>://</c> to say it is already a URL. The colon has to be past the first
    /// character so a Windows drive letter (<c>C:\repos\project</c>) is left as the local path it
    /// is, to be refused below rather than mangled into a hostname.
    /// </summary>
    private static bool IsScpStyle(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        int colon = value.IndexOf(':');
        int slash = value.IndexOf('/');
        return colon > 1 && (slash < 0 || colon < slash);
    }

    /// <summary>
    /// <c>git@host:owner/repo.git</c> is <c>ssh://git@host/owner/repo.git</c>: one separator
    /// changes and the scheme is spelled out. Only the first colon moves, so a remote whose path
    /// contains one keeps it.
    /// </summary>
    private static string ToSshUrl(string value)
    {
        int colon = value.IndexOf(':');
        return $"ssh://{value[..colon]}/{value[(colon + 1)..].TrimStart('/')}";
    }
}
