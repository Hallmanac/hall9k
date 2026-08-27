using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class ReviewPacketAssemblerTests : IDisposable
{
    private readonly string _root;
    private readonly string _repositoryPath;

    /// <summary>
    /// A repo with one commit on `main` and a `task/work` branch checked out one commit ahead of
    /// it — the shape every real dispatch reads (AGENTS.md: task branches never work directly on
    /// `main`). Individual tests add their own commits on top of `task/work`.
    /// </summary>
    public ReviewPacketAssemblerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hall9k-packet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _repositoryPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repositoryPath);
        Git(_repositoryPath, "init -q -b main");
        Commit("base.txt", "base\n", "init");
        Git(_repositoryPath, "checkout -q -b task/work");
    }

    [Fact]
    public async Task Assembles_the_diff_and_every_touched_files_full_current_text()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit("changed.cs", "class Widget { }\n", "add widget");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.RangeDescription.Should().Be("main...HEAD");
        packet.Degraded.Should().BeFalse();
        packet.TouchedFiles.Should().Contain("changed.cs");
        packet.Diff.Should().Contain("class Widget");
        packet.FileContents.Should().NotBeNull();
        packet.FileContents!["changed.cs"].Should().Be("class Widget { }\n");
    }

    [Fact]
    public async Task An_unchanged_branch_reports_an_empty_diff_and_no_touched_files()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.Diff.Should().BeEmpty();
        packet.TouchedFiles.Should().BeEmpty();
        packet.Degraded.Should().BeFalse();
    }

    /// <summary>
    /// The task's own acceptance criteria: over the cap, the packet drops file contents rather
    /// than truncating any one of them silently. The huge content is already on the merge base
    /// so the diff itself stays small — it is the file's own current text on disk, not the diff,
    /// that pushes the packet over its cap; <see cref="Diff_alone_over_the_cap_ships_no_packet"/>
    /// covers the diff-itself-too-large case.
    /// </summary>
    [Fact]
    public async Task Degrades_to_diff_and_file_list_when_the_packet_would_exceed_its_size_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        // Many lines, not one enormous line: appending a single line below then diffs as a few
        // bytes rather than replacing the whole huge line, which is what keeps the diff itself
        // small while the file's current text alone still exceeds the cap.
        string huge = string.Join('\n', Enumerable.Repeat(new string('a', 40), 9_000)) + "\n";
        Git(_repositoryPath, "checkout -q main");
        Commit("huge.txt", huge, "add a file bigger than the packet cap");
        // Rebuilds task/work on top of main's new tip, so the huge content is already on the
        // merge base and only the small change below shows up in the three-dot diff.
        Git(_repositoryPath, "checkout -q -B task/work");
        Commit("huge.txt", huge + "changed\n", "touch the huge file");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.Degraded.Should().BeTrue();
        packet.FileContents.Should().BeNull("the platform never truncates a file's content silently");
        packet.TouchedFiles.Should().Contain("huge.txt", "the file list still names what changed");
        packet.Diff.Should().NotBeNullOrEmpty("the diff itself is never dropped, only the full file text");
    }

    /// <summary>
    /// The packet's size ceiling bounds the diff itself, not only the touched files' text
    /// (conformance and adversarial review, cycle 1): a diff that alone exceeds
    /// <see cref="ReviewPacketAssembler.MaxPacketBytes"/> ships no packet at all rather than
    /// embedding it whole, so the caller falls back to the reviewer's own `git diff`.
    /// </summary>
    [Fact]
    public async Task Diff_alone_over_the_cap_ships_no_packet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string huge = new('a', (int)ReviewPacketAssembler.MaxPacketBytes + 50_000);
        Commit("huge.txt", huge, "add a file bigger than the packet cap");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().BeNull("the diff alone already breaks the packet's own size ceiling");
    }

    /// <summary>
    /// A binary touched file is never decoded as UTF-8 just to measure or embed it (adversarial
    /// review, cycle 1): its content is silently omitted, the same treatment a deleted file's
    /// missing text already gets, rather than filling the packet with replacement-character
    /// noise.
    /// </summary>
    [Fact]
    public async Task A_binary_touched_file_is_skipped_rather_than_decoded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02];
        File.WriteAllBytes(Path.Combine(_repositoryPath, "asset.png"), binary);
        Git(_repositoryPath, "add -A");
        Git(_repositoryPath, "-c user.name=Test -c user.email=test@test commit -q -m \"add binary asset\"");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.Degraded.Should().BeFalse();
        packet.TouchedFiles.Should().Contain("asset.png");
        packet.FileContents.Should().NotBeNull();
        packet.FileContents!.Should().NotContainKey("asset.png", "a binary file has no meaningful text to embed");
    }

    /// <summary>
    /// A binary file whose on-disk length alone exceeds the packet's remaining budget must not
    /// degrade the packet: it was never going to be embedded regardless of size, so its length
    /// must never be charged against the budget in the first place (cycle-2 conformance and
    /// adversarial review — the binary check used to run *after* the size check, so an oversized
    /// binary file tripped the degrade path before the code ever learned the file was binary).
    /// </summary>
    [Fact]
    public async Task An_oversized_binary_touched_file_does_not_degrade_the_packet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit("small.cs", "class Widget { }\n", "add a small text file");
        byte[] binary = new byte[ReviewPacketAssembler.MaxPacketBytes + 50_000]; // all-zero bytes trip the NUL heuristic
        File.WriteAllBytes(Path.Combine(_repositoryPath, "asset.bin"), binary);
        Git(_repositoryPath, "add -A");
        Git(_repositoryPath, "-c user.name=Test -c user.email=test@test commit -q -m \"add oversized binary asset\"");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.Degraded.Should().BeFalse("the oversized file is binary and was never going to be embedded");
        packet.TouchedFiles.Should().Contain("asset.bin");
        packet.FileContents.Should().NotBeNull();
        packet.FileContents!.Should().NotContainKey("asset.bin");
        packet.FileContents!["small.cs"].Should().Be("class Widget { }\n");
    }

    /// <summary>
    /// A task worktree is cut with `--no-track` off `origin/{baseBranch}` (AGENTS.md), so it may
    /// carry no local ref of the base branch's name at all — the same fallback the review
    /// prompt's own prose already states.
    /// </summary>
    [Fact]
    public async Task Falls_back_to_the_origin_ref_when_the_local_base_branch_is_absent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string originPath = Path.Combine(_root, "origin.git");
        Git(_root, $"init -q --bare -b main \"{originPath}\"");
        Git(_repositoryPath, $"remote add origin \"{originPath}\"");
        Git(_repositoryPath, "push -q origin main");
        Commit("feature.txt", "feature\n", "add feature file");
        // Mirrors a --no-track worktree: only origin/main exists, no local main ref at all.
        Git(_repositoryPath, "branch -D main");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.RangeDescription.Should().Be("origin/main...HEAD");
        packet.TouchedFiles.Should().Contain("feature.txt");
    }

    /// <summary>
    /// A task worktree shares its local base-branch ref with the project home's `dev/` worktree
    /// (AGENTS.md), so that ref is routinely stale relative to the task's actual base while
    /// `origin/{baseBranch}` stays current — the remote-tracking-preferred convention
    /// <c>VerificationRunner.CountBranchCommitsAsync</c> already follows (conformance review,
    /// cycle 1). A present-but-stale local ref must not win over a fresher origin one.
    /// </summary>
    [Fact]
    public async Task Prefers_the_origin_ref_over_a_present_but_stale_local_base_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string originPath = Path.Combine(_root, "origin.git");
        Git(_root, $"init -q --bare -b main \"{originPath}\"");
        Git(_repositoryPath, $"remote add origin \"{originPath}\"");
        Git(_repositoryPath, "push -q origin main");
        Commit("feature.txt", "feature\n", "add feature file");
        // origin/main moves ahead while the local main ref (shared with dev/) stays behind —
        // the exact staleness AGENTS.md's project-home shape produces in a real task worktree.
        Git(_repositoryPath, "checkout -q main");
        Commit("upstream.txt", "upstream\n", "advance main independently");
        Git(_repositoryPath, "push -q origin main");
        Git(_repositoryPath, "checkout -q task/work");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.RangeDescription.Should().Be("origin/main...HEAD");
        packet.TouchedFiles.Should().Contain("feature.txt");
        packet.TouchedFiles.Should().NotContain("upstream.txt", "upstream.txt is on origin/main itself, not part of this task's diff");
    }

    [Fact]
    public async Task A_sinceSha_range_reads_only_the_delta_since_that_commit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit("a.txt", "a\n", "add a");
        string sinceSha = TryGit(_repositoryPath, "rev-parse HEAD").Output.Trim();
        Commit("b.txt", "b\n", "add b");
        Commit("c.txt", "c\n", "add c");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha, cts.Token);

        packet.Should().NotBeNull();
        packet!.RangeDescription.Should().Be($"{sinceSha}..HEAD");
        packet.TouchedFiles.Should().BeEquivalentTo(["b.txt", "c.txt"]);
        packet.TouchedFiles.Should().NotContain("a.txt", "a.txt already existed as of the since-commit");
    }

    [Fact]
    public async Task A_file_deleted_relative_to_the_base_branch_is_named_but_its_content_is_never_read()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        // doomed.txt has to exist on main for removing it on the task branch to read as a
        // deletion in the diff against main; adding-then-removing on the same branch would
        // leave no trace of the file in either tree.
        Git(_repositoryPath, "checkout -q main");
        Commit("doomed.txt", "will be deleted\n", "add doomed file to main");
        Git(_repositoryPath, "checkout -q -b task/remove-doomed");
        Git(_repositoryPath, "rm -q doomed.txt");
        Git(_repositoryPath, "-c user.name=Test -c user.email=test@test commit -q -m \"remove doomed file\"");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.TouchedFiles.Should().Contain("doomed.txt");
        packet.FileContents.Should().NotBeNull();
        packet.FileContents!.Should().NotContainKey("doomed.txt", "there is no current text on disk to embed");
    }

    /// <summary>
    /// With `core.quotePath` at its default, git C-quotes a non-ASCII path in `--name-only`
    /// output; without `-z` the touched-files list would carry the escaped, unopenable literal
    /// and the file's text would be silently skipped as though it were deleted (conformance
    /// review, cycle 1). `VerificationRunner.ListUncommittedFilesAsync` already solved this for
    /// `git status` the same way.
    /// </summary>
    [Fact]
    public async Task A_non_ascii_file_name_is_read_correctly_rather_than_dropped()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit("café.cs", "class Cafe { }\n", "add café.cs");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.TouchedFiles.Should().Contain("café.cs");
        packet.FileContents.Should().NotBeNull();
        packet.FileContents!["café.cs"].Should().Be("class Cafe { }\n");
    }

    /// <summary>
    /// The packet's own "full current text ... unless noted otherwise" promise (cycle-3 conformance
    /// and adversarial review) is honest only if a file it could not embed is actually named and
    /// why: a deleted file has no text on disk, a binary file is never decoded. Both land in the
    /// same packet here so the omission list carries both reasons at once, not just whichever one
    /// a narrower test happens to exercise.
    /// </summary>
    [Fact]
    public async Task Records_which_touched_files_were_omitted_and_why()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Git(_repositoryPath, "checkout -q main");
        Commit("doomed.txt", "will be deleted\n", "add doomed file to main");
        Git(_repositoryPath, "checkout -q -b task/remove-and-add-binary");
        Git(_repositoryPath, "rm -q doomed.txt");
        byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02];
        File.WriteAllBytes(Path.Combine(_repositoryPath, "asset.png"), binary);
        Git(_repositoryPath, "add -A");
        Git(_repositoryPath, "-c user.name=Test -c user.email=test@test commit -q -m \"remove doomed file, add binary asset\"");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.Degraded.Should().BeFalse();
        packet.Omissions.Should().BeEquivalentTo(
        [
            new FileOmission("doomed.txt", FileOmissionReason.Deleted),
            new FileOmission("asset.png", FileOmissionReason.Binary),
        ]);
    }

    [Fact]
    public async Task An_unobservable_worktree_returns_no_packet_rather_than_a_guess()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string missing = Path.Combine(_root, "does-not-exist");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(missing, "main", sinceSha: null, cts.Token);

        packet.Should().BeNull();
    }

    private void Commit(string fileName, string content, string message)
    {
        File.WriteAllText(Path.Combine(_repositoryPath, fileName), content);
        Git(_repositoryPath, "add -A");
        Git(_repositoryPath, $"-c user.name=Test -c user.email=test@test commit -q -m \"{message}\"");
    }

    private static void Git(string workingDirectory, string arguments)
    {
        (int exitCode, string output) = TryGit(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    private static (int ExitCode, string Output) TryGit(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup, same as GitWorktreeManagerTests: a locked pack file on some
            // platforms is not worth failing the test run over.
        }
    }
}
