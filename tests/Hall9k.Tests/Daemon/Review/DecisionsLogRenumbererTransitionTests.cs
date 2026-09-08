using System.Text.RegularExpressions;
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

    /// <summary>
    /// Independent pre-PR review, cycle 6, adversarial lens (DecisionsLogRenumberer.cs:389): an
    /// entry renumbered once already carries its own placement note in its body — carried
    /// forward untouched by the heading rewrite — and that note's own prose can mention the very
    /// number a second collision is about to reassign away from. If <c>FindPlacementNoteLines</c>
    /// ever failed to exclude it, the citation sweep would rewrite the FIRST renumbering's own
    /// historical record to claim it assigned the number the SECOND renumbering actually picked,
    /// falsifying what the mechanical step actually did.
    /// </summary>
    [Fact]
    public async Task A_prior_renumbering_placement_note_is_never_rewritten_by_a_later_renumbering()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);

        // This branch's own entry already carries a placement note from an earlier renumbering
        // pass (the ordinary placeholder shape, assigned #3) — carried forward, untouched, by
        // RewriteHeadingPreservingBody. The base then independently landed its own #3 after this
        // branch's fork point, so this branch's own #3 collides again and must be renumbered a
        // second time, this time via the transition shape.
        await CommitPlanAsync("as if rebased onto base a second time",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
            "3. **This branch's own entry.** Already renumbered once before.",
            "",
            "> Renumbering placement note: this entry was appended under placeholder",
            "> `PLACEHOLDER-6df5f975` and assigned **#3** by the mechanical pre-final-pass",
            "> rebase step — the log's next free number once this branch was rebased onto its base.",
            "> Every citation of the placeholder elsewhere in this repository was rewritten to",
            "> `#3` in the same commit.",
        ]);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, forkPointSha, "6df5f975", CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.Renumbered);
        result.OldToken.Should().Be("3");
        result.NewNumber.Should().Be(4);

        string plan = await File.ReadAllTextAsync(Path.Combine(_repoPath, "PLAN.md"));
        plan.Should().Contain("4. **This branch's own entry.**");
        plan.Should().NotContain("3. **This branch's own entry.**");
        plan.Should().Contain(
            "assigned **#3** by the mechanical pre-final-pass",
            "the FIRST renumbering's own historical note must survive the SECOND renumbering's citation sweep untouched");
        plan.Should().NotContain(
            "assigned **#4** by the mechanical pre-final-pass",
            "the sweep must never rewrite the first note's own historical number to the second renumbering's new one");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens: the citation sweep rewrites every
    /// `#&lt;oldNumber&gt;` occurrence on a line this branch added, with no check that the
    /// occurrence is actually a Decisions Log citation — this repository's own number space (pull
    /// request numbers) can collide with a Decisions Log entry's own number by pure coincidence.
    /// </summary>
    [Fact]
    public async Task A_number_that_merely_shares_digits_with_a_citation_is_never_rewritten()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);
        File.WriteAllText(Path.Combine(_repoPath, "BaseNotes.md"), "Baseline notes, no citation yet.\n");
        await RunGitAsync(["add", "-A"]);
        await RunGitAsync(["commit", "-q", "-m", "fork point side file"]);

        string baseTipSha = await CommitPlanAsync("base's own entry, after the fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
        ]);

        // This branch's own rebase replays its hand-numbered #3 alongside a genuine citation of it
        // AND a line whose own "#3" is an unrelated pull request reference sharing the same
        // digits, not a Decisions Log citation at all.
        File.WriteAllText(
            Path.Combine(_repoPath, "BranchNotes.md"),
            "This branch's own notes, citing Decisions Log #3.\n"
            + "Origin incident (2026-08-17, PR #3): unrelated, not a citation.\n");
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

        string branchNotes = await File.ReadAllTextAsync(Path.Combine(_repoPath, "BranchNotes.md"));
        branchNotes.Should().Contain("citing Decisions Log #4.", "the genuine citation is rewritten");
        branchNotes.Should().Contain(
            "PR #3", "a bare digit match outside a Decisions Log citation form is never rewritten");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance lens: the ownership filter used to decide
    /// "was this line already at the base tip" by whole-line string membership in a HashSet, so a
    /// branch-added citation byte-identical to an existing base-tip line was silently excused as
    /// "already there" and left unrewritten. Counting occurrences (a multiset) instead of merely
    /// testing membership is what tells a branch's own THIRD identical line apart from the base's
    /// own two.
    /// </summary>
    [Fact]
    public async Task A_branch_added_citation_byte_identical_to_an_existing_base_line_is_still_rewritten()
    {
        string forkPointSha = await CommitPlanAsync("fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
        ]);
        File.WriteAllText(Path.Combine(_repoPath, "Notes.md"), "See Decisions Log #3 for background.\n");
        await RunGitAsync(["add", "-A"]);
        await RunGitAsync(["commit", "-q", "-m", "fork point side file"]);

        // The base independently lands its own #3 and, elsewhere in the same file, a SECOND
        // occurrence of the exact same citation line.
        File.WriteAllText(
            Path.Combine(_repoPath, "Notes.md"),
            "See Decisions Log #3 for background.\n"
            + "See Decisions Log #3 for background.\n");
        string baseTipSha = await CommitPlanAsync("base's own entry and citation, after the fork point",
        [
            "1. **First.** Baseline.",
            "2. **Second.** Baseline.",
            "3. **Base's own entry.** Landed on the base after this branch's fork point.",
        ]);

        // This branch's own rebase replays the base's two lines untouched and adds a THIRD,
        // byte-identical line of its own — its own citation of its own hand-numbered #3, which
        // just happens to read exactly like the base's own existing citation.
        File.WriteAllText(
            Path.Combine(_repoPath, "Notes.md"),
            "See Decisions Log #3 for background.\n"
            + "See Decisions Log #3 for background.\n"
            + "See Decisions Log #3 for background.\n");
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

        string notes = await File.ReadAllTextAsync(Path.Combine(_repoPath, "Notes.md"));
        int oldCitationCount = Regex.Matches(notes, @"(?<!\d)#3(?!\d)").Count;
        int newCitationCount = Regex.Matches(notes, @"(?<!\d)#4(?!\d)").Count;
        oldCitationCount.Should().Be(
            2, "the base's own two citations, already present at baseTipSha, are not this branch's to rewrite");
        newCitationCount.Should().Be(
            1,
            "exactly one line is this branch's own addition — a HashSet-based ownership check "
            + "would have excused it too, as already present at the base tip");
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
