using System.Diagnostics;
using System.Globalization;

namespace Hall9k.Connectors.Processes;

/// <summary>
/// Pins git's interactive knobs off for a process the platform spawns, so a rebase, commit, or
/// cherry-pick running on an operator's behalf can never block on that operator's own global
/// git configuration. The environment is inherited by every git the process starts in turn, which
/// is what lets a verification gate's own child git (a test fixture rebasing a throwaway repo)
/// stay non-interactive without every fixture knowing to ask.
/// <para>
/// Origin incident (2026-09-05): a test in an in-run-rebase branch performed a real rebase inside
/// a temp fixture; one step ran <c>git commit -S -e</c>, git fell through to the operator's
/// global <c>core.editor = code --wait</c> and <c>commit.gpgsign = true</c>, VS Code opened a
/// commit message for the fixture's <c>widget.cs</c>, and the git process, the test, and the
/// session behind it hung for sixteen minutes until the operator noticed the editor window. The
/// only mitigation anywhere before this was prompt text asking agents to prefix
/// <c>GIT_SEQUENCE_EDITOR=:</c> on their own autosquash rebases.
/// </para>
/// <para>
/// Signing is switched off through <c>GIT_CONFIG_COUNT</c> rather than a <c>-c</c> argument so
/// it reaches child processes too; an existing <c>GIT_CONFIG_COUNT</c> in the inherited
/// environment is respected and appended to rather than overwritten. Agent sessions are
/// deliberately not spawned through here: their commits carry the operator's identity and
/// signing exactly as they always have.
/// </para>
/// </summary>
public static class NonInteractiveGit
{
    public static void Apply(ProcessStartInfo startInfo)
    {
        startInfo.Environment["GIT_EDITOR"] = "true";
        startInfo.Environment["GIT_SEQUENCE_EDITOR"] = ":";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // NumberStyles.None rejects a sign, so a negative inherited count (which git itself
        // refuses as "too many entries", except -1, which it reads as zero entries and silently
        // drops every override, this one included) starts over at index 0 and count 1 rather
        // than being appended to.
        int existing = startInfo.Environment.TryGetValue("GIT_CONFIG_COUNT", out string? count)
            && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
        string index = existing.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[$"GIT_CONFIG_KEY_{index}"] = "commit.gpgsign";
        startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"] = "false";
        startInfo.Environment["GIT_CONFIG_COUNT"] = (existing + 1).ToString(CultureInfo.InvariantCulture);
    }
}
