using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Where a run's directory goes (ruled 2026-08-23, backlog 49): under the owning task's own
/// directory when the project has a home — there is no top-level runs/ in the home — and the
/// platform-global location otherwise. Resolved once, at dispatch, and recorded on
/// RunDispatched; see <see cref="Hall9k.Domain.Features.Run.RunAggregate"/> and
/// <see cref="Hall9k.Domain.Features.Run.Projections.RunDetails"/> for the fallback a stream
/// written before this existed replays through.
/// </summary>
public sealed class RunPathsTests
{
    [Fact]
    public void With_no_home_a_new_run_falls_back_to_the_platform_global_location()
    {
        Guid runId = DomainId.New();

        RunPaths.ResolveDirectory(ProjectHome.None, "abc12345-some-task", runId)
            .Should().Be(RunPaths.GlobalDirectory(runId), "a project with no home has nowhere else to put it");
    }

    [Fact]
    public void With_a_home_a_new_run_lands_under_its_owning_tasks_own_directory()
    {
        Guid runId = DomainId.New();
        ProjectHome home = ProjectHome.Parse(Path.Combine(Path.GetTempPath(), "hall9k-runpaths-home"));

        string directory = RunPaths.ResolveDirectory(home, "98ac05ef-project-home", runId);

        directory.Should().Be(
            Path.Combine(home.Value, "tasks", "98ac05ef-project-home", "runs", runId.ToString()),
            "the task directory is the whole story of the task, and that includes every run");
    }

    [Fact]
    public void The_global_directory_is_keyed_by_the_run_id_alone()
    {
        Guid runId = DomainId.New();

        RunPaths.GlobalDirectory(runId).Should().Be(Path.Combine(RunPaths.Root, "runs", runId.ToString()));
    }

    // ResolveCurrentDirectory (backlog 51 cycle 1): RunDispatched.RunDirectory is recorded once
    // and never updated, but the render sweep relocates a task's whole directory into or out of
    // tasks/_archive/ as it crosses the terminal boundary, carrying every run underneath it.
    public sealed class ResolveCurrentDirectoryTests : IDisposable
    {
        private readonly string _home = Directory.CreateTempSubdirectory("hall9k-runpaths-resolve-").FullName;

        [Fact]
        public void A_directory_that_still_exists_at_the_recorded_path_is_returned_unchanged()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-still-live", "runs", "some-run");
            Directory.CreateDirectory(recorded);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(recorded);
        }

        [Fact]
        public void A_run_whose_task_archived_resolves_to_the_directory_under_tasks_archive()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-closed-out", "runs", "some-run");
            string actual = Path.Combine(_home, "tasks", "_archive", "abc12345-closed-out", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the task's whole directory moved into tasks/_archive/ on a later sweep, taking this run with it");
        }

        [Fact]
        public void A_run_whose_task_reopened_resolves_to_the_directory_back_under_tasks()
        {
            string recorded = Path.Combine(_home, "tasks", "_archive", "abc12345-reopened", "runs", "some-run");
            string actual = Path.Combine(_home, "tasks", "abc12345-reopened", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the task moved back out of tasks/_archive/ once it was reopened, taking this run with it");
        }

        [Fact]
        public void A_brand_new_run_whose_task_moved_before_the_run_leaf_was_ever_created_still_resolves()
        {
            // Adversarial review, backlog 51 cycle 8: a run just dispatched has no leaf directory
            // on either side yet — ClaudeExecutor creates it only after this call returns — so a
            // task directory relocated by the render sweep between RunLauncher resolving this
            // path and ClaudeExecutor creating the leaf must still be found by the task directory
            // alone existing, not by the (necessarily absent) run leaf.
            string recorded = Path.Combine(_home, "tasks", "_archive", "abc12345-reopened", "runs", "brand-new-run");
            string actualTaskDirectory = Path.Combine(_home, "tasks", "abc12345-reopened");
            Directory.CreateDirectory(actualTaskDirectory);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(
                Path.Combine(actualTaskDirectory, "runs", "brand-new-run"),
                "the task directory already moved back out of tasks/_archive/, even though this run's own leaf was never created on either side");
        }

        [Fact]
        public void Neither_the_recorded_nor_the_alternate_path_existing_falls_back_to_the_recorded_path()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-gone", "runs", "some-run");

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(recorded,
                "a caller with neither location on disk (a foreign machine) keeps its existing not-found handling");
        }

        [Fact]
        public void A_slug_changing_revise_that_left_the_task_live_still_resolves()
        {
            // A rename is not an archive flip: the task never crossed the tasks/_archive/
            // boundary, so a fallback that only tries flipping that segment never finds it.
            string recorded = Path.Combine(_home, "tasks", "abc12345-old-objective", "runs", "some-run");
            string actual = Path.Combine(_home, "tasks", "abc12345-revised-objective", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the task directory was renamed onto its revised slug, same root, same short id");
        }

        [Fact]
        public void A_slug_changing_revise_combined_with_an_archive_flip_still_resolves()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-old-objective", "runs", "some-run");
            string actual = Path.Combine(
                _home, "tasks", "_archive", "abc12345-revised-objective", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the task both renamed and archived since the run's directory was recorded");
        }

        [Fact]
        public void A_short_id_prefix_matching_two_directories_falls_back_to_the_recorded_path()
        {
            // With no full task id to confirm a candidate against, an ambiguous prefix match is
            // exactly as unresolved as no match at all — guessing between the two would risk
            // resolving to a different task's directory entirely.
            string recorded = Path.Combine(_home, "tasks", "abc12345-old-objective", "runs", "some-run");
            Directory.CreateDirectory(Path.Combine(_home, "tasks", "abc12345-revised-objective"));
            Directory.CreateDirectory(Path.Combine(_home, "tasks", "abc12345-a-different-task-entirely"));

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(recorded,
                "two directories share this short-id prefix, so neither can be trusted as the match");
        }

        [Fact]
        public void A_home_whose_own_path_contains_a_tasks_segment_still_resolves_the_tasks_own_root()
        {
            // Adversarial review, backlog 51 cycle 2: a project home is an arbitrary path a human
            // names (h9k project add --home), so it can itself contain a "tasks" segment. The
            // task's own tasks/ root is always the LAST such segment before the task's own
            // <shortid>-<slug> directory, so the fallback must search from the end of the path.
            string home = Path.Combine(_home, "tasks", "hall9k");
            string recorded = Path.Combine(home, "tasks", "abc12345-closed-out", "runs", "some-run");
            string actual = Path.Combine(home, "tasks", "_archive", "abc12345-closed-out", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the first '/tasks/' in the path belongs to the home, not the task's own root");
        }

        [Fact]
        public void A_home_whose_own_path_contains_a_tasks_archive_segment_still_resolves_the_tasks_own_root()
        {
            // Adversarial review, backlog 51 cycle 7: LastIndexOf found the HOME's own
            // "/tasks/_archive/" segment first and stripped that instead of the task's own one,
            // silently returning the stale recorded path. Anchoring by position from the end of
            // the path (four or five trailing segments) rather than searching the string for a
            // literal match sidesteps the home's own path contents entirely.
            string home = Path.Combine(_home, "tasks", "_archive", "hall9k");
            string recorded = Path.Combine(home, "tasks", "abc12345-closed-out", "runs", "some-run");
            string actual = Path.Combine(home, "tasks", "_archive", "abc12345-closed-out", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the home's own '/tasks/_archive/' segment must not be mistaken for the task's own root");
        }

        public void Dispose() => Directory.Delete(_home, recursive: true);
    }
}
