using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The <c>--voice-skill</c> / <c>--clear-voice-skill</c> pair's own option shape for
/// <c>h9k owner set</c>, kept beside <see cref="ReviewRerequestOption"/> and out of the command's
/// own <c>ExecuteAsync</c> so the refusals are readable, and testable, without a database.
/// <para>
/// Two options rather than one word: every value <c>--voice-skill</c> takes is a real skill
/// directory name, and one of them could legitimately be spelled <c>default</c>, so a clearing
/// word inside that option's vocabulary would be a name an owner could never use.
/// </para>
/// </summary>
internal static class VoiceSkillOption
{
    /// <summary>
    /// What the command records: <see cref="Optional{T}.None"/> when neither option was passed
    /// (leave the preference alone), <see cref="VoiceSkillName.None"/> for
    /// <c>--clear-voice-skill</c> (an explicit "forget it"), and the parsed name otherwise.
    /// </summary>
    public static Optional<VoiceSkillName> Resolve(string? voiceSkill, bool clear)
    {
        if (voiceSkill is not null && clear)
        {
            throw new DomainValidationException(
                "--voice-skill and --clear-voice-skill ask for opposite things. Pass one: the name "
                + "to record it, --clear-voice-skill to forget whatever is recorded now.");
        }

        if (voiceSkill is null)
        {
            return clear ? Optional<VoiceSkillName>.Of(VoiceSkillName.None) : Optional<VoiceSkillName>.None;
        }

        VoiceSkillName parsed = VoiceSkillName.Parse(voiceSkill);
        // Blank reaches here only from `--voice-skill ""`, which reads as a request to clear —
        // refused rather than honoured, so the one command that forgets the preference is the one
        // named for it and an empty shell variable never silently wipes a setting.
        return parsed.HasValue
            ? Optional<VoiceSkillName>.Of(parsed)
            : throw new DomainValidationException(
                "--voice-skill needs a skill name. To forget the one recorded now, pass "
                + "--clear-voice-skill.");
    }

    /// <summary>How the preference reads in <c>h9k owner show</c>'s pane.</summary>
    public static string Describe(VoiceSkillName voiceSkill) => voiceSkill.HasValue
        ? $"{voiceSkill.Value.EscapeMarkup()} [dim]— loaded by name at every prompt seam that writes "
          + "text a human reads as this owner's[/]"
        : "[dim]none named — prompts render in the platform's own voice, with the project's writing "
          + "conventions[/]";
}
