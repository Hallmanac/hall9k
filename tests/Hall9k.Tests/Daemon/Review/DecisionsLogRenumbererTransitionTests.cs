using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run.Events;
using Xunit;

namespace Hall9k.Tests.Daemon.Review;

/// <summary>
/// Covers the narrower, transition-only shape <see cref="DecisionsLogRenumberer"/> handles
/// besides its ordinary placeholder path (Decisions Log #PLACEHOLDER-6df5f975, acceptance
/// criterion 3): a branch cut before this convention
/// shipped, which chose a real number by hand at write time and now collides with an entry that
/// reached the base after this branch's own fork point. That parallel-merge shape is renumbered
/// exactly once, mechanically; every other duplicate shape is left alone for
/// <c>DecisionsLogNumberingGuardTests</c> to fail, on purpose — a hand-numbering mistake is never
/// papered over.
/// </summary>
public sealed class DecisionsLogRenumbererTransitionTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"hall9k-dlrt-{Guid.NewGuid():N}");

    public DecisionsLogRenumbererTransitionTests() => Directory.CreateDirectory(_repoPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repoPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_tail_number_colliding_with_an_entry_that_reached_base_after_the_fork_point_is_renumbered()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);

        // "As if rebased": the base independently gained its own #3 after this branch's fork
        // point, and this branch's own #3 — written by hand, before this convention shipped —
        // now sits at the tail beneath it.
        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
            "3. **This branch's own entry.** Hand-numbered before the convention shipped.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, forkPointSha, "6df5f975", CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.Renumbered);
        result.OldToken.Should().Be("3");
        result.NewNumber.Should().Be(4);

        string plan = await File.ReadAllTextAsync(Path.Combine(_repoPath, "PLAN.md"));
        plan.Should().Contain("3. **Base's own entry.**", "the base's own #3 is untouched — only this branch's own tail entry moves");
        plan.Should().Contain("4. **This branch's own entry.**");
        plan.Should().NotContain("3. **This branch's own entry.**");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance lens: ownership of a citation line must be
    /// decided against the base's own tip once this branch is current with it, never against the
    /// fork point — content the base added between the fork point and now is equally absent from
    /// the fork point as this branch's own additions are, so a fork-point filter cannot tell them
    /// apart and would rewrite the base's own citation for its own #3 to point at this branch's
    /// decision instead.
    /// </summary>
    [Fact]
    public async Task A_citation_the_base_added_after_the_fork_point_is_never_mistaken_for_this_branchs_own()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);
        File.WriteAllText(Path.Combine(_repoPath, "BaseNotes.md"), "Baseline notes, no citation yet.\n");
        await RunGitAsync(["add", "-A"]);
        await RunGitAsync(["commit", "-q", "-m", "fork point side file"]);

        // The base independently landed its own #3 AND a citation of it, after this branch's own
        // fork point — this is baseTipSha, what the branch is rebased onto.
        File.WriteAllText(
            Path.Combine(_repoPath, "BaseNotes.md"), "Baseline notes, now citing Decisions Log #3.\n");
        string baseTipSha = await CommitPlanAsync("base's own entry and citation, after the fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
        ]);

        // This branch's own rebase then replays its hand-numbered #3 and its own citation on top.
        File.WriteAllText(
            Path.Combine(_repoPath, "BranchNotes.md"), "This branch's own notes, citing Decisions Log #3.\n");
        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
            "3. **This branch's own entry.** Hand-numbered before the convention shipped.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, baseTipSha, "6df5f975", CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.Renumbered);
        result.NewNumber.Should().Be(4);

        string baseNotes = await File.ReadAllTextAsync(Path.Combine(_repoPath, "BaseNotes.md"));
        baseNotes.Should().Contain("#3", "the base's own citation, already present at baseTipSha, is not this branch's to rewrite");

        string branchNotes = await File.ReadAllTextAsync(Path.Combine(_repoPath, "BranchNotes.md"));
        branchNotes.Should().Contain("#4", "this branch's own citation, added since baseTipSha, is this branch's to rewrite");
        branchNotes.Should().NotContain("#3");
    }

    [Fact]
    public async Task A_tail_number_that_already_existed_at_the_fork_point_is_left_for_the_guard()
    {
        // Not a parallel merge: #3 was already taken when this branch was cut, so a second #3 at
        // the tail is a genuine hand-numbering mistake, not two independent additions.
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Third.** Already on the base at this branch's own fork point.",
        ]);

        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Third.** Already on the base at this branch's own fork point.",
            "3. **A hand-numbering mistake.** Reused #3 instead of picking the true next number.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, forkPointSha, "6df5f975", CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.NoActionNeeded,
            "the guard should fail this collision honestly rather than have the mechanical step paper over it");

        string plan = await File.ReadAllTextAsync(Path.Combine(_repoPath, "PLAN.md"));
        plan.Should().Contain("3. **A hand-numbering mistake.**", "left exactly as written for the guard to catch");
    }

    [Fact]
    public async Task A_colliding_number_that_is_not_at_the_tail_is_left_alone()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
        ]);

        // The duplicate (#2) sits earlier in the log; the true tail (#3) is unique on its own, so
        // there is nothing at the tail for this branch's own rebase to act on.
        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "2. **A duplicate, but not at the tail.** From the base.",
            "2. **Another copy of the same number.** Also from the base, not this branch.",
            "3. **This branch's own entry, unique.** Nothing to renumber here.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, forkPointSha, "6df5f975", CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.NoActionNeeded,
            "the tail entry (#3) is not itself a duplicate, so this branch's own rebase has nothing to renumber");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, adversarial lens: a fork point one of this rebase's
    /// own callers could not resolve (<see cref="RunRebasedOntoBase.UnreadableCommit"/>, most
    /// often <c>ReviewEngine.ResolveObservedOntoCommitAsync</c>'s own fallback after a stuck
    /// output pipe) must never reach <c>git show</c> as a literal revision — caught before any
    /// write, so the transition-shape duplicate this call cannot safely resolve is left exactly
    /// as committed, for a later attempt or the guard to catch, rather than leaving a half-applied
    /// rewrite on disk.
    /// </summary>
    [Fact]
    public async Task A_transition_shape_with_an_unreadable_fork_point_is_skipped_before_any_write()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);

        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
            "3. **This branch's own entry.** Hand-numbered before the convention shipped.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, RunRebasedOntoBase.UnreadableCommit, forkPointSha, "6df5f975",
            CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.NoActionNeeded,
            "an unresolved fork point must never be handed to git as a literal revision");

        string plan = await File.ReadAllTextAsync(Path.Combine(_repoPath, "PLAN.md"));
        plan.Should().Contain(
            "3. **This branch's own entry.**", "skipped before any write — the duplicate is left exactly as committed");
        (await RunGitCapturingAsync(["status", "--porcelain"])).Should().BeEmpty("no half-applied rewrite is ever left on disk");
    }

    /// <summary>The same guard, for an unreadable base tip — the value the citation sweep's transition branch reads.</summary>
    [Fact]
    public async Task A_transition_shape_with_an_unreadable_base_tip_is_skipped_before_any_write()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);

        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
            "3. **This branch's own entry.** Hand-numbered before the convention shipped.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, RunRebasedOntoBase.UnreadableCommit, "6df5f975",
            CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.NoActionNeeded,
            "an unresolved base tip must never be handed to git as a literal revision");
        (await RunGitCapturingAsync(["status", "--porcelain"])).Should().BeEmpty("no half-applied rewrite is ever left on disk");
    }

    /// <summary>
    /// The ordinary placeholder shape never reads either parameter, so an unreadable base tip
    /// must not block it — a blanket guard checked before the shape is known would also skip the
    /// common case for a value it never needed (caught in this same task's own self-check pass).
    /// </summary>
    [Fact]
    public async Task A_placeholder_shape_still_renumbers_even_when_the_base_tip_is_unreadable()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
        ]);

        await CommitPlanAsync("as if rebased onto base",
        [
            "1. **First.** Baseline.",
            "PLACEHOLDER-6df5f975. **This branch's own decision.** Body.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, RunRebasedOntoBase.UnreadableCommit, "6df5f975",
            CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.Renumbered);
        result.NewNumber.Should().Be(2);
    }

    private async Task<string> CommitPlanAsync(string message, IReadOnlyList<string> entries)
    {
        string path = Path.Combine(_repoPath, "PLAN.md");
        File.WriteAllLines(path, [
            "# Fixture Plan",
            "",
            "## 16. v0 Decisions Log",
            "",
            .. entries,
            "",
            "---",
            "",
            "## 17. Reference Materials",
            "",
        ]);

        if (!Directory.Exists(Path.Combine(_repoPath, ".git")))
        {
            await RunGitAsync(["init", "-q", "-b", "main"]);
            await RunGitAsync(["config", "user.email", "test@hall9k.local"]);
            await RunGitAsync(["config", "user.name", "Hall9k Test"]);
        }

        await RunGitAsync(["add", "-A"]);
        await RunGitAsync(["commit", "-q", "-m", message]);
        return (await RunGitCapturingAsync(["rev-parse", "HEAD"])).Trim();
    }

    private async Task RunGitAsync(IReadOnlyList<string> arguments) => await RunGitCapturingAsync(arguments);

    private async Task<string> RunGitCapturingAsync(IReadOnlyList<string> arguments)
    {
        ProcessResult result = await ExternalProcess.Runner("git", arguments, _repoPath, CancellationToken.None);
        result.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} must succeed: {result.StandardError}");
        return result.StandardOutput;
    }
}
