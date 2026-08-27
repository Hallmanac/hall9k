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
    /// than truncating any one of them silently.
    /// </summary>
    [Fact]
    public async Task Degrades_to_diff_and_file_list_when_the_packet_would_exceed_its_size_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string huge = new('a', (int)ReviewPacketAssembler.MaxPacketBytes + 50_000);
        Commit("huge.txt", huge, "add a file bigger than the packet cap");

        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            _repositoryPath, "main", sinceSha: null, cts.Token);

        packet.Should().NotBeNull();
        packet!.Degraded.Should().BeTrue();
        packet.FileContents.Should().BeNull("the platform never truncates a file's content silently");
        packet.TouchedFiles.Should().Contain("huge.txt", "the file list still names what changed");
        packet.Diff.Should().NotBeNullOrEmpty("the diff itself is never dropped, only the full file text");
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
