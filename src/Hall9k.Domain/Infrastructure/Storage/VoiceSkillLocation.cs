using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The two places a <see cref="VoiceSkillName"/> can actually be found, and the refusal when it
/// is in neither. An owner's voice skill is theirs, not the platform's, so unlike
/// <see cref="SkillLibraryPaths"/> nothing here publishes, seeds, or owns a directory: this type
/// only looks.
/// <para>
/// Two tiers, least specific first, the same order the skill tiers already resolve in (Decisions
/// Log #76): the owner's own user-level skills at <c>~/.claude/skills/&lt;name&gt;</c>, which is
/// where a voice written once and used everywhere belongs and which the daemon already makes
/// visible to every session it launches (<c>ClaudeExecutor</c> loads the owner's <c>~/.claude</c>
/// settings for trusted and untrusted worktrees alike); then a project home's own
/// <c>skills/&lt;name&gt;</c>, for a voice that is a team's rather than a person's.
/// </para>
/// <para>
/// Checked where the human types it (<c>h9k owner set --voice-skill</c>) rather than at the prompt
/// seam that names it, because a preference recording a skill nobody has would fail silently at
/// every seam afterwards, hours later, in a session's own prompt nobody reads.
/// </para>
/// </summary>
public static class VoiceSkillLocation
{
    /// <summary>
    /// The owner's own user-level skill directory. Not a platform path — it belongs to Claude Code
    /// and to the human, which is exactly why a voice skill can live in it and be the same voice on
    /// every project this owner runs.
    /// </summary>
    public static string UserSkillsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");

    /// <summary>
    /// Every directory a named voice skill could be, in resolution order, against the real
    /// <see cref="UserSkillsDirectory"/>. <paramref name="projectHomes"/> is the homes of the
    /// projects this owner actually owns, in whatever order the caller read them.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(
        VoiceSkillName name, IEnumerable<string> projectHomes) =>
        CandidateDirectories(name, UserSkillsDirectory, projectHomes);

    /// <summary>
    /// <see cref="CandidateDirectories(VoiceSkillName, IEnumerable{string})"/>'s own
    /// implementation, with the user skills directory pulled out as a parameter so this is
    /// testable against a scratch directory rather than the operator's own live Claude Code
    /// state — the same seam <c>TaskRegisterSessionCommand.ReadClaudeSessionName</c> already uses
    /// for the sibling <c>~/.claude/sessions</c> directory.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(
        VoiceSkillName name, string userSkillsDirectory, IEnumerable<string> projectHomes) =>
    [
        Path.Combine(userSkillsDirectory, name.Value),
        .. projectHomes.Select(home => Path.Combine(ProjectHomePaths.SkillsDirectory(home), name.Value)),
    ];

    /// <summary>
    /// Whether <paramref name="directory"/> is a skill rather than merely a directory: it carries
    /// a <c>SKILL.md</c>, the same discriminator <see cref="SkillLibraryPaths.Published"/> uses.
    /// Nothing about a bare directory's existence says a session could load it.
    /// </summary>
    public static bool IsSkillDirectory(string directory) =>
        File.Exists(Path.Combine(directory, "SKILL.md"));

    /// <summary>
    /// The first of <paramref name="candidates"/> that is actually a skill, or null when the name
    /// resolves to nothing anywhere.
    /// </summary>
    public static string? Locate(IReadOnlyList<string> candidates) =>
        candidates.FirstOrDefault(IsSkillDirectory);

    /// <summary>
    /// The located skill's own directory, or a refusal naming every path it looked in — so the
    /// human sees where to put the skill (or which spelling they meant) rather than a bare "not
    /// found", per the CLI standard that a failure prints why.
    /// </summary>
    public static string Resolve(
        VoiceSkillName name, string userSkillsDirectory, IEnumerable<string> projectHomes)
    {
        IReadOnlyList<string> candidates = CandidateDirectories(name, userSkillsDirectory, projectHomes);
        if (Locate(candidates) is { } found)
        {
            return found;
        }

        // Named rather than counted: an owner with no project home registered yet has exactly one
        // tier to look in, and a refusal that said "neither" would be naming a second path it
        // never checked (AGENTS.md's never-guess rule).
        string looked = candidates.Count == 1
            ? $"{candidates[0]} — the only tier there is to look in, since no project home is "
              + "registered to this owner yet"
            : string.Join(" and ", candidates);
        throw new DomainValidationException(
            $"No voice skill named '{name.Value}' — looked in {looked}, and no SKILL.md is there. "
            + "A voice skill is the owner's own, referenced by name and never copied into this "
            + "repository, so it has to exist in one of those tiers before a prompt seam can tell a "
            + "session to load it: put it in the user skills directory for a voice that is yours on "
            + "every project, or in a project home's skills/ for one that is that team's.");
    }

    /// <summary>
    /// <see cref="Resolve(VoiceSkillName, string, IEnumerable{string})"/> against the real
    /// <see cref="UserSkillsDirectory"/>, which is what the CLI calls.
    /// </summary>
    public static string Resolve(VoiceSkillName name, IEnumerable<string> projectHomes) =>
        Resolve(name, UserSkillsDirectory, projectHomes);
}
