using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="ContainerGateDirectory"/> is the daemon's own read of the shared, machine-wide
/// directory the test project's <c>CrossProcessContainerGate</c> polls for Postgres container
/// permits: the format contract for a holder's own sidecar, and the excerpt
/// <c>VerificationRunner</c> appends to a killed gate's own timeout failure text naming who held
/// the permits (task: a killed gate names who held the permits). Every test here works against a
/// fabricated temp directory and a fake <see cref="ContainerGateDirectory.LivenessProbe"/> — no
/// test starts a container, spawns a process, or kills one.
/// </summary>
public sealed class ContainerGateDirectoryTests
{
    [Fact]
    public void FormatSidecar_round_trips_through_ParseSidecar()
    {
        DateTimeOffset startedAt = new(2026, 9, 20, 10, 22, 0, TimeSpan.Zero);
        DateTimeOffset acquiredAt = new(2026, 9, 20, 10, 22, 1, TimeSpan.Zero);

        string content = ContainerGateDirectory.FormatSidecar(4821, startedAt, "/repo/wt-abc12345", acquiredAt);
        ContainerGateDirectory.SidecarHolder holder = ContainerGateDirectory.ParseSidecar(content);

        holder.ProcessId.Should().Be(4821);
        holder.ProcessStartTimeUtc.Should().Be(startedAt);
        holder.WorkingDirectory.Should().Be("/repo/wt-abc12345");
        holder.AcquiredAtUtc.Should().Be(acquiredAt);
    }

    [Fact]
    public void ParseSidecar_never_throws_on_content_it_does_not_recognize()
    {
        ContainerGateDirectory.SidecarHolder holder = ContainerGateDirectory.ParseSidecar("not a sidecar at all\njust prose");

        holder.ProcessId.Should().BeNull();
        holder.ProcessStartTimeUtc.Should().BeNull();
        holder.WorkingDirectory.Should().BeNull();
        holder.AcquiredAtUtc.Should().BeNull();
    }

    [Fact]
    public void DescribeContents_is_null_for_a_directory_that_does_not_exist() =>
        ContainerGateDirectory.DescribeContents(Path.Combine(Path.GetTempPath(), $"h9k-no-such-dir-{Guid.NewGuid():N}"))
            .Should().BeNull();

    [Fact]
    public void DescribeContents_is_null_for_an_empty_directory()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            ContainerGateDirectory.DescribeContents(directory).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The excerpt names a wait file's own embedded pid, marking it stale when the fake probe
    /// says that pid is no longer alive — a killed gate's own timeout text should be able to tell
    /// a human whether a queued class was genuinely still contending or just a leftover from a
    /// process that already died.
    /// </summary>
    [Fact]
    public void DescribeContents_lists_a_wait_file_and_marks_it_stale_when_the_probe_says_so()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "waiting-4821-abc123.txt"), "irrelevant diagnostic text");

            string? excerpt = ContainerGateDirectory.DescribeContents(
                directory, (processId, _) => processId != 4821);

            excerpt.Should().NotBeNull();
            excerpt.Should().Contain("1 wait file(s)");
            excerpt.Should().Contain("waiting-4821-abc123.txt");
            excerpt.Should().Contain("pid 4821");
            excerpt.Should().Contain("stale");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DescribeContents_lists_a_wait_file_without_stale_when_the_probe_says_it_is_alive()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "waiting-4821-abc123.txt"), "irrelevant diagnostic text");

            string? excerpt = ContainerGateDirectory.DescribeContents(directory, (_, _) => true);

            excerpt.Should().NotBeNull();
            excerpt.Should().Contain("pid 4821");
            excerpt.Should().NotContain("stale");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A holder sidecar names who held the permit — its own pid and working directory, the
    /// worktree (and so the run or session) that acquired it — exactly the fact this task exists
    /// to surface at a kill.
    /// </summary>
    [Fact]
    public void DescribeContents_names_a_holder_sidecars_process_and_working_directory()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            DateTimeOffset startedAt = new(2026, 9, 20, 10, 22, 0, TimeSpan.Zero);
            DateTimeOffset acquiredAt = new(2026, 9, 20, 10, 22, 1, TimeSpan.Zero);
            File.WriteAllText(
                Path.Combine(directory, "permit-0.lock.holder"),
                ContainerGateDirectory.FormatSidecar(9001, startedAt, "/repo/wt-57e538ee", acquiredAt));

            string? excerpt = ContainerGateDirectory.DescribeContents(directory, (_, _) => true);

            excerpt.Should().NotBeNull();
            excerpt.Should().Contain("1 holder sidecar(s)");
            excerpt.Should().Contain("permit-0.lock.holder");
            excerpt.Should().Contain("pid 9001");
            excerpt.Should().Contain("/repo/wt-57e538ee");
            excerpt.Should().NotContain("stale");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A holder sidecar left behind by a process that was killed hard enough to skip its own
    /// release path is read as stale by the identical liveness probe the dead-waiter sweep uses —
    /// the same check, two readers.
    /// </summary>
    [Fact]
    public void DescribeContents_marks_a_holder_sidecar_stale_when_its_process_is_dead()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            DateTimeOffset startedAt = new(2026, 9, 20, 10, 22, 0, TimeSpan.Zero);
            File.WriteAllText(
                Path.Combine(directory, "permit-0.lock.holder"),
                ContainerGateDirectory.FormatSidecar(9001, startedAt, "/repo/wt-57e538ee", startedAt));

            string? excerpt = ContainerGateDirectory.DescribeContents(directory, (_, _) => false);

            excerpt.Should().Contain("pid 9001, stale");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The origin incident found 25 stale wait files in one directory; this method's own doc
    /// comment promises "a short, human-readable listing" regardless of how many accumulate, so
    /// the excerpt truncates rather than growing without bound (independent pre-PR review, this
    /// cycle).
    /// </summary>
    [Fact]
    public void DescribeContents_truncates_a_long_wait_file_listing_with_a_count_of_the_rest()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            for (int i = 0; i < 12; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"waiting-{1000 + i}-a.txt"), "irrelevant");
            }

            string? excerpt = ContainerGateDirectory.DescribeContents(directory, (_, _) => true);

            excerpt.Should().NotBeNull();
            excerpt.Should().Contain("12 wait file(s)");
            excerpt.Should().Contain("+4 more");
            excerpt.Should().NotContain("waiting-1011-a.txt");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DescribeContents_names_both_kinds_at_once()
    {
        string directory = Directory.CreateTempSubdirectory("h9k-gate-dir-test-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(directory, "waiting-100-a.txt"), "irrelevant");
            File.WriteAllText(
                Path.Combine(directory, "permit-1.lock.holder"),
                ContainerGateDirectory.FormatSidecar(200, now, "/repo/wt-other", now));

            string? excerpt = ContainerGateDirectory.DescribeContents(directory, (_, _) => true);

            excerpt.Should().Contain("1 wait file(s)");
            excerpt.Should().Contain("1 holder sidecar(s)");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
