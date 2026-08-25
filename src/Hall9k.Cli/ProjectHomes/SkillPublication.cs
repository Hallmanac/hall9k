namespace Hall9k.Cli.ProjectHomes;

/// <summary>
/// What one <c>h9k install</c> did to the canonical skill set: what it published, what it retired
/// because a previous install published it and this source no longer ships it, and what it found
/// already there under a name it ships and did not touch.
/// <para>
/// <paramref name="Retired"/> is carried rather than left implicit because removing content is
/// the one thing here a person cannot undo by re-running the command. It is reported by name.
/// </para>
/// <para>
/// <paramref name="LeftAlone"/> is carried for the mirror-image reason: nothing was removed, but
/// the platform's own version of that skill is not what any agent will read, and a shadow nobody
/// is told about is discovered later as a skill that would not update.
/// </para>
/// </summary>
/// <param name="Published">Skill names now in the canonical set, in a stable order.</param>
/// <param name="Retired">Skill names this install removed, in a stable order.</param>
/// <param name="LeftAlone">
/// Skill names this install ships that were already in the canonical directory without any
/// install having put them there. The existing content stays and shadows the shipped version.
/// </param>
/// <param name="ManifestUnconfirmed">
/// True when the published manifest exists but could not be read this pass (a Windows antivirus
/// scan or an editor holding it open) — not the same fact as no manifest ever existing. When
/// true, nothing was published, retired, or classified as an override: without the manifest,
/// an already-present shipped skill cannot be told apart from an operator's own file of the same
/// name, so <see cref="Published"/>, <see cref="Retired"/>, and <see cref="LeftAlone"/> are all
/// empty rather than guessed at, and the caller should say so rather than report any of them.
/// </param>
public sealed record SkillPublication(
    IReadOnlyList<string> Published,
    IReadOnlyList<string> Retired,
    IReadOnlyList<string> LeftAlone,
    bool ManifestUnconfirmed = false);
