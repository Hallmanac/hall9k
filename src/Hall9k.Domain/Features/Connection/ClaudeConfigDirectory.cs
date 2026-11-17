using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// The one path rule for a Claude Code config directory: absolute after <c>~</c> is expanded, with no
/// trailing separator, and never symlink-resolved. The string this returns is what a claude-code
/// connection records, what every spawn passes as <c>CLAUDE_CONFIG_DIR</c>, and what every fix line
/// prints, because on macOS Claude Code keys the login's Keychain item to a hash of that exact string.
/// </summary>
public static class ClaudeConfigDirectory
{
    /// <summary>The variable Claude Code chooses its account by.</summary>
    public const string Variable = "CLAUDE_CONFIG_DIR";

    /// <summary>
    /// Normalizes <paramref name="raw"/> against <paramref name="homeDirectory"/>, or refuses it: a relative
    /// path names a different directory from every working directory it is read in, and a filesystem root is
    /// never a Claude Code config directory anybody meant.
    /// </summary>
    public static string Normalize(string raw, string homeDirectory)
    {
        string path = raw.Trim();
        if (path.Length == 0)
        {
            throw new DomainValidationException(
                "A claude-code connection needs the Claude Code config directory it runs under, for example ~/.claude-alt.");
        }

        if (path == "~")
        {
            path = homeDirectory;
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Join(homeDirectory, path[2..]);
        }

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new DomainValidationException(
                $"'{raw}' is not an absolute path. Give the Claude Code config directory as an absolute path or "
                + "one starting with ~/, for example ~/.claude-alt, so every process Hall9k starts reads the same directory.");
        }

        if (trimmed.Length == 0
            || (Path.GetPathRoot(path) is { } root
                && root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) == trimmed))
        {
            throw new DomainValidationException(
                $"'{raw}' is a filesystem root, not a Claude Code config directory. Name the directory itself, "
                + "for example ~/.claude-alt.");
        }

        return trimmed;
    }
}
