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

        if (!Path.IsPathRooted(trimmed))
        {
            throw new DomainValidationException(
                $"'{trimmed}' is not an absolute path. A project's home is recorded once and read "
                + "back by the daemon, which runs in no particular directory, so a relative path "
                + "would name a different place for every caller. Pass a full path "
                + "(~/.hall9k/projects/<name> is the default).");
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
