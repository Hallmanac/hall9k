using System.Security.Cryptography;
using System.Text;
using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Whether the current run skill's parsed command steps are the ones this node last ran here, and
/// the identity that answers it (security review idea 6be68ee2, process-injection finding 3):
/// <c>h9k task run-local</c> refuses to run a step nobody has actually approved — a replicated
/// skill from a teammate's node, or a hand edit nobody has looked at, must not ride an earlier
/// approval of a different plan. Deliberately pure, the same reason
/// <see cref="Hall9k.Domain.Features.Project.GateSetAcceptance"/> is: every case
/// <c>TaskRunLocalCommand</c>'s own gate depends on is exercised here rather than only through a
/// live console and store.
/// <para>
/// The fingerprint covers every step's own <see cref="RunSkillStep.Command"/> and
/// <see cref="RunSkillStep.Section"/>, in plan order, never <see cref="RunSkillStep.Text"/>: a
/// prose-only edit (the "how to know it is up" wording, a human step's own instructions) changes
/// nothing a shell would ever run, so it must not re-open an approval an operator already gave to
/// the identical commands. Section is not prose here — <see cref="Hall9k.Connectors.Processes.LocalLaunchWalker"/>
/// reads it to decide whether a step is spawned detached with its port substituted in, or run to
/// completion and checked for failure — so a command moved between sections with its text
/// untouched (a setup check relocated into <c>## Launch</c>, say) is a different plan and must
/// re-open approval too. Each command and section is length-prefixed before joining, the same
/// discipline <see cref="Project.VerifyCommand.Fingerprint"/> already uses and for the same
/// reason: two different plans whose commands differ only in where one ends and the next begins
/// must never collide on the same digest.
/// </para>
/// </summary>
public static class LocalLaunchStepApproval
{
    /// <summary>
    /// How much of the full digest is shown to an operator and matched against <c>--approve</c> —
    /// short enough to read and retype without a paste, long enough that a fingerprint typed by
    /// hand for a different plan is not going to collide with it by accident.
    /// </summary>
    private const int ShortLength = 12;

    /// <summary>
    /// Whether <paramref name="steps"/> is a different plan from the one <paramref name="lastApprovedFingerprint"/>
    /// names. A null fingerprint — nothing has ever been approved on this node for this project — is
    /// always a change: the first run on a node counts as changed, the same as any other.
    /// </summary>
    public static bool Changed(string? lastApprovedFingerprint, IReadOnlyList<RunSkillStep> steps) =>
        lastApprovedFingerprint is null
        || !string.Equals(lastApprovedFingerprint, Fingerprint(steps), StringComparison.Ordinal);

    /// <summary>The SHA-256 of every step's own command and section, in plan order — the whole digest, recorded on approval and compared on every later run.</summary>
    public static string Fingerprint(IReadOnlyList<RunSkillStep> steps) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(steps))));

    /// <summary>The prefix of <paramref name="fingerprint"/> shown to an operator and accepted from <c>--approve</c>.</summary>
    public static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length <= ShortLength ? fingerprint : fingerprint[..ShortLength];

    /// <summary>
    /// Whether <paramref name="provided"/> — whatever an operator typed after <c>--approve</c> —
    /// names exactly the plan <paramref name="fingerprint"/> was computed from, matched against
    /// either the full digest or the short one that is actually printed, since the short one is
    /// what an operator can plausibly type back.
    /// </summary>
    public static bool Matches(string? provided, string fingerprint) =>
        provided.IsNotBlank()
        && (string.Equals(provided, fingerprint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provided, ShortFingerprint(fingerprint), StringComparison.OrdinalIgnoreCase));

    private static string Canonical(IReadOnlyList<RunSkillStep> steps) =>
        string.Join(
            '\u001f',
            steps.Select(step => FormattableString.Invariant(
                $"{step.Section.Length}:{step.Section}:{step.Command.Length}:{step.Command}")));
}
