using System.Globalization;
using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Whether a run skill's launch command lets the caller choose the port, and the substitution when
/// it does (idea b9b09779, piece 5). A reviewer's machine is already running things, and a review
/// launch that seized the project's own default port would collide with whatever the reviewer had
/// up — so the launch takes an ephemeral port wherever the command has somewhere to put one.
/// <para>
/// Three forms, and only three, because each is a place the command itself already names a port:
/// the explicit <c>{{PORT}}</c> placeholder a hand-set skill can carry, a <c>--port</c> option, and
/// a leading <c>PORT=</c> environment assignment. A command naming no port is run exactly as
/// written and the launch records no port of its own, rather than guessing at a flag this
/// project's binary may not have.
/// </para>
/// </summary>
public static partial class RunSkillLaunchPort
{
    /// <summary>The placeholder a run skill written by hand can carry to say "any free port will do".</summary>
    public const string Placeholder = "{{PORT}}";

    /// <summary>
    /// <paramref name="command"/> with <paramref name="port"/> substituted, or null when the
    /// command names no port for the caller to choose. Null is a real answer, not a failure: the
    /// launch runs the command as written and says the port was the project's own.
    /// </summary>
    public static string? Substitute(string command, int port)
    {
        string text = port.ToString(CultureInfo.InvariantCulture);
        if (command.Contains(Placeholder, StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(command, Regex.Escape(Placeholder), text, RegexOptions.IgnoreCase);
        }

        if (PortOption().IsMatch(command))
        {
            return PortOption().Replace(command, match => $"{match.Groups[1].Value}{text}", 1);
        }

        return PortEnvironment().IsMatch(command)
            ? PortEnvironment().Replace(command, match => $"{match.Groups[1].Value}{text}", 1)
            : null;
    }

    /// <summary>
    /// The skill's own address line with the chosen port swapped in, for the one case where that
    /// is a fact rather than a guess: the address names a loopback host and a port, and the launch
    /// actually chose a port. Anything else is returned untouched — a deployed hostname, a socket
    /// path, or a CLI binary has no port of ours in it, and rewriting a number inside one would
    /// hand the reviewer an address nothing is listening on.
    /// </summary>
    public static string ApplyToAddress(string address, int? port) =>
        port is { } chosen && address.IsNotBlank()
            ? LoopbackPort().Replace(address, match => $"{match.Groups[1].Value}{chosen.ToString(CultureInfo.InvariantCulture)}")
            : address;

    [GeneratedRegex(@"(--port[=\s]+)\d+", RegexOptions.IgnoreCase)]
    private static partial Regex PortOption();

    [GeneratedRegex(@"(\bPORT=)\d+")]
    private static partial Regex PortEnvironment();

    [GeneratedRegex(@"((?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):)\d+", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackPort();
}
