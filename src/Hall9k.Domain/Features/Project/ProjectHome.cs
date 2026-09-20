using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Where a project lives on this machine — the directory holding its generated AGENTS.md,
/// its repo, its ideas, its tasks and its skills. A value object per the house type discipline
/// (TASK-MODEL.md §8) rather than a second bare path string beside <c>RepositoryPath</c>.
/// <para>
/// The rule it carries is that a home is always an absolute path. A relative one is resolved
/// against whatever directory the caller happened to be in, so recording it would record a
/// different place for every shell that typed it — and the home is read back by the daemon,
/// which is in no directory at all. Callers resolve relative input themselves, where the
/// current directory still means something; this type refuses what reaches it unrooted.
/// </para>
/// <para>
/// <see cref="None"/> is the honest absence: a project registered before homes existed, or one
/// whose home has not been created yet. It serializes as the empty string, so a stream written
/// before this type replays into it unchanged. Location is a setting; the shape inside it is
/// the contract (ruled at the project-home discovery, 2026-08-23).
/// </para>
/// <para>
/// A value replicated from another node (an idea's <c>WorkspaceHomeDirectory</c>, chiefly) may be
/// rooted in a form foreign to THIS host — a Windows path replayed on macOS, or the reverse — since
/// the field records where a home lives on the node that captured it, not this one. Rejecting that
/// as "not absolute" would abort every later event from that sender (idea 202383dc's own worst
/// case). <see cref="Parse"/> recognises both the POSIX and the Windows shape of an absolute path
/// on every host; only a value rooted in the CURRENT host's own shape is run through
/// <see cref="Path.GetFullPath(string)"/>, since normalising a foreign-form path with this host's
/// own rules would mangle it (<c>Path.GetFullPath</c> on macOS reads a Windows drive path as
/// relative and prepends the working directory to it). A foreign-form value is kept exactly as
/// received — this type's whole job is recording the fact, never resolving it into a directory on
/// this machine.
/// </para>
/// </summary>
[JsonConverter(typeof(ProjectHomeJsonConverter))]
public sealed record ProjectHome
{
    /// <summary>No home recorded. <c>h9k project init &lt;name&gt;</c> is what ends this state.</summary>
    public static readonly ProjectHome None = new(string.Empty);

    public string Value { get; }

    private ProjectHome(string value) => Value = value;

    /// <summary>True when a home is actually recorded.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>
    /// True when a recorded home's own path SHAPE matches this host's own operating system family —
    /// false for one replicated from a node on a different operating system FAMILY (a Windows path
    /// read on macOS or Linux, or the reverse), and false for <see cref="None"/>, which names no
    /// shape at all. A caller that would read or create a directory from <see cref="Value"/> checks
    /// this first: a foreign-form value is a node-local fact about a different machine, never a path
    /// this one can resolve (see this type's own doc comment).
    /// <para>
    /// Shape alone cannot tell two hosts of the SAME family apart: a POSIX-rooted home replicated
    /// from another macOS or Linux node reads as native here too, even though it names a directory
    /// on that other machine, not this one (independent pre-PR review, cycle 1, adversarial lens,
    /// low). Nothing today acts on a same-family foreign path without a best-effort catch around
    /// the filesystem call it makes, but a future caller relying on this property alone to mean
    /// "safe to touch a directory for on this host" would be wrong for that pair.
    /// </para>
    /// </summary>
    public bool IsNativeForm => HasValue && (OperatingSystem.IsWindows() ? IsWindowsRooted(Value) : IsPosixRooted(Value));

    /// <summary>A leading slash — the POSIX shape of an absolute path, recognised on every host regardless of which one is running.</summary>
    private static bool IsPosixRooted(string path) => path.Length > 0 && path[0] == '/';

    /// <summary>
    /// The Windows shape of an absolute path, recognised on every host regardless of which one is
    /// running: a drive letter followed by a colon and a separator (<c>C:\...</c> or <c>C:/...</c>),
    /// or a UNC path's leading double backslash (<c>\\server\share</c>). A bare leading backslash
    /// with no drive letter (<c>\Users\bob</c>) is drive-relative, not absolute, and deliberately
    /// does not match either shape here.
    /// </summary>
    private static bool IsWindowsRooted(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || (path.Length >= 2 && path[0] == '\\' && path[1] == '\\');

    /// <summary>
    /// The home as an absolute path, or a refusal naming the rule. Blank is <see cref="None"/>
    /// rather than an error: clearing the recorded home is a legitimate thing to ask for, and it
    /// is how a project says "no home here yet".
    /// </summary>
    public static ProjectHome Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            return None;
        }

        bool posixRooted = IsPosixRooted(trimmed);
        bool windowsRooted = IsWindowsRooted(trimmed);
        if (!posixRooted && !windowsRooted)
        {
            throw new DomainValidationException(
                $"'{trimmed}' is not an absolute path. A project's home is recorded once and read "
                + "back by the daemon, which runs in no particular directory, so a relative path "
                + "would name a different place for every caller. Pass a full path "
                + "(~/.hall9k/projects/<name> is the default).");
        }

        bool nativeForm = OperatingSystem.IsWindows() ? windowsRooted : posixRooted;
        if (!nativeForm)
        {
            // Rooted in the OTHER host's own shape — a value replicated from a node on a
            // different operating system. Kept exactly as received: running it through
            // GetFullPath below would apply THIS host's own separator and rooting rules to a
            // path that was never in that shape to begin with, mangling it rather than
            // normalising it.
            return new ProjectHome(trimmed);
        }

        // Collapses . and .. and any duplicated separators, so two spellings of one directory
        // are one recorded home. Nothing here touches the filesystem: the path is normalised as
        // text, and whether it exists is the recipe's business rather than this type's.
        return new ProjectHome(Path.GetFullPath(trimmed));
    }

    public override string ToString() => Value;

    private sealed class ProjectHomeJsonConverter : JsonConverter<ProjectHome>
    {
        // Reading is deliberately not Parse: a value already on an event stream is a record of
        // where a home was, and a rule tightened later must not make an old document unreadable.
        public override ProjectHome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } stored && stored.IsNotBlank() ? new ProjectHome(stored) : None;

        public override void Write(Utf8JsonWriter writer, ProjectHome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
