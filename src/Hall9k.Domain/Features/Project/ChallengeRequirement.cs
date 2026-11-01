using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One project's effective challenge requirement and where it came from: whether <c>h9k task publish</c>
/// refuses a draft with no usable challenge verdict unless a person states why. Every reader goes through
/// this rather than reading <see cref="Projections.ProjectDetails.RequireChallengeRecorded"/> directly,
/// which cannot say whether anything recorded a value at all.
/// <para>
/// Default on: a project that never chose, including one registered before the setting existed, requires a
/// challenge. <c>h9k project set --require-challenge off</c> turns it off, and the verb still works with it off.
/// </para>
/// </summary>
public sealed record ChallengeRequirement(bool IsOn, bool Recorded)
{
    /// <summary>What a project with nothing recorded resolves to.</summary>
    public const bool Default = true;

    /// <summary>The default resolution itself, for a caller with no project to read (a test, a fresh registration).</summary>
    public static readonly ChallengeRequirement Unrecorded = new(Default, Recorded: false);

    /// <summary>How the effective value is described wherever origin is printed: 'explicit' or 'default'.</summary>
    public string Origin => Recorded
        ? "explicit"
        : "default";

    /// <summary>The one-word state a settings row and a refusal both say.</summary>
    public string OnOff => IsOn
        ? "on"
        : "off";

    /// <summary>The effective requirement for the value a project's stream last recorded, or null when it recorded none.</summary>
    public static ChallengeRequirement From(bool? recorded) =>
        recorded is { } value
            ? new ChallengeRequirement(value, Recorded: true)
            : Unrecorded;

    /// <summary>
    /// The <c>on</c>/<c>off</c> word a human types, refused by name rather than silently read as off. A blank
    /// is refused for the same reason the sibling settings refuse one: at a command line it is an empty shell
    /// variable, never a request.
    /// </summary>
    public static bool ParseOnOff(string? value, string optionName) => value?.Trim().ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new DomainValidationException(
            $"{optionName} takes 'on' or 'off': whether h9k task publish on this project refuses a draft that has "
            + "no usable challenge verdict on record (h9k task challenge) unless the publisher states why with "
            + "--skip-challenge."),
    };
}
