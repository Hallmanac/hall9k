using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="LocalLaunchStepApproval"/> is the single check behind "run-local never executes a
/// step nobody has actually approved" (security review idea 6be68ee2, process-injection finding
/// 3) — deliberately pure, so every case <c>TaskRunLocalCommand</c>'s own gate depends on is
/// exercised here rather than only through a live console and store.
/// </summary>
public sealed class LocalLaunchStepApprovalTests
{
    private static RunSkillStep Command(int number, string command, string text = "run it", string section = "Launch") =>
        new(number, RunSkillStepKind.Command, section, text, command);

    private static RunSkillStep Human(int number, string text) =>
        new(number, RunSkillStepKind.Human, "Human steps", text, string.Empty);

    [Fact]
    public void The_first_run_on_a_node_counts_as_changed()
    {
        IReadOnlyList<RunSkillStep> steps = [Command(1, "npm start")];

        LocalLaunchStepApproval.Changed(lastApprovedFingerprint: null, steps).Should().BeTrue(
            "nothing has ever been approved on this node for this project yet");
    }

    [Fact]
    public void The_identical_plan_is_unchanged()
    {
        IReadOnlyList<RunSkillStep> steps = [Command(1, "npm install"), Command(2, "npm start")];
        string fingerprint = LocalLaunchStepApproval.Fingerprint(steps);

        LocalLaunchStepApproval.Changed(fingerprint, steps).Should().BeFalse();
    }

    [Fact]
    public void A_different_command_is_changed()
    {
        IReadOnlyList<RunSkillStep> approved = [Command(1, "npm start")];
        IReadOnlyList<RunSkillStep> current = [Command(1, "npm run dev")];
        string fingerprint = LocalLaunchStepApproval.Fingerprint(approved);

        LocalLaunchStepApproval.Changed(fingerprint, current).Should().BeTrue();
    }

    /// <summary>
    /// A command split differently across two steps — "a && b" as one step versus "a" and "b" as
    /// two — must not fingerprint identically to a single step "ab", the same collision
    /// <see cref="Project.VerifyCommand.Fingerprint"/> already guards against with a length prefix.
    /// </summary>
    [Fact]
    public void Commands_that_would_collide_unprefixed_fingerprint_differently()
    {
        IReadOnlyList<RunSkillStep> joined = [Command(1, "ab")];
        IReadOnlyList<RunSkillStep> split = [Command(1, "a"), Command(2, "b")];

        LocalLaunchStepApproval.Fingerprint(joined).Should().NotBe(LocalLaunchStepApproval.Fingerprint(split));
    }

    /// <summary>
    /// The fingerprint covers <see cref="RunSkillStep.Command"/> and <see cref="RunSkillStep.Section"/>,
    /// never <see cref="RunSkillStep.Text"/>: a prose-only edit — the wording of a human step, a step's
    /// own descriptive sentence — must never re-open an approval already given to the identical
    /// commands (the acceptance criterion's own "a prose-only change does not prompt").
    /// </summary>
    [Fact]
    public void A_prose_only_edit_does_not_change_the_fingerprint()
    {
        IReadOnlyList<RunSkillStep> original =
        [
            Command(1, "npm start", text: "Start the dev server."),
            Human(2, "Log into the sandbox first."),
        ];
        IReadOnlyList<RunSkillStep> reworded =
        [
            Command(1, "npm start", text: "Fires up the local dev server on the usual port."),
            Human(2, "Obtain a sandbox login before doing anything else."),
        ];

        LocalLaunchStepApproval.Fingerprint(original).Should().Be(LocalLaunchStepApproval.Fingerprint(reworded));
    }

    /// <summary>
    /// The fingerprint must also cover <see cref="RunSkillStep.Section"/>: the same command text
    /// moved between sections is a different plan, because
    /// <see cref="Hall9k.Connectors.Processes.LocalLaunchWalker"/> decides whether a step is
    /// spawned detached and left running, or waited on and checked for failure, purely from which
    /// section it sits in (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_step_moved_to_a_different_section_is_changed()
    {
        IReadOnlyList<RunSkillStep> approved =
        [
            Command(1, "./scripts/verify-deps.sh", section: "One-time setup"),
            Command(2, "npm run dev -- --port 3000", section: "Launch"),
        ];
        IReadOnlyList<RunSkillStep> current =
        [
            Command(1, "./scripts/verify-deps.sh", section: "Launch"),
            Command(2, "npm run dev -- --port 3000", section: "Launch"),
        ];
        string fingerprint = LocalLaunchStepApproval.Fingerprint(approved);

        LocalLaunchStepApproval.Changed(fingerprint, current).Should().BeTrue();
    }

    [Fact]
    public void Matches_the_full_digest()
    {
        string fingerprint = LocalLaunchStepApproval.Fingerprint([Command(1, "npm start")]);

        LocalLaunchStepApproval.Matches(fingerprint, fingerprint).Should().BeTrue();
    }

    [Fact]
    public void Matches_the_short_digest_it_prints()
    {
        string fingerprint = LocalLaunchStepApproval.Fingerprint([Command(1, "npm start")]);
        string shortFingerprint = LocalLaunchStepApproval.ShortFingerprint(fingerprint);

        shortFingerprint.Length.Should().BeLessThan(fingerprint.Length);
        LocalLaunchStepApproval.Matches(shortFingerprint, fingerprint).Should().BeTrue();
    }

    [Fact]
    public void A_stale_fingerprint_does_not_match()
    {
        string fingerprint = LocalLaunchStepApproval.Fingerprint([Command(1, "npm start")]);
        string other = LocalLaunchStepApproval.Fingerprint([Command(1, "npm run dev")]);

        LocalLaunchStepApproval.Matches(other, fingerprint).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_provided_never_matches(string? provided)
    {
        string fingerprint = LocalLaunchStepApproval.Fingerprint([Command(1, "npm start")]);

        LocalLaunchStepApproval.Matches(provided, fingerprint).Should().BeFalse();
    }
}
